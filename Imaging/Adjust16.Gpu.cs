using System;
using System.Buffers;
using System.Threading;
using ComputeSharp;

namespace ImgViewer.Imaging;

public static partial class Adjust16
{
    private const int GpuMinPixels = 4_000_000;

    public static bool GpuEnabled { get; set; } =
        !string.Equals(Environment.GetEnvironmentVariable("PHOTOGRAPHEREYE_GPU"), "0", StringComparison.Ordinal);

    private static readonly object GpuLock = new();
    private static bool _gpuInit;
    private static GraphicsDevice? _gpu;
    private static string _gpuReason = "not checked";
    private static ReadOnlyBuffer<int>? _bMonoDoc;
    private static ReadOnlyBuffer<int>? _bMonoTriX;
    private static ReadOnlyBuffer<int>? _bEncodeSrgb;
    private static int _bufN;
    private static GpuBufs? _bufs;

    public static bool GpuAvailable
    {
        get { EnsureGpu(); return _gpu is not null; }
    }

    public static string? GpuDeviceName
    {
        get { EnsureGpu(); return _gpu?.Name; }
    }

    public static string GpuStatus
    {
        get
        {
            if (!GpuEnabled) return "CPU (GPU acceleration disabled)";
            EnsureGpu();
            return _gpu is not null ? $"GPU: {_gpu.Name}" : $"CPU (GPU unavailable: {_gpuReason})";
        }
    }

    private static bool EnsureGpu()
    {
        if (_gpuInit) return _gpu is not null;
        lock (GpuLock)
        {
            if (_gpuInit) return _gpu is not null;
            try
            {
                GraphicsDevice? best = null;
                var spare = new List<GraphicsDevice>();
                foreach (var dev in GraphicsDevice.QueryDevices(static info => info.IsHardwareAccelerated))
                {
                    if (best is null || dev.DedicatedMemorySize > best.DedicatedMemorySize)
                    {
                        if (best is not null) spare.Add(best);
                        best = dev;
                    }
                    else
                    {
                        spare.Add(dev);
                    }
                }
                foreach (var s in spare)
                {
                    try { s.Dispose(); } catch (Exception) { }
                }

                if (best is not null && best.IsHardwareAccelerated)
                {
                    _gpu = best;
                    _bMonoDoc = best.AllocateReadOnlyBuffer(ToIntLut(MonoDocLut.Value));
                    _bMonoTriX = best.AllocateReadOnlyBuffer(ToIntLut(MonoTriXLut.Value));
                    _bEncodeSrgb = best.AllocateReadOnlyBuffer(ToIntLut(LinearRender.LinToSrgbLut));
                    _gpuReason = "ok";
                }
                else
                {
                    _gpuReason = "no hardware adapter (WARP only)";
                }
            }
            catch (Exception ex)
            {
                _gpu = null;
                _gpuReason = $"{ex.GetType().Name}: {ex.Message}";
                Debug.Log($"GPU init failed: {_gpuReason}");
            }
            _gpuInit = true;
        }
        return _gpu is not null;
    }

