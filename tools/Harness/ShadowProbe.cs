using System;
using System.Windows.Media.Imaging;
using ImgViewer.Imaging;
using ImgViewer.Services;

internal static partial class Program
{
    // Shadows on a dark saturated red must brighten it without turning it into a bright, more saturated red.
    private static void TestShadowColor()
    {
        const int W = 2048, H = 2048;
        var buf = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            bool red = (i % W) < W / 2;
            buf[i * 4 + 2] = red ? (byte)83 : (byte)34;
            buf[i * 4 + 1] = red ? (byte)20 : (byte)34;
            buf[i * 4] = red ? (byte)10 : (byte)34;
            buf[i * 4 + 3] = 255;
        }
        var b = (byte[])buf.Clone();
        PhotoOps.ApplyShadowsHighlights(b, W, H, 0.0, 1.0);
        int r0 = H / 2 * W * 4 + 4, g0 = H / 2 * W * 4 + (W - 2) * 4;
        double y0 = 0.2126 * 83 + 0.7152 * 20 + 0.0722 * 10;
        double y1 = 0.2126 * b[r0 + 2] + 0.7152 * b[r0 + 1] + 0.0722 * b[r0];
        double c0 = 83 - 10, c1 = b[r0 + 2] - b[r0];
        Check(y1 > y0 * 1.3 && c1 < c0 * 1.35 && b[r0 + 2] < 140,
              $"shadows: dark red is lifted, not turned bright red (Y {y0:0}->{y1:0}, R {b[r0 + 2]}, chroma {c0:0}->{c1:0})");
        Check(b[g0] > 60 && b[g0] == b[g0 + 1] && b[g0 + 1] == b[g0 + 2], $"shadows: dark gray lifted and stays neutral ({b[g0]})");

        var img = new Raw16Image(W, H);
        for (int i = 0; i < W * H; i++)
        {
            bool red = (i % W) < W / 2;
            img.R[i] = red ? (ushort)5600 : (ushort)1100;
            img.G[i] = red ? (ushort)420 : (ushort)1100;
            img.B[i] = red ? (ushort)160 : (ushort)1100;
        }
        var cpu = Adjust16.Apply(img, 1.0, 1.0, null, out _);
        var flat = Adjust16.Apply(img, 0.0, 1.0, null, out _);
        var gpu = Adjust16.TryApplyGpu(img, 1.0, 1.0, null, out _);
        int ri = H / 2 * W + 1;
        static double Sat(Raw16Image m, int i) => (m.R[i] - (double)Math.Min(m.G[i], m.B[i])) / Math.Max(1, (int)m.R[i]);
        static double Lum(Raw16Image m, int i) => 0.2126 * m.R[i] + 0.7152 * m.G[i] + 0.0722 * m.B[i];
        Check(Lum(cpu, ri) > Lum(flat, ri) * 1.2 && Sat(cpu, ri) <= Sat(flat, ri) + 0.01 && Sat(cpu, ri) > Sat(flat, ri) - 0.25,
              $"shadows 16-bit: dark red lifted (Y {Lum(flat, ri):0}->{Lum(cpu, ri):0}), saturation kept or slightly lower ({Sat(flat, ri):0.00}->{Sat(cpu, ri):0.00})");
        if (gpu is not null)
        {
            int md = 0;
            for (int i = 0; i < W * H; i++)
                md = Math.Max(md, Math.Max(Math.Abs(gpu.R[i] - cpu.R[i]), Math.Max(Math.Abs(gpu.G[i] - cpu.G[i]), Math.Abs(gpu.B[i] - cpu.B[i]))));
            Check(md <= 64, $"shadows: GPU matches CPU (max diff {md})");
        }
    }

    // Shadows slider diagnostics: per-channel change and chroma in dark / mid areas (8-bit path, preview size).
    private static void ShadowProbe(string path, double s)
    {
        var info = ImageLoader.GetInfo(path);
        var src = ImageLoader.LoadScaled(path, info, 700, 700);
        var a = AdjustmentsProcessor.ToBgraBuffer(src, out _);
        var outB = AdjustmentsProcessor.Apply(src, s, 1.0, null, out _)!;
        var b = AdjustmentsProcessor.ToBgraBuffer(outB, out _);
        var outDir = Environment.GetEnvironmentVariable("SHPROBE_OUT");
        if (!string.IsNullOrEmpty(outDir))
        {
            foreach (var (bmp, name) in new[] { (src, "sh_before.png"), ((BitmapSource)outB, Environment.GetEnvironmentVariable("SHPROBE_NAME") ?? "sh_after.png") })
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = System.IO.File.Create(System.IO.Path.Combine(outDir, name));
                enc.Save(fs);
            }
        }
        foreach (var (lo, hi) in new[] { (0, 40), (40, 80), (80, 130), (130, 256) })
        {
            double[] s0 = new double[3], s1 = new double[3];
            double c0 = 0, c1 = 0, clipR = 0; long n = 0;
            for (int i = 0; i < a.Length; i += 4)
            {
                double l = 0.2126 * a[i + 2] + 0.7152 * a[i + 1] + 0.0722 * a[i];
                if (l < lo || l >= hi) continue;
                n++;
                for (int c = 0; c < 3; c++) { s0[c] += a[i + 2 - c]; s1[c] += b[i + 2 - c]; }
                c0 += Math.Max(a[i + 2], Math.Max(a[i + 1], a[i])) - Math.Min(a[i + 2], Math.Min(a[i + 1], a[i]));
                c1 += Math.Max(b[i + 2], Math.Max(b[i + 1], b[i])) - Math.Min(b[i + 2], Math.Min(b[i + 1], b[i]));
                if (b[i + 2] == 255 && a[i + 2] < 250) clipR++;
            }
            if (n == 0) continue;
            Console.WriteLine($"luma {lo,3}-{hi,3} n={n,8}  RGB {s0[0] / n:0.0},{s0[1] / n:0.0},{s0[2] / n:0.0} -> {s1[0] / n:0.0},{s1[1] / n:0.0},{s1[2] / n:0.0}  chroma {c0 / n:0.0} -> {c1 / n:0.0}  newRclip {clipR / n:P2}");
        }
    }
}
