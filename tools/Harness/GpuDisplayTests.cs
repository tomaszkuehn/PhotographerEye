using System;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using ImgViewer.Imaging;
using ImgViewer.Services;

internal static partial class Program
{
    private static void TestGpuDisplay()
    {
        if (!Adjust16.GpuAvailable)
        {
            Console.WriteLine($"INFO  gpu display: skipped ({Adjust16.GpuStatus})");
            return;
        }
        const int W = 1600, H = 1100;
        var src = new Raw16Image(W, H);
        var rnd = new Random(11);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                double u = x / (double)W, v = y / (double)H;
                double l = 0.01 + 0.35 * u * v + 0.25 * Math.Exp(-((u - 0.6) * (u - 0.6) + (v - 0.4) * (v - 0.4)) * 20);
                double nz = (rnd.NextDouble() - 0.5) * 0.01;
                src.R[i] = (ushort)(Math.Clamp(l * 1.2 + nz, 0, 1) * 65535);
                src.G[i] = (ushort)(Math.Clamp(l + nz, 0, 1) * 65535);
                src.B[i] = (ushort)(Math.Clamp(l * (0.5 + 0.6 * v) + nz, 0, 1) * 65535);
            }

        (BitmapSource Bmp, HistogramData Hist) Cpu(double ev, double temp, double tint, double sh, double gm, PhotoAdjustments? a, MonoMode mono = MonoMode.None)
        {
            var lin = LinearRender.ApplyGain(src, ev, temp, tint);
            var o = Adjust16.Apply(lin, sh, gm, null, out var hh, mono: mono, grainSeed: 3, extra: a);
            return (o.ToDisplay(true), hh!);
        }

        static (double Mean, double OverOne) Diff(BitmapSource a, BitmapSource b)
        {
            int n = a.PixelWidth * a.PixelHeight * 4;
            var pa = new byte[n];
            var pb = new byte[n];
            a.CopyPixels(pa, a.PixelWidth * 4, 0);
            b.CopyPixels(pb, b.PixelWidth * 4, 0);
            long sum = 0, over = 0;
            for (int i = 0; i < n; i++)
            {
                int d = Math.Abs(pa[i] - pb[i]);
                sum += d;
                if (d > 1) over++;
            }
            return (sum / (double)n, over / (double)n);
        }

        static double HistDiff(HistogramData a, HistogramData b)
        {
            long tot = 0, diff = 0;
            foreach (var (x, y) in new[] { (a.Lum, b.Lum), (a.R, b.R), (a.G, b.G), (a.B, b.B) })
                for (int i = 0; i < 256; i++) { tot += x[i]; diff += Math.Abs(x[i] - y[i]); }
            return diff / (double)Math.Max(1, tot);
        }

        var cases = new (string Name, double Ev, double Temp, double Tint, double Sh, double Gm, PhotoAdjustments? A, MonoMode Mono)[]
        {
            ("neutral", 0, 0, 0, 0, 1, null, MonoMode.None),
            ("tone", 0.4, 0.2, -0.1, 0.3, 1.1, new PhotoAdjustments { Contrast = 0.3, Saturation = 1.25, Highlights = -0.3, Clarity = 0.4, Dehaze = 0.15 }, MonoMode.None),
            ("denoise", -0.3, -0.3, 0.2, 0, 1, new PhotoAdjustments { Denoise = 0.5, ColorNoise = 0.4, Contrast = 0.1 }, MonoMode.None),
            ("mono", 0, 0, 0, 0, 1, new PhotoAdjustments { Contrast = 0.2 }, MonoMode.Documentary),
        };
        foreach (var c in cases)
        {
            var (cb, ch) = Cpu(c.Ev, c.Temp, c.Tint, c.Sh, c.Gm, c.A, c.Mono);
            var gb = Adjust16.TryRenderDisplayGpu(src, c.Ev, c.Temp, c.Tint, c.Sh, c.Gm, null, out var gh, c.Mono, 3, c.A);
            if (gb is null || gh is null) { Check(false, $"gpu display: {c.Name} rendered"); continue; }
            var (mean, over) = Diff(cb, gb);
            double hd = HistDiff(ch, gh);
            Check(gb.IsFrozen && gb.PixelWidth == W && mean < 0.35 && over < 0.01,
                  $"gpu display: {c.Name} matches the CPU render (mean {mean:0.000}, >1 level {over:P2})");
            Check(hd < 0.01, $"gpu display: {c.Name} histogram matches the CPU one (diff {hd:P3})");
        }

