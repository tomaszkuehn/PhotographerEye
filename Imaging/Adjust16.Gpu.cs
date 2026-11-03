#if !PHOTOGRAPHEREYE_NO_GPU
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
        bool curveDehaze = cameraTone is not null && extra is not null && Math.Abs(extra.Dehaze) > 0.0005;

        if (extra is not null && !extra.IsDefault)
        {
            if (NoiseReduction.Active(extra.Denoise, extra.ColorNoise)) Denoise(d, b, w, h, extra.Denoise, extra.ColorNoise, ct);
            ColorLut(d, b, w, h, extra, ct, wbOnly: curveDehaze);
            if (shadow || Math.Abs(extra.Highlights) > 0.0005) ShadowsHighlights(d, b, w, h, extra.Highlights, shadow ? shadowLift : 0.0, ct);
            if (Math.Abs(extra.Clarity) > 0.0005) Detail(d, b, w, h, extra.Clarity, sharpen: false, ct);
            if (extra.Sharpen > 0.0005) Detail(d, b, w, h, extra.Sharpen, sharpen: true, ct);
        }
        else if (shadow)
        {
            ShadowsHighlights(d, b, w, h, 0.0, shadowLift, ct);
        }

        Tone(d, b, w, h, extra ?? new PhotoAdjustments(), manual, cameraTone, ct);
        if (curveDehaze)
        {
            using var db = d.AllocateReadOnlyBuffer(ToIntLut(GetChannelLut(1.0, Math.Clamp(extra!.Dehaze, -1.0, 1.0))));
            d.For(w, h, new Lut3Kernel(b.R, b.G, b.B, db, w, h));
        }
        Sat(d, b, w, h, extra ?? new PhotoAdjustments(), ct);
        EncodeLut(d, b, w, h, ct);
        if (hasGamma)
        {
            var gl = GammaLutCache.GetOrAdd((long)Math.Round(gamma * 1000000.0), _ => BuildGammaLut16(gamma));
            using var gb = d.AllocateReadOnlyBuffer(ToIntLut(gl));
            ApplyLut(d, b, w, h, gb, ct);
        }
        if (extra?.Curves is { HasEffect: true } curves)
        {
            using var cr = d.AllocateReadOnlyBuffer(curves.IntLut16(HistChannel.R));
            using var cg = d.AllocateReadOnlyBuffer(curves.IntLut16(HistChannel.G));
            using var cb = d.AllocateReadOnlyBuffer(curves.IntLut16(HistChannel.B));
            d.For(w, h, new ColorLutKernel(b.R, b.G, b.B, cr, cg, cb, w, h));
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

    // Same algorithm as NoiseReduction.Apply16; per-scale noise is measured on a sparse grid read
    // back from the GPU (~200k floats per scale) and turned into band sigmas on the CPU.
    private static void Denoise(GraphicsDevice d, GpuBufs b, int w, int h, double luma, double color, CancellationToken ct)
    {
        var plan = NoiseReduction.Plan(luma, color);
        d.For(w, h, new NrToPlanesKernel(b.R, b.G, b.B, b.P0, b.P1, b.P2, w, h));
        // Guide luma (two a trous levels of Y) picks the noise band per pixel.
        d.For(w, h, new AtrousHKernel(b.P0, b.Lo1, w, h, 1));
        d.For(w, h, new AtrousVKernel(b.Lo1, b.Luma, w, h, 1));
        d.For(w, h, new AtrousHKernel(b.Luma, b.Lo1, w, h, 2));
        d.For(w, h, new AtrousVKernel(b.Lo1, b.P3, w, h, 2));
        int gstep = Math.Max(1, (int)Math.Sqrt((double)w * h / 200_000.0));
        int nx = w / gstep, ny = h / gstep;
        using var samp = d.AllocateReadWriteBuffer<float>(nx * ny);
        var host = new float[nx * ny];
        d.For(nx, ny, new NrSampleKernel(b.P3, b.P3, samp, w, gstep, nx, ny, 0));
        samp.CopyTo(host);
        var grid = NoiseReduction.MakeGrid(w, h, i =>
        {
            int y = i / w, x = i % w;
            return host[(y / gstep) * nx + x / gstep];
        });
        ct.ThrowIfCancellationRequested();
        DenoisePlane(d, b, b.P0, w, h, NoiseReduction.LumaScales, plan.Ky, plan.KeepY, grid, samp, host, ct);
        if (plan.Median > 0f)
        {
            foreach (var pl in new[] { b.P1, b.P2 })
                for (int step = 1; step <= 2; step++)
                {
                    d.For(w, h, new NrMedianKernel(pl, b.Lo1, plan.Median, step, w, h));
                    d.For(w, h, new NrCopyKernel(b.Lo1, pl, w, h));
                }
        }
        DenoisePlane(d, b, b.P1, w, h, NoiseReduction.ChromaScales, plan.Kc, 0f, grid, samp, host, ct);
        DenoisePlane(d, b, b.P2, w, h, NoiseReduction.ChromaScales, plan.Kc, 0f, grid, samp, host, ct);
        d.For(w, h, new NrFromPlanesKernel(b.R, b.G, b.B, b.P0, b.P1, b.P2, w, h));
    }

    private static void DenoisePlane(GraphicsDevice d, GpuBufs b, ReadWriteBuffer<float> plane, int w, int h, int scales, float k, float keep,
                                     NoiseGrid grid, ReadWriteBuffer<float> samp, float[] host, CancellationToken ct)
    {
        if (k <= 0f) return;
        var cur = b.Luma;
        var next = b.Lo2;
        d.For(w, h, new NrCopyKernel(plane, cur, w, h));
        float[]? prev = null;
        for (int j = 0; j < scales; j++)
        {
            int step = 1 << j;
            if (step * 2 >= Math.Min(w, h)) break;
            ct.ThrowIfCancellationRequested();
            d.For(w, h, new AtrousHKernel(cur, b.Lo1, w, h, step));
            d.For(w, h, new AtrousVKernel(b.Lo1, next, w, h, step));
            d.For(grid.Nx, grid.Ny, new NrSampleKernel(cur, next, samp, w, grid.Step, grid.Nx, grid.Ny, 1));
            samp.CopyTo(host);
            prev = NoiseReduction.BandSigma(host, grid, j, prev);
            using var sigma = d.AllocateReadOnlyBuffer(prev);
            d.For(w, h, new NrShrinkKernel(plane, cur, next, b.P3, sigma, k, keep, j == 0 ? 1 : 0, w, h));
            (cur, next) = (next, cur);
        }
        d.For(w, h, new NrAddKernel(plane, cur, w, h));
    }

    private static void ColorLut(GraphicsDevice d, GpuBufs b, int w, int h, PhotoAdjustments a, CancellationToken ct, bool wbOnly = false)
    {
        var luts = wbOnly ? BuildChannelLuts16(a, 0.0) : BuildChannelLuts16(a);
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

    private static void ShadowsHighlights(GraphicsDevice d, GpuBufs b, int w, int h, double highlights, double shadows, CancellationToken ct)
    {
        float hl = (float)Math.Clamp(highlights, -1.0, 1.0);
        float sh = (float)Math.Clamp(shadows, -1.0, 1.0);
        if (Math.Abs(hl) <= 0.0005f && Math.Abs(sh) <= 0.0005f) return;
        int s = ToneRegions.Factor(w, h);
        int lw = (w + s - 1) / s;
        int lh = (h + s - 1) / s;
        var low = new float[lw * lh];
        ct.ThrowIfCancellationRequested();
        using (var lb = d.AllocateReadWriteBuffer<float>(low.Length))
        {
            d.For(lw, lh, new RegionDownKernel(b.R, b.G, b.B, lb, w, h, s, lw, lh));
            lb.CopyTo(low);
        }
        var grid = ToneRegions.Solve(low, lw, lh, s, w, h);
        ct.ThrowIfCancellationRequested();
        using var ga = d.AllocateReadOnlyBuffer(grid.A);
        using var gb = d.AllocateReadOnlyBuffer(grid.B);
        d.For(w, h, new RegionApplyKernel(b.R, b.G, b.B, ga, gb, hl, sh, s, lw, lh, w, h));
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
        public readonly ReadWriteBuffer<float> P0;
        public readonly ReadWriteBuffer<float> P1;
        public readonly ReadWriteBuffer<float> P2;
        public readonly ReadWriteBuffer<float> P3;
        public readonly ReadWriteBuffer<float> Luma;
        public readonly ReadWriteBuffer<float> Lo1;
        public readonly ReadWriteBuffer<float> Lo2;

        public GpuBufs(GraphicsDevice d, int n)
        {
            R = d.AllocateReadWriteBuffer<int>(n);
            G = d.AllocateReadWriteBuffer<int>(n);
            B = d.AllocateReadWriteBuffer<int>(n);
            P0 = d.AllocateReadWriteBuffer<float>(n);
            P1 = d.AllocateReadWriteBuffer<float>(n);
            P2 = d.AllocateReadWriteBuffer<float>(n);
            P3 = d.AllocateReadWriteBuffer<float>(n);
            Luma = d.AllocateReadWriteBuffer<float>(n);
            Lo1 = d.AllocateReadWriteBuffer<float>(n);
            Lo2 = d.AllocateReadWriteBuffer<float>(n);
        }

        public void Dispose()
        {
            R.Dispose();
            G.Dispose();
            B.Dispose();
            P0.Dispose();
            P1.Dispose();
            P2.Dispose();
            P3.Dispose();
            Luma.Dispose();
            Lo1.Dispose();
            Lo2.Dispose();
        }
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct NrToPlanesKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadWriteBuffer<float> py, ReadWriteBuffer<float> pco, ReadWriteBuffer<float> pcg, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        float r = Hlsl.Sqrt(pr[i] / 65535f);
        float g = Hlsl.Sqrt(pg[i] / 65535f);
        float b = Hlsl.Sqrt(pb[i] / 65535f);
        py[i] = 0.25f * r + 0.5f * g + 0.25f * b;
        pco[i] = 0.5f * (r - b);
        pcg[i] = -0.25f * r + 0.5f * g - 0.25f * b;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct NrFromPlanesKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadWriteBuffer<float> py, ReadWriteBuffer<float> pco, ReadWriteBuffer<float> pcg, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        float t = py[i] - pcg[i];
        float g = Hlsl.Max(py[i] + pcg[i], 0f);
        float r = Hlsl.Max(t + pco[i], 0f);
        float b = Hlsl.Max(t - pco[i], 0f);
        pr[i] = (int)Hlsl.Min(r * r * 65535f + 0.5f, 65535f);
        pg[i] = (int)Hlsl.Min(g * g * 65535f + 0.5f, 65535f);
        pb[i] = (int)Hlsl.Min(b * b * 65535f + 0.5f, 65535f);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct NrMedianKernel(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, float t, int step, int w, int h) : IComputeShader
{
    // 3x3 median (taps step px apart) blended in by t, same network as NoiseReduction.Median9.
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int m = Hlsl.Max(y - step, 0) * w, o = y * w, p = Hlsl.Min(y + step, h - 1) * w;
        int l = Hlsl.Max(x - step, 0), r = Hlsl.Min(x + step, w - 1);
        float p0 = src[m + l], p1 = src[m + x], p2 = src[m + r];
        float p3 = src[o + l], p4 = src[o + x], p5 = src[o + r];
        float p6 = src[p + l], p7 = src[p + x], p8 = src[p + r];
        float c = p4;
        float a;
        a = Hlsl.Min(p1, p2); p2 = Hlsl.Max(p1, p2); p1 = a;
        a = Hlsl.Min(p4, p5); p5 = Hlsl.Max(p4, p5); p4 = a;
        a = Hlsl.Min(p7, p8); p8 = Hlsl.Max(p7, p8); p7 = a;
        a = Hlsl.Min(p0, p1); p1 = Hlsl.Max(p0, p1); p0 = a;
        a = Hlsl.Min(p3, p4); p4 = Hlsl.Max(p3, p4); p3 = a;
        a = Hlsl.Min(p6, p7); p7 = Hlsl.Max(p6, p7); p6 = a;
        a = Hlsl.Min(p1, p2); p2 = Hlsl.Max(p1, p2); p1 = a;
        a = Hlsl.Min(p4, p5); p5 = Hlsl.Max(p4, p5); p4 = a;
        a = Hlsl.Min(p7, p8); p8 = Hlsl.Max(p7, p8); p7 = a;
        a = Hlsl.Min(p0, p3); p3 = Hlsl.Max(p0, p3); p0 = a;
        a = Hlsl.Min(p5, p8); p8 = Hlsl.Max(p5, p8); p5 = a;
        a = Hlsl.Min(p4, p7); p7 = Hlsl.Max(p4, p7); p4 = a;
        a = Hlsl.Min(p3, p6); p6 = Hlsl.Max(p3, p6); p3 = a;
        a = Hlsl.Min(p1, p4); p4 = Hlsl.Max(p1, p4); p1 = a;
        a = Hlsl.Min(p2, p5); p5 = Hlsl.Max(p2, p5); p2 = a;
        a = Hlsl.Min(p4, p7); p7 = Hlsl.Max(p4, p7); p4 = a;
        a = Hlsl.Min(p4, p2); p2 = Hlsl.Max(p4, p2); p4 = a;
        a = Hlsl.Min(p6, p4); p4 = Hlsl.Max(p6, p4); p6 = a;
        p4 = Hlsl.Min(p4, p2);
        dst[o + x] = c + t * (p4 - c);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct NrCopyKernel(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        dst[y * w + x] = src[y * w + x];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct AtrousHKernel(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int h, int step) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int o = y * w;
        int xm2 = Hlsl.Max(x - 2 * step, 0);
        int xm1 = Hlsl.Max(x - step, 0);
        int xp1 = Hlsl.Min(x + step, w - 1);
        int xp2 = Hlsl.Min(x + 2 * step, w - 1);
        dst[o + x] = (src[o + xm2] + src[o + xp2] + 4f * (src[o + xm1] + src[o + xp1]) + 6f * src[o + x]) * 0.0625f;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct AtrousVKernel(ReadWriteBuffer<float> src, ReadWriteBuffer<float> dst, int w, int h, int step) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int m2 = Hlsl.Max(y - 2 * step, 0) * w;
        int m1 = Hlsl.Max(y - step, 0) * w;
        int p1 = Hlsl.Min(y + step, h - 1) * w;
        int p2 = Hlsl.Min(y + 2 * step, h - 1) * w;
        dst[y * w + x] = (src[m2 + x] + src[p2 + x] + 4f * (src[m1 + x] + src[p1 + x]) + 6f * src[y * w + x]) * 0.0625f;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct NrShrinkKernel(ReadWriteBuffer<float> plane, ReadWriteBuffer<float> cur, ReadWriteBuffer<float> next, ReadWriteBuffer<float> guide, ReadOnlyBuffer<float> sigma, float k, float keep, int first, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        int i = y * w + x;
        // Same band lookup as NoiseReduction.Lookup (16 bins over luma 0..1).
        float f = Hlsl.Clamp(guide[i] * 16f - 0.5f, 0f, 15f);
        int i0 = (int)f;
        int i1 = Hlsl.Min(i0 + 1, 15);
        float t = k * (sigma[i0] + (sigma[i1] - sigma[i0]) * (f - i0));
        float d = cur[i] - next[i];
        float d2 = d * d;
        float s = d * d2 / (d2 + t * t + 1e-20f);
        float o = s + keep * (d - s);
        plane[i] = first != 0 ? o : plane[i] + o;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct NrSampleKernel(ReadWriteBuffer<float> a, ReadWriteBuffer<float> b, ReadWriteBuffer<float> samp, int w, int step, int nx, int ny, int diff) : IComputeShader
{
    // Grid point (k, j) = pixel at the cell center, as NoiseReduction.GridIndex.
    public void Execute()
    {
        int k = ThreadIds.X;
        int j = ThreadIds.Y;
        if (k >= nx || j >= ny) return;
        int i = (j * step + step / 2) * w + k * step + step / 2;
        samp[j * nx + k] = diff != 0 ? Hlsl.Abs(a[i] - b[i]) : a[i];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct NrAddKernel(ReadWriteBuffer<float> plane, ReadWriteBuffer<float> coarse, int w, int h) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= w || y >= h) return;
        plane[y * w + x] += coarse[y * w + x];
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
public readonly partial struct RegionDownKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadWriteBuffer<float> low, int w, int h, int s, int lw, int lh) : IComputeShader
{
    public void Execute()
    {
        int lx = ThreadIds.X;
        int ly = ThreadIds.Y;
        if (lx >= lw || ly >= lh) return;
        int x1 = Hlsl.Min(w, (lx + 1) * s);
        int y1 = Hlsl.Min(h, (ly + 1) * s);
        float acc = 0f;
        int cnt = 0;
        for (int y = ly * s; y < y1; y++)
        {
            for (int x = lx * s; x < x1; x++)
            {
                int i = y * w + x;
                // ToneRegions.ToneNorm: luminance with a quarter of the largest channel.
                float yl = (0.75f * (0.2126f * pr[i] + 0.7152f * pg[i] + 0.0722f * pb[i]) + 0.25f * Hlsl.Max(pr[i], Hlsl.Max(pg[i], pb[i]))) / 65535f;
                acc += Hlsl.Max(Hlsl.Log2(Hlsl.Max(yl, 1e-9f) / 0.18f), -12f);
                cnt++;
            }
        }
        low[ly * lw + lx] = acc / Hlsl.Max(cnt, 1);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct RegionApplyKernel(ReadWriteBuffer<int> pr, ReadWriteBuffer<int> pg, ReadWriteBuffer<int> pb, ReadOnlyBuffer<float> ga, ReadOnlyBuffer<float> gb, float hl, float sh, int s, int lw, int lh, int w, int h) : IComputeShader
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
        float yl = (0.75f * (0.2126f * r + 0.7152f * g + 0.0722f * b) + 0.25f * Hlsl.Max(r, Hlsl.Max(g, b))) / 65535f;
        if (yl <= 0f) return;
        float l = Hlsl.Max(Hlsl.Log2(Hlsl.Max(yl, 1e-9f) / 0.18f), -12f);

        float gx = Hlsl.Clamp((x + 0.5f) / s - 0.5f, 0f, lw - 1);
        float gy = Hlsl.Clamp((y + 0.5f) / s - 0.5f, 0f, lh - 1);
        int x0 = (int)gx;
        int y0 = (int)gy;
        int x1 = Hlsl.Min(x0 + 1, lw - 1);
        int y1 = Hlsl.Min(y0 + 1, lh - 1);
        float fx = gx - x0;
        float fy = gy - y0;
        float a0 = ga[y0 * lw + x0] + (ga[y0 * lw + x1] - ga[y0 * lw + x0]) * fx;
        float a1 = ga[y1 * lw + x0] + (ga[y1 * lw + x1] - ga[y1 * lw + x0]) * fx;
        float b0 = gb[y0 * lw + x0] + (gb[y0 * lw + x1] - gb[y0 * lw + x0]) * fx;
        float b1 = gb[y1 * lw + x0] + (gb[y1 * lw + x1] - gb[y1 * lw + x0]) * fx;
        float baseEv = (a0 + (a1 - a0) * fy) * l + (b0 + (b1 - b0) * fy);

        float m = 1f - SmoothEV(-4.8f, 0.3f, baseEv);
        float delta = hl * 1.7f * SmoothEV(-1f, 2.8f, baseEv) + sh * 1.7f * m * Hlsl.Sqrt(m);
        if (Hlsl.Abs(delta) < 1e-4f) return;
        float k = Hlsl.Pow(2f, delta);
        float scale = (k <= 1f ? yl * k : yl * k / (1f + (k - 1f) * Hlsl.Min(yl, 1f))) / yl;
        // ToneRegions.Scale: brightening lifts luminance by scale, the colour difference only by scale^0.7 (ChromaGainExpLinear).
        float y0l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        if (scale <= 1f)
        {
            r *= scale;
            g *= scale;
            b *= scale;
        }
        else
        {
            float yo = y0l * scale;
            float c = Hlsl.Pow(scale, 0.7f);
            r = Hlsl.Max(yo + (r - y0l) * c, 0f);
            g = Hlsl.Max(yo + (g - y0l) * c, 0f);
            b = Hlsl.Max(yo + (b - y0l) * c, 0f);
        }
        float o = (0.2126f * r + 0.7152f * g + 0.0722f * b) / 65535f;
        float mx = Hlsl.Max(r, Hlsl.Max(g, b));
        if (mx > 65535f)
        {
            float o16 = o * 65535f;
            float t = (mx - 65535f) / Hlsl.Max(mx - o16, 1e-6f);
            r += (o16 - r) * t;
            g += (o16 - g) * t;
            b += (o16 - b) * t;
        }
        int ri = (int)(r + 0.5f);
        pr[i] = ri < 0 ? 0 : ri > 65535 ? 65535 : ri;
        int gi = (int)(g + 0.5f);
        pg[i] = gi < 0 ? 0 : gi > 65535 ? 65535 : gi;
        int bi = (int)(b + 0.5f);
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
#endif