    private static int[] ToIntLut(ushort[] src)
    {
        var dst = new int[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = src[i];
        return dst;
    }

    public static Raw16Image? TryApplyGpu(Raw16Image source, double shadowLift, double gamma, ManualStretchMap? manual, out HistogramData? histogram, MonoMode mono = MonoMode.None, int grainSeed = 0, PhotoAdjustments? extra = null, CancellationToken ct = default, CameraToneCurve? cameraTone = null)
    {
        histogram = null;
        if (!GpuEnabled) return null;
        bool nothing = (extra is null || extra.IsDefault)
            && Math.Abs(shadowLift) <= 0.0001
            && Math.Abs(gamma - 1.0) < 0.0005
            && (manual is null || !manual.HasEffect)
            && mono == MonoMode.None;
        if (nothing) return null;
        int w = source.W;
        int h = source.H;
        long n = (long)w * h;
        if (n < GpuMinPixels || n > int.MaxValue || w <= 0 || h <= 0) return null;
        if (!EnsureGpu()) return null;

        lock (GpuLock)
        {
            try
            {
                return RunGpu(source, w, h, (int)n, shadowLift, gamma, manual, out histogram, mono, grainSeed, extra, ct, cameraTone);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.Log($"GPU APPLY failed: {ex.GetType().Name} {ex.Message}; fallback CPU");
                histogram = null;
                return null;
            }
        }
    }

    private static Raw16Image RunGpu(Raw16Image source, int w, int h, int n, double shadowLift, double gamma, ManualStretchMap? manual, out HistogramData? histogram, MonoMode mono, int grainSeed, PhotoAdjustments? extra, CancellationToken ct, CameraToneCurve? cameraTone = null)
    {
        histogram = null;
        var d = _gpu!;
        EnsureBufs(d, n);
        var b = _bufs!;
        var opts = new ParallelOptions { CancellationToken = ct };

        Upload(b.R, source.R, n);
        Upload(b.G, source.G, n);
        Upload(b.B, source.B, n);
        ct.ThrowIfCancellationRequested();

        ct.ThrowIfCancellationRequested();

        bool shadow = Math.Abs(shadowLift) > 0.0001;
        bool hasGamma = Math.Abs(gamma - 1.0) >= 0.0005;

        if (extra is not null && !extra.IsDefault)
        {
            if (extra.Denoise > 0.0005) BlurMix(d, b, w, h, extra.Denoise, chroma: false, ct);
            if (extra.ColorNoise > 0.0005) BlurMix(d, b, w, h, extra.ColorNoise, chroma: true, ct);
            ColorLut(d, b, w, h, extra, ct);
            if (Math.Abs(extra.Highlights) > 0.0005) RegionCurve(d, b, w, h, extra.Highlights, highlight: true, ct);
            if (shadow) RegionCurve(d, b, w, h, shadowLift, highlight: false, ct);
            if (Math.Abs(extra.Clarity) > 0.0005) Detail(d, b, w, h, extra.Clarity, sharpen: false, ct);
            if (extra.Sharpen > 0.0005) Detail(d, b, w, h, extra.Sharpen, sharpen: true, ct);
        }
        else if (shadow)
        {
            RegionCurve(d, b, w, h, shadowLift, highlight: false, ct);
        }

        Tone(d, b, w, h, extra ?? new PhotoAdjustments(), manual, cameraTone, ct);
        Sat(d, b, w, h, extra ?? new PhotoAdjustments(), ct);
        EncodeLut(d, b, w, h, ct);
        if (hasGamma)
        {
            var gl = GammaLutCache.GetOrAdd((long)Math.Round(gamma * 1000000.0), _ => BuildGammaLut16(gamma));
            using var gb = d.AllocateReadOnlyBuffer(ToIntLut(gl));
            ApplyLut(d, b, w, h, gb, ct);
        }
        switch (mono)
        {
            case MonoMode.Documentary:
                Mono(d, b, w, h, 0.325f, 0.605f, 0.070f, _bMonoDoc!, ct);
                break;
            case MonoMode.TriX:
                Mono(d, b, w, h, 0.345f, 0.600f, 0.055f, _bMonoTriX!, ct);
                Grain(d, b, w, h, grainSeed, ct);
                break;
        }

        ct.ThrowIfCancellationRequested();
        var r = Download(b.R, n);
        var g = Download(b.G, n);
        var bl = Download(b.B, n);
        var img = new Raw16Image(r, g, bl, w, h);
        histogram = ComputeHistogram16(img, opts);
        return img;
    }

    private static void BlurMix(GraphicsDevice d, GpuBufs b, int w, int h, double amount, bool chroma, CancellationToken ct)
    {
        double x = Math.Clamp(amount, 0.0, 1.0);
        int r = x < 0.34 ? 1 : (x < 0.67 ? 2 : 3);
        float blend = (float)(chroma ? 0.15 + 0.65 * x : 0.15 + 0.55 * x);
        BoxBlurInt(d, b.R, b.Blr, b.Tmp, w, h, r, ct);
        BoxBlurInt(d, b.G, b.Blg, b.Tmp, w, h, r, ct);
        BoxBlurInt(d, b.B, b.Blb, b.Tmp, w, h, r, ct);
        ct.ThrowIfCancellationRequested();
        if (chroma)
            d.For(w, h, new ChromaMixKernel(b.R, b.G, b.B, b.Blr, b.Blg, b.Blb, blend, w, h));
        else
            d.For(w, h, new BlurMixKernel(b.R, b.G, b.B, b.Blr, b.Blg, b.Blb, blend, w, h));
    }

    private static void BoxBlurInt(GraphicsDevice d, ReadWriteBuffer<int> src, ReadWriteBuffer<int> dst, ReadWriteBuffer<int> tmp, int w, int h, int r, CancellationToken ct)
    {
        int window = 2 * r + 1;
        d.For(w, h, new BlurHIntKernel(src, tmp, w, h, r, window));
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new BlurVIntKernel(tmp, dst, w, h, r, window));
    }