        // Source stays resident: same buffer, other settings -> no upload; denoise reused while only tone changes.
        int up0 = Adjust16.GpuDisplayUploads;
        int nr0 = Adjust16.GpuDisplayDenoiseReuse;
        var dn = new PhotoAdjustments { Denoise = 0.5, ColorNoise = 0.4, Contrast = 0.1 };
        var first = Adjust16.TryRenderDisplayGpu(src, -0.3, -0.3, 0.2, 0, 1, null, out _, extra: dn)!;
        var again = Adjust16.TryRenderDisplayGpu(src, -0.3, -0.3, 0.2, 0, 1, null, out _, extra: new PhotoAdjustments { Denoise = 0.5, ColorNoise = 0.4, Contrast = 0.4 })!;
        var back = Adjust16.TryRenderDisplayGpu(src, -0.3, -0.3, 0.2, 0, 1, null, out _, extra: dn)!;
        Check(Adjust16.GpuDisplayUploads == up0, $"gpu display: source uploaded once ({Adjust16.GpuDisplayUploads - up0} extra uploads)");
        Check(Adjust16.GpuDisplayDenoiseReuse - nr0 == 3, $"gpu display: denoise reused while only later settings change ({Adjust16.GpuDisplayDenoiseReuse - nr0})");
        Check(Diff(first, back).Mean == 0 && Diff(first, again).Mean > 0, "gpu display: reused denoise gives the same image");
        Adjust16.TryRenderDisplayGpu(src, 0.1, -0.3, 0.2, 0, 1, null, out _, extra: dn);
        Check(Adjust16.GpuDisplayDenoiseReuse - nr0 == 3, "gpu display: exposure change redoes the denoise");
        var other = src.Downscale(1200);
        Adjust16.TryRenderDisplayGpu(other, 0, 0, 0, 0, 1, null, out _, extra: dn);
        Check(Adjust16.GpuDisplayUploads == up0 + 1, "gpu display: a new buffer is uploaded");

        // Timing (informative): 4.4 MP preview, contrast + saturation, CPU path vs GPU display path.
        var big = new Raw16Image(2560, 1707);
        for (int i = 0; i < big.R.Length; i++) { big.R[i] = (ushort)(i * 7); big.G[i] = (ushort)(i * 13); big.B[i] = (ushort)(i * 3); }
        var simple = new PhotoAdjustments { Contrast = 0.2, Saturation = 1.2 };
        long Time(Action f)
        {
            f();
            var sw = Stopwatch.StartNew();
            for (int k = 0; k < 3; k++) f();
            return sw.ElapsedMilliseconds / 3;
        }
        long tCpu = Time(() => Adjust16.Apply(LinearRender.ApplyGain(big, 0.2, 0.1, 0), 0, 1, null, out _, extra: simple).ToDisplay(true));
        long tGpu = Time(() => Adjust16.TryRenderDisplayGpu(big, 0.2, 0.1, 0, 0, 1, null, out _, extra: simple));
        Console.WriteLine($"INFO  gpu display: 4.4 MP gain+contrast+saturation+display CPU {tCpu} ms, GPU display {tGpu} ms");
        Check(tGpu <= tCpu, "gpu display: GPU path is not slower than the CPU path");
    }
}
