using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Controls;
using ImgViewer.Imaging;
using ImgViewer.Services;

internal static class Program
{
    private static int _fails;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            DiagStretch(args);
            return;
        }
        TestCentering();
        TestDpiNormalization();
        TestShadows();
        TestGamma();
        TestHistogram();
        TestAutoStretch();

        Console.WriteLine(_fails == 0 ? "ALL OK" : $"FAILURES: {_fails}");
        Environment.ExitCode = _fails;
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok) _fails++;
    }

    private static BitmapSource MakeSource(int w, int h, double dpiX = 96, double dpiY = 96)
    {
        int stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                buf[row + x * 4 + 0] = (byte)x;
                buf[row + x * 4 + 1] = (byte)y;
                buf[row + x * 4 + 2] = 128;
                buf[row + x * 4 + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(w, h, dpiX, dpiY, PixelFormats.Bgra32, null, buf, stride);
        bmp.Freeze();
        return bmp;
    }

    private static ImageViewer MakeViewer(Size viewport, BitmapSource src, Size native)
    {
        var viewer = new ImageViewer();
        viewer.Measure(viewport);
        viewer.Arrange(new Rect(new Point(0, 0), viewport));
        viewer.SetSource(src, native);
        return viewer;
    }

    private static void TestCentering()
    {
        var src = MakeSource(1500, 1000);
        var native = new Size(3000, 2000);
        var vp = new Size(800, 533);
        var v = MakeViewer(vp, src, native);

        double ox0 = v.TestOffsetX;
        double oy0 = v.TestOffsetY;
        double dw = v.TestDisplayedWidth;
        double dh = v.TestDisplayedHeight;
        Check(Math.Abs(ox0 - (800 - dw) / 2) < 0.6 && Math.Abs(oy0 - (533 - dh) / 2) < 0.6,
              $"fit centered (ox={ox0:R} oy={oy0:R} disp={dw:0.#}x{dh:0.#})");

        v.PanForTest(60, 40);
        Check(Math.Abs(v.TestOffsetX - ox0) < 0.01 && Math.Abs(v.TestOffsetY - oy0) < 0.01,
              "pan blocked when image smaller");

        v.SetZoom(2.0);
        double ox1 = v.TestOffsetX;
        double oy1 = v.TestOffsetY;
        dw = v.TestDisplayedWidth;
        dh = v.TestDisplayedHeight;
        Check(dw > 800 && ox1 <= 0.01 && ox1 >= 800 - dw - 0.01 && oy1 <= 0.01 && oy1 >= 533 - dh - 0.01,
              $"zoom clamped, no blank edges (ox={ox1:R} oy={oy1:R} disp={dw:0.#}x{dh:0.#})");

        v.PanForTest(120, -70);
        Check(Math.Abs(v.TestOffsetX - Math.Clamp(ox1 + 120, 800 - dw, 0)) < 0.01
              && Math.Abs(v.TestOffsetY - Math.Clamp(oy1 - 70, 533 - dh, 0)) < 0.01,
              "pan works when image bigger");

        v.Measure(new Size(1000, 667));
        v.Arrange(new Rect(new Point(0, 0), new Size(1000, 667)));
        ox1 = v.TestOffsetX;
        oy1 = v.TestOffsetY;
        Check(ox1 <= 0.01 && ox1 >= 1000 - dw - 0.01 && oy1 <= 0.01 && oy1 >= 667 - dh - 0.01,
              "resize: still clamped");

        v.SetZoom(0.2);
        ox1 = v.TestOffsetX;
        oy1 = v.TestOffsetY;
        dw = v.TestDisplayedWidth;
        dh = v.TestDisplayedHeight;
        Check(dw <= 1000 && Math.Abs(ox1 - (1000 - dw) / 2) < 0.6 && Math.Abs(oy1 - (667 - dh) / 2) < 0.6,
              $"zoom-out below viewport recenters (ox={ox1:R} oy={oy1:R} disp={dw:0.#}x{dh:0.#})");

        Check(Math.Abs(v.Zoom - 0.2) < 0.01, $"zoom value roundtrip ({v.Zoom:0.###})");
    }

    private static void TestDpiNormalization()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opencode");
        Directory.CreateDirectory(dir);

        foreach (double dpi in new[] { 72.0, 350.0 })
        {
            string path = Path.Combine(dir, $"dpi_{(int)dpi}.png");
            var src = MakeSource(120, 80, dpi, dpi);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(path))
            {
                enc.Save(fs);
            }

            var info = ImageLoader.GetInfo(path);
            var loaded = ImageLoader.LoadScaled(path, info, 4096, 4096);
            Check(Math.Abs(loaded.DpiX - 96) < 0.01 && Math.Abs(loaded.DpiY - 96) < 0.01
                  && loaded.PixelWidth == 120 && loaded.PixelHeight == 80,
                  $"dpi {dpi} -> 96 (got {loaded.DpiX:0.#}, {loaded.PixelWidth}x{loaded.PixelHeight})");
        }
    }

    private static void TestShadows()
    {
        var src = MakeFlat(255, 240, 240, 240, 120, 120, 120, 10, 10, 10);
        var out1 = AdjustmentsProcessor.Apply(src, 0.5, 1.0, autoStretch: false);
        var out0 = AdjustmentsProcessor.Apply(src, 0.0, 1.0);
        Check(out1 is not null && out0 is not null, "shadows apply");

        var p = ReadPixels(out1!);
        var q0 = ReadPixels(out0!);
        Check(diff(p.dark, q0.dark) >= 6, $"dark lifted from near-black ({p.dark.g} vs {q0.dark.g})");
        Check(diff(p.mid, q0.mid) >= 10 && diff(p.mid, q0.mid) <= 45, $"mid lifted moderately ({p.mid.g} vs {q0.mid.g})");
        Check(diff(p.bright, q0.bright) <= 3, $"bright untouched ({p.bright.g} vs {q0.bright.g})");
        Check(p.bright.g - p.bright.b <= 2, "highlight hue preserved");

        var outN = AdjustmentsProcessor.Apply(src, -0.5, 1.0, autoStretch: false);
        var pN = ReadPixels(outN!);
        Check(pN.bright.g <= q0.bright.g, "darkened bright not above base");
        Check(diff(pN.dark, q0.dark) > 8, $"dark darkened heavily ({pN.dark.g} vs {q0.dark.g})");
        Check(Math.Abs(pN.mid.g - q0.mid.g) >= 8 && Math.Abs(pN.mid.g - q0.mid.g) <= 40, $"mid darkened moderately ({pN.mid.g} vs {q0.mid.g})");
        Check(diff(pN.bright, q0.bright) <= 3, "bright untouched on darken");
    }

    private static void TestGamma()
    {
        var src = MakeFlat(255, 240, 240, 240, 120, 120, 120, 10, 10, 10);
        var out1 = AdjustmentsProcessor.Apply(src, 0.0, 1.4);
        var out0 = AdjustmentsProcessor.Apply(src, 0.0, 1.0);
        Check(diff(ReadPixels(out1!).mid, ReadPixels(out0!).mid) > 8, "gamma brightens midtones");
        Check(Math.Abs(ReadPixels(out1!).bright.g - 240) <= 5, "gamma keeps near-white (≤5 lvl)");
    }

    private static int diff((byte b, byte g, byte r) a, (byte b, byte g, byte r) c) => Math.Abs(a.g - c.g);

    private static void TestHistogram()
    {
        foreach (var v in new[] { 10, 120, 240 })
        {
            var src = MakeFlat(64, v, v, v, v, v, v, v, v, v);
            var hist = AdjustmentsProcessor.ComputeHistogram(src);
            Check(hist is not null, $"histogram compute {v}");
            Check(ArgMax(hist!.Lum) == v, $"lum peak=={v} (got {ArgMax(hist.Lum)})");
            Check(hist.R[v] == 192 && hist.G[v] == 192 && hist.B[v] == 192, $"per-channel bin {v}");
        }

        var flat = MakeFlat(64, 240, 240, 240, 120, 120, 120, 10, 20, 30);
        var hist2 = AdjustmentsProcessor.Apply(flat, 0.0, 1.0, out var h2);
        Check(hist2 is not null && h2 is not null, "apply histogram out");
        Check(h2!.Lum[19] == 64 && h2.Lum[120] == 64 && h2.Lum[240] == 64, "apply-out lum bins distinct");
    }

    private static void TestAutoStretch()
    {
        var ramp = MakeRampSource();
        var without = AdjustmentsProcessor.Apply(ramp, 0.0, 1.0);
        var w0 = ReadRow(without!, 0);
        var w2 = ReadRow(without!, 2);
        Check(w0.g == 64 && w2.g == 191, "neutral leaves narrow range intact");

        var st = AdjustmentsProcessor.ComputeAutoStretch(ramp);
        Check(st is not null && st.Length == 2 && st[0] == 64 && st[1] == 190,
              $"auto-stretch bounds lo=64 hi=190 (got {Fmt(st)})");

        var outS = AdjustmentsProcessor.Apply(ramp, 0.5, 1.0);
        var p = ReadRow(outS!, 0);
        var m = ReadRow(outS!, 1);
        var b = ReadRow(outS!, 2);
        Check(p.g <= 1, $"stretched dark anchored at 0 ({p.g})");
        Check(b.g >= 252, $"stretched bright near 255 ({b.g})");
        Check(m.g is >= 120 and <= 160, $"stretched mid lifted moderately ({m.g})");

        var full = MakeFlat(255, 255, 255, 255, 120, 120, 120, 0, 0, 0);
        Check(AdjustmentsProcessor.ComputeAutoStretch(full) is null, "full range -> no stretch");

        var flatS = MakeFlat(64, 128, 128, 128, 128, 128, 128, 128, 128, 128);
        Check(AdjustmentsProcessor.ComputeAutoStretch(flatS) is null, "flat image -> no stretch");

        var wb = MakeWbSource();
        Check(AdjustmentsProcessor.ComputeAutoStretch(wb) is not null, "wb fixture triggers stretch");
        var wbOut = AdjustmentsProcessor.Apply(wb, 0.45, 1.0);
        int Spread(int row, int col)
        {
            var px = ReadRow(wbOut!, row, col);
            return Math.Max(Math.Abs(px.r - px.g), Math.Max(Math.Abs(px.g - px.b), Math.Abs(px.r - px.b)));
        }
        Check(Spread(0, 25) <= 1 && Spread(0, 50) <= 1 && Spread(0, 75) <= 1 && Spread(1, 50) <= 1,
              $"neutral grays stay neutral under stretch+shadows ({Spread(0, 25)},{Spread(0, 50)},{Spread(0, 75)},{Spread(1, 50)})");
    }

    private static BitmapSource MakeWbSource()
    {
        const int w = 100, h = 6;
        int stride = w * 4;
        var buf = new byte[stride * h];
        for (int x = 0; x < w; x++)
        {
            int vn = 30 + x * 170 / (w - 1);
            for (int row = 0; row < 3; row++)
            {
                buf[row * stride + x * 4 + 0] = (byte)vn;
                buf[row * stride + x * 4 + 1] = (byte)vn;
                buf[row * stride + x * 4 + 2] = (byte)vn;
                buf[row * stride + x * 4 + 3] = 255;
            }
            int v = x * 255 / (w - 1);
            for (int row = 3; row < 6; row++)
            {
                buf[row * stride + x * 4 + 0] = (byte)v;
                buf[row * stride + x * 4 + 1] = (byte)(v / 2);
                buf[row * stride + x * 4 + 2] = (byte)(v / 4);
                buf[row * stride + x * 4 + 3] = 255;
            }
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        src.Freeze();
        return src;
    }

    private static void DiagStretch(string[] paths)
    {
        foreach (var path in paths)
        {
            var info = ImageLoader.GetInfo(path);
            var img = ImageLoader.LoadScaled(path, info, 4096, 4096);
            int w = img.PixelWidth, h = img.PixelHeight;
            int stride = w * 4;
            var buf = new byte[(long)stride * h];
            img.CopyPixels(buf, stride, 0);

            long n = (long)w * h;
            Console.WriteLine($"{Path.GetFileName(path)}  {w}x{h}  n={n}");
            string[] names = { "B", "G", "R" };
            for (int c = 0; c < 3; c++)
            {
                var hist = new long[256];
                for (long i = c; i < buf.Length; i += 4) hist[buf[i]]++;
                long cum = 0; int minV = -1, maxV = -1;
                for (int v = 0; v < 256; v++) if (hist[v] > 0) { if (minV < 0) minV = v; maxV = v; }
                var sb = new System.Text.StringBuilder();
                foreach (double p in new[] { 0.001, 0.005, 0.01, 0.02 })
                {
                    long t = (long)(n * p); if (t < 1) t = 1;
                    long lo = t, hi = n - t; cum = 0; int loV = 0, hiV = 255;
                    for (int v = 0; v < 256; v++) { cum += hist[v]; if (cum >= lo) { loV = v; break; } }
                    cum = 0;
                    for (int v = 0; v < 256; v++) { cum += hist[v]; if (cum > hi) { hiV = v - 1; break; } }
                    sb.Append($"  p{p * 100:0.##}%=[{loV},{hiV}]");
                }
                Console.WriteLine($"  {names[c]} min={minV} max={maxV}{sb}");
            }
            var st = AdjustmentsProcessor.ComputeAutoStretch(img);
            Console.WriteLine(st is null
                ? "  ComputeAutoStretch: NULL (no stretch)"
                : $"  ComputeAutoStretch (lum): [{st[0]},{st[1]}]");

            var proc = AdjustmentsProcessor.Apply(img, 0.45, 1.0, out var histOut);
            if (proc is not null)
            {
                int pw = proc.PixelWidth;
                int ps = pw * 4;
                var pbuf = new byte[(long)ps * proc.PixelHeight];
                proc.CopyPixels(pbuf, ps, 0);
                var hOut = new int[3][];
                for (int c = 0; c < 3; c++) hOut[c] = c == 0 ? histOut!.B : c == 1 ? histOut!.G : histOut!.R;
                Console.Write("  shadows 45% output p0.5%:");
                for (int c = 2; c >= 0; c--)
                {
                    var hd = hOut[c];
                    long cum = 0; int loV = 0, hiV = 255;
                    long t = (long)((long)pw * proc.PixelHeight * 0.005); if (t < 1) t = 1;
                    for (int v = 0; v < 256; v++) { cum += hd[v]; if (cum >= t) { loV = v; break; } }
                    cum = 0;
                    for (int v = 0; v < 256; v++) { cum += hd[v]; if (cum > (long)pw * proc.PixelHeight - t) { hiV = v - 1; break; } }
                    Console.Write($" {names[c]}=[{loV},{hiV}]");
                }
                Console.WriteLine();
            }
        }
    }

    private static (byte b, byte g, byte r) ReadRow(WriteableBitmap bmp, int row, int col = 0)
    {
        var buf = new byte[4];
        bmp.CopyPixels(new Int32Rect(col, row, 1, 1), buf, 4, 0);
        return (buf[0], buf[1], buf[2]);
    }

    private static string Fmt(byte[]? st) =>
        st is null ? "null" : string.Join(",", st);

    private static BitmapSource MakeRampSource()
    {
        const int w = 100, h = 100;
        int stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            byte v = y switch
            {
                0 => (byte)64,
                1 => (byte)128,
                2 => (byte)191,
                _ => (byte)Math.Min(190, 65 + (y - 3) * 126 / 97)
            };
            for (int x = 0; x < w; x++)
            {
                buf[y * stride + x * 4 + 0] = v;
                buf[y * stride + x * 4 + 1] = v;
                buf[y * stride + x * 4 + 2] = v;
                buf[y * stride + x * 4 + 3] = 255;
            }
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        src.Freeze();
        return src;
    }

    private static int ArgMax(int[] arr)
    {
        int idx = 0;
        int best = -1;
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i] > best)
            {
                best = arr[i];
                idx = i;
            }
        }
        return idx;
    }

    private static BitmapSource MakeFlat(int w, int br, int bg, int bb, int mr, int mg, int mb, int dr, int dg, int db)
    {
        var pixels = new (int r, int g, int b)[] { (br, bg, bb), (mr, mg, mb), (dr, dg, db) };
        int stride = w * 4;
        var buf = new byte[stride * 3];
        for (int row = 0; row < 3; row++)
        {
            for (int x = 0; x < w; x++)
            {
                buf[row * stride + x * 4 + 0] = (byte)pixels[row].b;
                buf[row * stride + x * 4 + 1] = (byte)pixels[row].g;
                buf[row * stride + x * 4 + 2] = (byte)pixels[row].r;
                buf[row * stride + x * 4 + 3] = 255;
            }
        }
        var src = BitmapSource.Create(w, 3, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        src.Freeze();
        return src;
    }

    private static ((byte b, byte g, byte r) dark, (byte b, byte g, byte r) mid, (byte b, byte g, byte r) bright)
        ReadPixels(WriteableBitmap bmp)
    {
        int w = bmp.PixelWidth;
        var buf = new byte[w * 3 * 4];
        bmp.CopyPixels(buf, w * 4, 0);
        (byte, byte, byte) px(int row)
        {
            int o = row * w * 4;
            return (buf[o], buf[o + 1], buf[o + 2]);
        }
        return (px(2), px(1), px(0));
    }
}
