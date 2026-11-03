#if !PHOTOGRAPHEREYE_NO_GPU
using System;
using System.Buffers;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComputeSharp;

namespace ImgViewer.Imaging;

/// <summary>
/// Live preview on the GPU: the linear source stays resident on the device (uploaded once per buffer), exposure /
/// white balance gain runs as a kernel, and the result is packed to dithered BGRA8 with the histogram counted on the
/// GPU, so only 4 bytes per pixel come back. The denoised planes are kept while only later settings change.
/// </summary>
public static partial class Adjust16
{
    /// <summary>Smallest buffer worth the GPU preview (below it the CPU path is as fast).</summary>
    private const int GpuDisplayMinPixels = 250_000;

    private static Raw16Image? _resSrc;
    private static ReadWriteBuffer<int>? _resR, _resG, _resB;
    private static ReadWriteBuffer<int>? _outBuf;
    private static ReadWriteBuffer<int>? _histBuf;
    private static ReadOnlyBuffer<float>? _bayerBuf;

    private static ReadWriteBuffer<int>? _nrR, _nrG, _nrB;
    private static (Raw16Image Src, float Gr, float Gg, float Gb, double Luma, double Color)? _nrKey;

    /// <summary>Diagnostics for tests: uploads of a source and denoise passes reused from the cache.</summary>
    public static int GpuDisplayUploads { get; private set; }
    public static int GpuDisplayDenoiseReuse { get; private set; }

    /// <summary>
    /// Gain (2^ev x white balance of <paramref name="temperature"/>/<paramref name="tint"/>) plus the Adjust16 passes
    /// on <paramref name="linear"/>, returned as a frozen BGRA8 bitmap with the 16-bit histogram; null when the GPU
    /// cannot do it (caller falls back to <see cref="LinearRender.ApplyGain(Raw16Image, double, double, double, CancellationToken)"/> +
    /// <see cref="Apply"/> + <see cref="Raw16Image.ToDisplay"/>). <paramref name="extra"/> must not carry exposure/temperature.
    /// </summary>
    public static BitmapSource? TryRenderDisplayGpu(Raw16Image linear, double ev, double temperature, double tint,
        double shadowLift, double gamma, ManualStretchMap? manual, out HistogramData? histogram, MonoMode mono = MonoMode.None,
        int grainSeed = 0, PhotoAdjustments? extra = null, CancellationToken ct = default, CameraToneCurve? cameraTone = null,
        bool dither = true, int minPixels = GpuDisplayMinPixels)
    {
        histogram = null;
        if (!GpuEnabled) return null;
        int w = linear.W, h = linear.H;
        long n = (long)w * h;
        if (w <= 0 || h <= 0 || n < minPixels || n > int.MaxValue / 4) return null;
        if (!EnsureGpu()) return null;

        lock (GpuLock)
        {
            try
            {
                return RunGpuDisplay(linear, w, h, (int)n, ev, temperature, tint, shadowLift, gamma, manual, out histogram,
                    mono, grainSeed, extra, ct, cameraTone, dither);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.Log($"GPU DISPLAY failed: {ex.GetType().Name} {ex.Message}; fallback CPU");
                ReleaseDisplayBuffers();
                histogram = null;
                return null;
            }
        }
    }

    private static BitmapSource RunGpuDisplay(Raw16Image src, int w, int h, int n, double ev, double temperature, double tint,
        double shadowLift, double gamma, ManualStretchMap? manual, out HistogramData? histogram, MonoMode mono, int grainSeed,
        PhotoAdjustments? extra, CancellationToken ct, CameraToneCurve? cameraTone, bool dither)
    {
        var d = _gpu!;
        EnsureBufs(d, n);
        var b = _bufs!;
        EnsureResident(d, src, n);
        ct.ThrowIfCancellationRequested();

        var (gr, gg, gb) = LinearRender.Gains(ev, temperature, tint);
        bool denoise = extra is not null && !extra.IsDefault && NoiseReduction.Active(extra.Denoise, extra.ColorNoise);
        bool denoised = false;
        if (denoise)
        {
            denoised = TryReuseDenoise(d, b, w, h, n, src, gr, gg, gb, extra!.Denoise, extra.ColorNoise);
            if (!denoised)
            {
                d.For(w, h, new GainKernel(_resR!, _resG!, _resB!, b.R, b.G, b.B, gr, gg, gb, w, h));
                Denoise(d, b, w, h, extra.Denoise, extra.ColorNoise, ct);
                StoreDenoise(d, b, w, h, n, src, gr, gg, gb, extra.Denoise, extra.ColorNoise);
                denoised = true;
            }
        }
        else
        {
            d.For(w, h, new GainKernel(_resR!, _resG!, _resB!, b.R, b.G, b.B, gr, gg, gb, w, h));
        }
        ct.ThrowIfCancellationRequested();

        RunStages(d, b, w, h, shadowLift, gamma, manual, mono, grainSeed, extra, ct, cameraTone, skipDenoise: denoised);
        ct.ThrowIfCancellationRequested();

        if (_outBuf is null || _outBuf.Length != n)
        {
            _outBuf?.Dispose();
            _outBuf = d.AllocateReadWriteBuffer<int>(n);
        }
        _histBuf ??= d.AllocateReadWriteBuffer<int>(1024);
        _bayerBuf ??= d.AllocateReadOnlyBuffer(Raw16Image.Bayer);
        d.For(1024, 1, new ClearKernel(_histBuf, 1024));
        d.For(w, h, new PackKernel(b.R, b.G, b.B, _outBuf, _histBuf, _bayerBuf, dither ? 1 : 0, w, h));

        var hist = new int[1024];
        _histBuf.CopyTo(hist);
        histogram = new HistogramData(hist[..256], hist[256..512], hist[512..768], hist[768..]);

        int[] px = ArrayPool<int>.Shared.Rent(n);
        try
        {
            _outBuf.CopyTo(px.AsSpan(0, n));
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(px);
        }
    }

