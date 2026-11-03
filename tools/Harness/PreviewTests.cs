using System;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Imaging;
using ImgViewer.Services;

internal static partial class Program
{
    private static void TestPreview()
    {
        // ToDisplay (parallel, LUT) matches the plain quantization with the 4x4 ordered dither.
        float[] bayer =
        {
            -0.43750f, +0.31250f, -0.31250f, +0.43750f,
            +0.31250f, -0.43750f, +0.43750f, -0.31250f,
            -0.31250f, +0.43750f, -0.43750f, +0.31250f,
            +0.43750f, -0.31250f, +0.31250f, -0.43750f
        };
        static byte Q(ushort v, float d)
        {
            int i = (int)(v * (255.0f / 65535.0f) + d + 0.5f);
            return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
        }
        var img = new Raw16Image(37, 23);
        var rnd = new Random(5);
        for (int i = 0; i < img.R.Length; i++) { img.R[i] = (ushort)rnd.Next(65536); img.G[i] = (ushort)rnd.Next(65536); img.B[i] = (ushort)rnd.Next(65536); }
        foreach (bool dither in new[] { true, false })
        {
            var bmp = img.ToDisplay(dither);
            var px = new byte[img.W * img.H * 4];
            bmp.CopyPixels(px, img.W * 4, 0);
            bool ok = bmp.Format == PixelFormats.Bgra32 && bmp.IsFrozen;
            for (int y = 0; y < img.H && ok; y++)
                for (int x = 0; x < img.W; x++)
                {
                    int i = y * img.W + x, p = i * 4;
                    float d = dither ? bayer[(y & 3) * 4 + (x & 3)] : 0f;
                    if (px[p] != Q(img.B[i], d) || px[p + 1] != Q(img.G[i], d) || px[p + 2] != Q(img.R[i], d) || px[p + 3] != 255) { ok = false; break; }
                }
            Check(ok, $"preview: ToDisplay matches the reference quantization (dither={dither})");
        }

        // Downscale: area average, size and identity.
        var flat = new Raw16Image(400, 300);
        for (int i = 0; i < flat.R.Length; i++) { flat.R[i] = 1000; flat.G[i] = 30000; flat.B[i] = 65535; }
        var ds = flat.Downscale(100);
        Check(ds.W == 100 && ds.H == 75 && ds.R[0] == 1000 && ds.G[ds.G.Length - 1] == 30000 && ds.B[1234] == 65535,
              $"preview: downscale keeps flat colour and aspect ({ds.W}x{ds.H})");
        Check(ReferenceEquals(flat.Downscale(400), flat) && ReferenceEquals(flat.Downscale(0), flat), "preview: downscale to full size returns the image");
        var chk = new Raw16Image(4, 2);
        chk.G[0] = 100; chk.G[1] = 300; chk.G[4] = 500; chk.G[5] = 700; // 2x2 block -> 400
        var c2 = chk.Downscale(2);
        Check(c2.W == 2 && c2.H == 1 && c2.G[0] == 400 && c2.G[1] == 0, $"preview: downscale averages the covered area ({c2.G[0]})");

        // Target size: fit zoom on a 24 MP photo in a 2560 px window, sharp doubles, full and 100 % zoom use the image.
        double fitZoom = 2560.0 / 6000.0;
        int tw = PreviewQuality.TargetLong(PreviewQuality.Window, 6000, fitZoom, 6000);
        int ts = PreviewQuality.TargetLong(PreviewQuality.Sharp, 6000, fitZoom, 6000);
        Check(tw >= 2560 && tw < 2560 + PreviewQuality.Step && tw % PreviewQuality.Step == 0, $"preview: window target covers the screen ({tw})");
        Check(ts == 0 || ts >= 5120, $"preview: sharp target is twice the screen or full ({ts})");
        Check(PreviewQuality.TargetLong(PreviewQuality.Full, 6000, fitZoom, 6000) == 0 &&
              PreviewQuality.TargetLong(PreviewQuality.Window, 6000, 1.0, 6000) == 0 &&
              PreviewQuality.TargetLong(PreviewQuality.Window, 6000, 0.1, 6000) == 1024,
              "preview: full setting / 100 % zoom render the image, tiny zoom keeps a 1024 px floor");
        Check(PreviewQuality.Normalize("bogus") == PreviewQuality.Window && PreviewQuality.Normalize(null) == PreviewQuality.Window,
              "preview: unknown setting falls back to window");

        // Rendering the downscaled copy looks like the full render downscaled (local filters scale their radius).
        const int W = 3000, H = 2000;
        var src = new Raw16Image(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                double u = x / (double)W, v = y / (double)H;
                double blob = Math.Exp(-((u - 0.4) * (u - 0.4) + (v - 0.5) * (v - 0.5)) * 18);
                double baseL = 0.02 + 0.5 * u * v + 0.4 * blob;
                src.R[i] = (ushort)(Math.Clamp(baseL * 1.1, 0, 1) * 65535);
                src.G[i] = (ushort)(Math.Clamp(baseL * 0.9, 0, 1) * 65535);
                src.B[i] = (ushort)(Math.Clamp(baseL * (0.6 + 0.5 * v), 0, 1) * 65535);
            }
        var adj = new PhotoAdjustments { Contrast = 0.3, Saturation = 1.3, Clarity = 0.5, Highlights = -0.4, Dehaze = 0.2 };
        var fullOut = Adjust16.Apply(src, 0.4, 1.0, null, out _, extra: adj).Downscale(1000);
        var small = src.Downscale(1000);
        var smallOut = Adjust16.Apply(small, 0.4, 1.0, null, out _, extra: adj);
        double sum = 0;
        for (int i = 0; i < smallOut.R.Length; i++)
            sum += Math.Abs(smallOut.R[i] - fullOut.R[i]) + Math.Abs(smallOut.G[i] - fullOut.G[i]) + Math.Abs(smallOut.B[i] - fullOut.B[i]);
        double mean = sum / (3.0 * smallOut.R.Length) / 65535.0 * 255.0;
        Check(smallOut.W == fullOut.W && mean < 1.5, $"preview: proxy render matches the full render downscaled (mean diff {mean:0.00}/255)");

        // Timing (informative): full 24 MP vs window-sized preview of the same adjustments.
        var big = new Raw16Image(6000, 4000);
        for (int i = 0; i < big.R.Length; i++) { big.R[i] = (ushort)(i * 7); big.G[i] = (ushort)(i * 13); big.B[i] = (ushort)(i * 3); }
        var simple = new PhotoAdjustments { Contrast = 0.2, Saturation = 1.2 };
        long Time(Func<Raw16Image> f)
        {
            f().ToDisplay(true);
            var sw = Stopwatch.StartNew();
            f().ToDisplay(true);
            return sw.ElapsedMilliseconds;
        }
        int t = PreviewQuality.TargetLong(PreviewQuality.Window, 6000, fitZoom, 6000);
        var proxy = big.Downscale(t);
        long tFull = Time(() => Adjust16.TryApplyGpu(big, 0, 1, null, out _, extra: simple) ?? Adjust16.Apply(big, 0, 1, null, out _, extra: simple));
        long tProxy = Time(() => Adjust16.TryApplyGpu(proxy, 0, 1, null, out _, extra: simple) ?? Adjust16.Apply(proxy, 0, 1, null, out _, extra: simple));
        Console.WriteLine($"INFO  preview: contrast+saturation render+display 24 MP {tFull} ms, window preview {proxy.W}x{proxy.H} {tProxy} ms");
        Check(tProxy < tFull, "preview: window-sized preview renders faster than the full image");
    }
}