    private static void ColorLut(GraphicsDevice d, GpuBufs b, int w, int h, PhotoAdjustments a, CancellationToken ct)
    {
        var luts = BuildChannelLuts16(a);
        using var br = d.AllocateReadOnlyBuffer(ToIntLut(luts.R));
        using var bg = d.AllocateReadOnlyBuffer(ToIntLut(luts.G));
        using var bb = d.AllocateReadOnlyBuffer(ToIntLut(luts.B));
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new ColorLutKernel(b.R, b.G, b.B, br, bg, bb, w, h));
    }

    private static void Sat(GraphicsDevice d, GpuBufs b, int w, int h, PhotoAdjustments a, CancellationToken ct)
    {
        float sat = PhotoOps.SatFactor(a);
        if (Math.Abs(sat - 1.0f) <= 0.0005f) return;
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new SatKernel(b.R, b.G, b.B, sat, w, h));
    }

    private static void EncodeLut(GraphicsDevice d, GpuBufs b, int w, int h, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new Lut3Kernel(b.R, b.G, b.B, _bEncodeSrgb!, w, h));
    }

    private static void Tone(GraphicsDevice d, GpuBufs b, int w, int h, PhotoAdjustments a, ManualStretchMap? manual, CameraToneCurve? cameraTone, CancellationToken ct)
    {
        float gain = (float)Math.Pow(2.0, Math.Clamp(a.Exposure, -5.0, 5.0));
        var lut = PhotoOps.GetToneLut(a.Contrast, a.Whites, a.Blacks, scene: true, manual, cameraTone);
        if (Math.Abs(gain - 1f) > 1e-4f)
        {
            d.For(w, h, new ToneGainKernel(b.R, b.G, b.B, gain, w, h));
        }
        ct.ThrowIfCancellationRequested();
        using var lb = d.AllocateReadOnlyBuffer(lut);
        d.For(w, h, new ToneKernel(b.R, b.G, b.B, lb, 1f, w, h));
    }

    private static void RegionCurve(GraphicsDevice d, GpuBufs b, int w, int h, double value, bool highlight, CancellationToken ct)
    {
        float v = (float)Math.Clamp(value, -1.0, 1.0);
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new RegionCurveKernel(b.R, b.G, b.B, v, highlight, w, h));
    }

    private static void Detail(GraphicsDevice d, GpuBufs b, int w, int h, double amount, bool sharpen, CancellationToken ct)
    {
        int r;
        float a;
        float weight;
        if (sharpen)
        {
            double amt = Math.Clamp(amount, 0.0, 1.5);
            r = Math.Clamp((int)Math.Round(Math.Min(w, h) * 0.0005), 1, 3);
            a = (float)(amt * 1.15);
            weight = 0f;
        }
        else
        {
            double amt = Math.Clamp(amount, -1.0, 1.0);
            r = (int)PhotoOps.ClarityRadius(w, h);
            a = (float)(1.5 * amt);
            weight = 1f;
        }
        float wf = 2 * r + 1;
        d.For(w, h, new LumaKernel(b.R, b.G, b.B, b.Luma, w, h));
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new BlurHFloatKernel(b.Luma, b.Lo1, w, h, r, wf));
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new BlurVFloatKernel(b.Lo1, b.Lo2, w, h, r, wf));
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new DeltaKernel(b.R, b.G, b.B, b.Luma, b.Lo2, a, weight, w, h));
    }

    private static void ApplyLut(GraphicsDevice d, GpuBufs b, int w, int h, ReadOnlyBuffer<int> lut, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new Lut3Kernel(b.R, b.G, b.B, lut, w, h));
    }

    private static void Mono(GraphicsDevice d, GpuBufs b, int w, int h, float wr, float wg, float wb, ReadOnlyBuffer<int> lut, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new MonoKernel(b.R, b.G, b.B, lut, wr, wg, wb, w, h));
    }

    private static void Grain(GraphicsDevice d, GpuBufs b, int w, int h, int seed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        d.For(w, h, new GrainKernel(b.R, b.G, b.B, seed, w, h));
    }

    private static void EnsureBufs(GraphicsDevice d, int n)
    {
        if (_bufs is not null && _bufN == n) return;
        _bufs?.Dispose();
        _bufs = new GpuBufs(d, n);
        _bufN = n;
    }

    private static void Upload(ReadWriteBuffer<int> buf, ushort[] src, int n)
    {
        int[] tmp = ArrayPool<int>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; i++) tmp[i] = src[i];
            buf.CopyFrom(tmp.AsSpan(0, n));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(tmp);
        }
    }

    private static ushort[] Download(ReadWriteBuffer<int> buf, int n)
    {
        int[] tmp = ArrayPool<int>.Shared.Rent(n);
        var dst = new ushort[n];
        try
        {
            buf.CopyTo(tmp.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                int v = tmp[i];
                dst[i] = (ushort)(v < 0 ? 0 : v > 65535 ? 65535 : v);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(tmp);
        }
        return dst;
    }

    private sealed class GpuBufs : IDisposable
    {
        public readonly ReadWriteBuffer<int> R;
        public readonly ReadWriteBuffer<int> G;
        public readonly ReadWriteBuffer<int> B;
        public readonly ReadWriteBuffer<int> Blr;
        public readonly ReadWriteBuffer<int> Blg;
        public readonly ReadWriteBuffer<int> Blb;
        public readonly ReadWriteBuffer<int> Tmp;
        public readonly ReadWriteBuffer<float> Luma;
        public readonly ReadWriteBuffer<float> Lo1;
        public readonly ReadWriteBuffer<float> Lo2;

        public GpuBufs(GraphicsDevice d, int n)
        {
            R = d.AllocateReadWriteBuffer<int>(n);
            G = d.AllocateReadWriteBuffer<int>(n);
            B = d.AllocateReadWriteBuffer<int>(n);
            Blr = d.AllocateReadWriteBuffer<int>(n);
            Blg = d.AllocateReadWriteBuffer<int>(n);
            Blb = d.AllocateReadWriteBuffer<int>(n);
            Tmp = d.AllocateReadWriteBuffer<int>(n);
            Luma = d.AllocateReadWriteBuffer<float>(n);
            Lo1 = d.AllocateReadWriteBuffer<float>(n);
            Lo2 = d.AllocateReadWriteBuffer<float>(n);
        }

        public void Dispose()
        {
            R.Dispose();
            G.Dispose();
            B.Dispose();
            Blr.Dispose();
            Blg.Dispose();
            Blb.Dispose();
            Tmp.Dispose();
            Luma.Dispose();
            Lo1.Dispose();
            Lo2.Dispose();
        }
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct BlurHIntKernel(ReadWriteBuffer<int> src, ReadWriteBuffer<int> dst, int w, int h, int r, int window) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int row = y * w;
        int sum = 0;
        for (int k = -r; k <= r; k++)
        {
            int xx = x + k;
            if (xx < 0) xx = 0;
            else if (xx >= w) xx = w - 1;
            sum += src[row + xx];
        }
        dst[row + x] = sum / window;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct BlurVIntKernel(ReadWriteBuffer<int> src, ReadWriteBuffer<int> dst, int w, int h, int r, int window) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int sum = 0;
        for (int k = -r; k <= r; k++)
        {
            int yy = y + k;
            if (yy < 0) yy = 0;
            else if (yy >= h) yy = h - 1;
            sum += src[yy * w + x];
        }
        dst[y * w + x] = sum / window;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct BlurMixKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadWriteBuffer<int> blr, ReadWriteBuffer<int> blg, ReadWriteBuffer<int> blb, float blend, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        int r = (int)(pr[i] + (blr[i] - pr[i]) * blend + 0.5f);
        pr[i] = r < 0 ? 0 : r > 65535 ? 65535 : r;
        int g = (int)(pg[i] + (blg[i] - pg[i]) * blend + 0.5f);
        pg[i] = g < 0 ? 0 : g > 65535 ? 65535 : g;
        int b = (int)(pb[i] + (blb[i] - pb[i]) * blend + 0.5f);
        pb[i] = b < 0 ? 0 : b > 65535 ? 65535 : b;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ChromaMixKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadWriteBuffer<int> blr, ReadWriteBuffer<int> blg, ReadWriteBuffer<int> blb, float blend, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        float lum0 = 0.2126f * pr[i] + 0.7152f * pg[i] + 0.0722f * pb[i];
        float lumB = 0.2126f * blr[i] + 0.7152f * blg[i] + 0.0722f * blb[i];
        int rr = (int)(lum0 + (blr[i] - lumB) + 0.5f);
        int gg = (int)(lum0 + (blg[i] - lumB) + 0.5f);
        int bb = (int)(lum0 + (blb[i] - lumB) + 0.5f);
        int r = (int)(pr[i] + (rr - pr[i]) * blend + 0.5f);
        pr[i] = r < 0 ? 0 : r > 65535 ? 65535 : r;
        int g = (int)(pg[i] + (gg - pg[i]) * blend + 0.5f);
        pg[i] = g < 0 ? 0 : g > 65535 ? 65535 : g;
        int b = (int)(pb[i] + (bb - pb[i]) * blend + 0.5f);
        pb[i] = b < 0 ? 0 : b > 65535 ? 65535 : b;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ColorLutKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadOnlyBuffer<int> lutR, ReadOnlyBuffer<int> lutG, ReadOnlyBuffer<int> lutB, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        pr[i] = lutR[pr[i]];
        pg[i] = lutG[pg[i]];
        pb[i] = lutB[pb[i]];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct SatKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, float sat, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        float r = pr[i];
        float g = pg[i];
        float b = pb[i];
        float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        int ri = (int)(lum + (r - lum) * sat + 0.5f);
        pr[i] = ri < 0 ? 0 : ri > 65535 ? 65535 : ri;
        int gi = (int)(lum + (g - lum) * sat + 0.5f);
        pg[i] = gi < 0 ? 0 : gi > 65535 ? 65535 : gi;
        int bi = (int)(lum + (b - lum) * sat + 0.5f);
        pb[i] = bi < 0 ? 0 : bi > 65535 ? 65535 : bi;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct RegionCurveKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, float strength, bool highlight, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        float lr = pr[i] / 65535f;
        float lg = pg[i] / 65535f;
        float lb = pb[i] / 65535f;
        float lum = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
        if (lum <= 0f) return;
        float ev = Hlsl.Log2(lum / 0.18f);
        float mask = highlight ? SmoothEV(-1f, 2.8f, ev) : 1f - SmoothEV(-4.8f, 0.3f, ev);
        if (mask <= 0f) return;
        float delta = strength * 1.7f * mask * (highlight ? 1f : Hlsl.Sqrt(mask));
        float g = Hlsl.Pow(2f, delta);
        int ri = (int)(pr[i] * g + 0.5f);
        pr[i] = ri < 0 ? 0 : ri > 65535 ? 65535 : ri;
        int gi = (int)(pg[i] * g + 0.5f);
        pg[i] = gi < 0 ? 0 : gi > 65535 ? 65535 : gi;
        int bi = (int)(pb[i] * g + 0.5f);
        pb[i] = bi < 0 ? 0 : bi > 65535 ? 65535 : bi;
    }

    private static float SmoothEV(float e0, float e1, float x)
    {
        float t = (x - e0) / (e1 - e0);
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ToneGainKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, float gain, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        int ri = (int)(pr[i] * gain + 0.5f);
        pr[i] = ri < 0 ? 0 : ri > 65535 ? 65535 : ri;
        int gi = (int)(pg[i] * gain + 0.5f);
        pg[i] = gi < 0 ? 0 : gi > 65535 ? 65535 : gi;
        int bi = (int)(pb[i] * gain + 0.5f);
        pb[i] = bi < 0 ? 0 : bi > 65535 ? 65535 : bi;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ToneKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadOnlyBuffer<float> lut, float gain, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        float yl = (0.2126f * pr[i] + 0.7152f * pg[i] + 0.0722f * pb[i]) / 65535f;
        float yl2 = yl * gain;
        if (yl2 <= 1e-7f)
        {
            pr[i] = pg[i] = pb[i] = 0;
            return;
        }
        float ev = Hlsl.Log2(yl2 / 0.18f);
        float f = Hlsl.Clamp((ev + 14f) / 24f, 0f, 1f);
        float o;
        if (ev < -14f)
        {
            o = lut[0] * yl2 / (0.18f * Hlsl.Pow(2f, -14f));
        }
        else
        {
            int idx = (int)(f * (lut.Length - 1));
            if (idx >= lut.Length - 1) o = lut[lut.Length - 1];
            else
            {
                float t = f * (lut.Length - 1) - idx;
                o = lut[idx] + (lut[idx + 1] - lut[idx]) * t;
            }
        }
        float scale = o / Hlsl.Max(yl, 1e-7f);
        float r0 = pr[i] * scale;
        float g0 = pg[i] * scale;
        float b0 = pb[i] * scale;
        float mx = Hlsl.Max(r0, Hlsl.Max(g0, b0));
        if (mx > 65535f)
        {
            float o16 = o * 65535f;
            float t = (mx - 65535f) / Hlsl.Max(mx - o16, 1e-6f);
            r0 += (o16 - r0) * t;
            g0 += (o16 - g0) * t;
            b0 += (o16 - b0) * t;
        }
        int ri = (int)(r0 + 0.5f);
        pr[i] = ri < 0 ? 0 : ri > 65535 ? 65535 : ri;
        int gi = (int)(g0 + 0.5f);
        pg[i] = gi < 0 ? 0 : gi > 65535 ? 65535 : gi;
        int bi = (int)(b0 + 0.5f);
        pb[i] = bi < 0 ? 0 : bi > 65535 ? 65535 : bi;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct LumaKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadWriteBuffer<float> lum, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        lum[i] = 0.2126f * pr[i] + 0.7152f * pg[i] + 0.0722f * pb[i];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct BlurHFloatKernel(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int h, int r, float wf) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int row = y * w;
        float sum = 0f;
        for (int k = -r; k <= r; k++)
        {
            int xx = x + k;
            if (xx < 0) xx = 0;
            else if (xx >= w) xx = w - 1;
            sum += src[row + xx];
        }
        dst[row + x] = sum / wf;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct BlurVFloatKernel(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int h, int r, float wf) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        float sum = 0f;
        for (int k = -r; k <= r; k++)
        {
            int yy = y + k;
            if (yy < 0) yy = 0;
            else if (yy >= h) yy = h - 1;
            sum += src[yy * w + x];
        }
        dst[y * w + x] = sum / wf;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct DeltaKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadWriteBuffer<float> lum, ReadWriteBuffer<float> low2, float a, float weight, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        float aa = a;
        if (weight > 0f)
        {
            float v = lum[i] / 65535f;
            float mid = 1f - (2f * v - 1f) * (2f * v - 1f);
            aa *= 0.25f + 0.75f * mid;
        }
        float delta = (lum[i] - low2[i]) * aa;
        int r = (int)(pr[i] + delta + 0.5f);
        pr[i] = r < 0 ? 0 : r > 65535 ? 65535 : r;
        int g = (int)(pg[i] + delta + 0.5f);
        pg[i] = g < 0 ? 0 : g > 65535 ? 65535 : g;
        int b = (int)(pb[i] + delta + 0.5f);
        pb[i] = b < 0 ? 0 : b > 65535 ? 65535 : b;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct Lut3Kernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadOnlyBuffer<int> lut, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        pr[i] = lut[pr[i]];
        pg[i] = lut[pg[i]];
        pb[i] = lut[pb[i]];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct MonoKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadOnlyBuffer<int> lut, float wr, float wg, float wb, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        float gray = wr * pr[i] + wg * pg[i] + wb * pb[i];
        int gi = (int)(gray + 0.5f);
        if (gi < 0) gi = 0; else if (gi > 65535) gi = 65535;
        int o = lut[gi];
        pr[i] = o;
        pg[i] = o;
        pb[i] = o;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct GrainKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, int seed, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        uint n = (uint)i * 1597334677u ^ (uint)seed;
        n = (n ^ (n >> 13)) * 1274126177u;
        n ^= n >> 16;
        uint n2 = (n ^ (n >> 11)) * 2654434677u;
        n2 ^= n2 >> 15;
        float t = ((n & 0xFFFF) / 65535f) - ((n2 & 0xFFFF) / 65535f);
        int gray = (pr[i] + pg[i] + pb[i]) / 3;
        float fg = gray / 65535f;
        float weight = 4f * fg * (1f - fg);
        int v = gray + (int)Hlsl.Round(t * 17.5f * 257f * weight);
        if (v < 0) v = 0; else if (v > 65535) v = 65535;
        pr[i] = v;
        pg[i] = v;
        pb[i] = v;
    }
}