    /// <summary>Puts the cached denoised planes into b.R/G/B when they were made from the same source, gain and strength.</summary>
    private static bool TryReuseDenoise(GraphicsDevice d, GpuBufs b, int w, int h, int n, Raw16Image src, float gr, float gg, float gb, double luma, double color)
    {
        if (_nrKey is not { } k || !ReferenceEquals(k.Src, src) || k.Gr != gr || k.Gg != gg || k.Gb != gb || k.Luma != luma || k.Color != color
            || _nrR is null || _nrR.Length != n) return false;
        d.For(w, h, new Copy3Kernel(_nrR, _nrG!, _nrB!, b.R, b.G, b.B, w, h));
        GpuDisplayDenoiseReuse++;
        return true;
    }

    private static void StoreDenoise(GraphicsDevice d, GpuBufs b, int w, int h, int n, Raw16Image src, float gr, float gg, float gb, double luma, double color)
    {
        if (_nrR is null || _nrR.Length != n)
        {
            DisposeNr();
            _nrR = d.AllocateReadWriteBuffer<int>(n);
            _nrG = d.AllocateReadWriteBuffer<int>(n);
            _nrB = d.AllocateReadWriteBuffer<int>(n);
        }
        d.For(w, h, new Copy3Kernel(b.R, b.G, b.B, _nrR, _nrG!, _nrB!, w, h));
        _nrKey = (src, gr, gg, gb, luma, color);
    }

    private static void EnsureResident(GraphicsDevice d, Raw16Image src, int n)
    {
        if (ReferenceEquals(_resSrc, src) && _resR is not null && _resR.Length == n) return;
        _resR?.Dispose(); _resG?.Dispose(); _resB?.Dispose();
        _resR = _resG = _resB = null;
        _resSrc = null;
        _resR = d.AllocateReadWriteBuffer<int>(n);
        _resG = d.AllocateReadWriteBuffer<int>(n);
        _resB = d.AllocateReadWriteBuffer<int>(n);
        Upload(_resR, src.R, n);
        Upload(_resG, src.G, n);
        Upload(_resB, src.B, n);
        _resSrc = src;
        GpuDisplayUploads++;
    }

    private static void DisposeNr()
    {
        _nrR?.Dispose(); _nrG?.Dispose(); _nrB?.Dispose();
        _nrR = _nrG = _nrB = null;
        _nrKey = null;
    }

    /// <summary>Frees the resident source and caches (photo closed, or after a GPU error).</summary>
    public static void ReleaseDisplayBuffers()
    {
        lock (GpuLock)
        {
            _resR?.Dispose(); _resG?.Dispose(); _resB?.Dispose();
            _resR = _resG = _resB = null;
            _resSrc = null;
            _outBuf?.Dispose();
            _outBuf = null;
            DisposeNr();
        }
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct GainKernel(ReadWriteBuffer<int> sr, ReadWriteBuffer<int> sg, ReadWriteBuffer<int> sb,
    ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, float gr, float gg, float gb, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        pr[i] = Hlsl.Clamp((int)(sr[i] * gr + 0.5f), 0, 65535);
        pg[i] = Hlsl.Clamp((int)(sg[i] * gg + 0.5f), 0, 65535);
        pb[i] = Hlsl.Clamp((int)(sb[i] * gb + 0.5f), 0, 65535);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct Copy3Kernel(ReadWriteBuffer<int> sr, ReadWriteBuffer<int> sg, ReadWriteBuffer<int> sb,
    ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        pr[i] = sr[i];
        pg[i] = sg[i];
        pb[i] = sb[i];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ClearKernel(ReadWriteBuffer<int> buf, int n) : IComputeShader
{
    public void Execute()
    {
        int i = ThreadIds.X;
        if (i < n) buf[i] = 0;
    }
}

/// <summary>16-bit to dithered BGRA8 (as <see cref="Raw16Image.ToDisplay"/>) plus lum/R/G/B 256-bin histograms (as ComputeHistogram16).</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct PackKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb,
    ReadWriteBuffer<int> outp, ReadWriteBuffer<int> hist, ReadOnlyBuffer<float> bayer, int dither, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        int r = Hlsl.Clamp(pr[i], 0, 65535);
        int g = Hlsl.Clamp(pg[i], 0, 65535);
        int b = Hlsl.Clamp(pb[i], 0, 65535);
        float dd = dither != 0 ? bayer[(y & 3) * 4 + (x & 3)] : 0f;
        const float q = 255.0f / 65535.0f;
        int qr = Hlsl.Clamp((int)(r * q + dd + 0.5f), 0, 255);
        int qg = Hlsl.Clamp((int)(g * q + dd + 0.5f), 0, 255);
        int qb = Hlsl.Clamp((int)(b * q + dd + 0.5f), 0, 255);
        outp[i] = qb | (qg << 8) | (qr << 16) | (255 << 24);
        int l = Hlsl.Clamp((int)(0.2126f * r + 0.7152f * g + 0.0722f * b + 0.5f), 0, 65535);
        Hlsl.InterlockedAdd(ref hist[l >> 8], 1);
        Hlsl.InterlockedAdd(ref hist[256 + (r >> 8)], 1);
        Hlsl.InterlockedAdd(ref hist[512 + (g >> 8)], 1);
        Hlsl.InterlockedAdd(ref hist[768 + (b >> 8)], 1);
    }
}
#endif
