using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Controls;
using ImgViewer.Imaging;
using ImgViewer.Services;
using ImgViewer;

internal static class Program
{
    private static int _fails;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe")
        {
            ProbeDecode(args[1]);
            return;
        }
        if (args.Length > 0 && args[0] == "--exif6")
        {
            MakeExifJpegBySplice(args[1], args[2], 6);
            Console.WriteLine($"OK {args[2]}");
            return;
        }
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
        TestManualStretch();
        TestMono();
        TestTriX();
        TestCrossImageSwitch();
        TestRaw();
        TestPhotoExtras();
        TestExifRotation();

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

        var tiny = AdjustmentsProcessor.Apply(ramp, 0.01, 1.0);
        int t0 = Math.Abs(ReadRow(tiny!, 0).g - 64);
        int t1 = Math.Abs(ReadRow(tiny!, 1).g - 128);
        int t2 = Math.Abs(ReadRow(tiny!, 2).g - 191);
        Check(t0 <= 6 && t1 <= 6 && t2 <= 6, $"tiny nudge +-1% stays subtle ({t0},{t1},{t2})");

        var half = AdjustmentsProcessor.Apply(ramp, 0.25, 1.0);
        int h0 = ReadRow(half!, 0).g;
        Check(h0 > 0 && h0 < 64, $"stretch scales with slider (25%: 64->{h0})");

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

    private static void TestManualStretch()
    {
        var map = new ManualStretchMap();
        Check(!map.HasEffect && map.Count == 0, "manual map starts neutral");
        Check(Near(map.Map(0.25), 0.25) && Near(map.Map(0.9), 0.9), "neutral map is identity");

        map.Upsert(0.5, 0.7);
        Check(Near(map.Map(0.5), 0.7), "knot output honored");
        Check(Near(map.Map(0.25), 0.35), "left side stretches toward knot");
        Check(Near(map.Map(0.75), 0.85), "right side compresses toward knot");
        Check(Near(map.Map(0.0), 0.0) && Near(map.Map(1.0), 1.0), "manual endpoints fixed");
        Check(Near(map.Inverse(0.7), 0.5), "inverse maps knot back");
        Check(Near(map.Inverse(0.85), 0.75), "inverse on right segment");

        bool monotonic = true;
        double prev = 0;
        for (int i = 0; i <= 255; i++)
        {
            double v = map.Map(i / 255.0);
            if (v < prev - 1e-9) monotonic = false;
            prev = v;
        }
        Check(monotonic, "manual map monotonic");

        var src = MakeRampSource();
        var plain = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false);
        Check(plain is not null, "apply with null manual");
        var stretched = AdjustmentsProcessor.Apply(src, 0.0, 1.0, map, out _, autoStretch: false);
        Check(stretched is not null, "apply with manual map");
        var r0 = ReadRow(stretched!, 0);
        var r1 = ReadRow(stretched!, 1);
        var r2 = ReadRow(stretched!, 2);
        int exp0 = (int)Math.Round(map.Map(64 / 255.0) * 255.0);
        int exp1 = (int)Math.Round(map.Map(128 / 255.0) * 255.0);
        int exp2 = (int)Math.Round(map.Map(191 / 255.0) * 255.0);
        Check(Math.Abs(r0.g - exp0) <= 1 && Math.Abs(r1.g - exp1) <= 1 && Math.Abs(r2.g - exp2) <= 1,
              $"manual map applied to pixels ({r0.g}/{exp0}, {r1.g}/{exp1}, {r2.g}/{exp2})");
        Check(r0.r == r0.g && r0.g == r0.b && r2.r == r2.g && r2.g == r2.b, "manual map preserves gray");

        var clone = map.Clone();
        clone.Upsert(0.2, 0.05);
        Check(map.Count == 1 && clone.Count == 2, "clone isolation");
        Check(Near(map.Map(0.25), 0.35), "clone edit does not leak into original");

        var empty = new ManualStretchMap();
        Check(!empty.HasEffect, "empty map has no effect");

        var neutral = new ManualStretchMap();
        Check(SamePixels(
            AdjustmentsProcessor.Apply(src, 0.0, 1.0, neutral, out _, autoStretch: false)!,
            AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false)!),
              "empty manual map applies as identity");
    }

    private static bool SamePixels(WriteableBitmap a, WriteableBitmap b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        int n = a.PixelWidth * a.PixelHeight * 4;
        var ba = new byte[n];
        var bb = new byte[n];
        a.CopyPixels(ba, a.PixelWidth * 4, 0);
        b.CopyPixels(bb, b.PixelWidth * 4, 0);
        for (int i = 0; i < n; i++)
            if (ba[i] != bb[i]) return false;
        return true;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;

    private static void TestTriX()
    {
        var src = MakeFlat(255, 255, 255, 255, 128, 128, 128, 0, 0, 0);
        var a1 = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false, MonoMode.TriX, 777);
        var a2 = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false, MonoMode.TriX, 777);
        var b = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false, MonoMode.TriX, 911);
        Check(a1 is not null && SamePixels(a1, a2), "trix grain deterministic (same seed)");
        Check(!SamePixels(a1, b), "trix grain differs (different seed)");

        var m = ReadPixels(a1!);
        Check(m.dark.b == m.dark.g && m.dark.g == m.dark.r, "trix channels collapsed");
        Check(m.dark.g <= 8 && m.dark.g >= 1, "trix black toe deep");
        Check(m.bright.g >= 250, "trix white shoulder holds");

        var doc = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false, MonoMode.Documentary, 777);
        float trixMid = RowMean(a1!, 1);
        float docMid = RowMean(doc!, 1);
        Check(trixMid > docMid + 3.0f, $"trix midtone punchier (trix {trixMid:0.##} vs doc {docMid:0.##})");

        var ramp = MakeRampSource();
        var rm = AdjustmentsProcessor.Apply(ramp, 0.0, 1.0, null, out _, autoStretch: false, MonoMode.TriX, 5);
        int r0 = ReadRow(rm!, 0).g, r1 = ReadRow(rm!, 1).g, r2 = ReadRow(rm!, 2).g;
        Check(r0 < r1 && r1 < r2, "trix monotonic on ramp");

        float midNoise = RowMean(a1!, 1);
        Check(Math.Abs(midNoise - 140) < 12, $"grain mean near trix curve mid ({midNoise:0.#})");
    }

    private static void TestRaw()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "imgviewer_rawtest"));
        foreach (var f in new[] { "sample1.rw2", "sample1.nef" })
        {
            string src = Path.Combine(Path.GetTempPath(), "opencode", "rawsamples", f);
            if (!File.Exists(src))
            {
                Check(false, $"raw sample present: {f}");
                continue;
            }
            Check(ImageLoader.IsRaw(src), $"raw extension detected ({f})");

            var info = ImageLoader.GetInfo(src);
            Check(info.Width > 0 && info.Height > 0, $"raw info dims {f}: {info.Width}x{info.Height}");
            Check(info.RotatedQuarter is false, $"raw orientation baked ({f})");

            var half = ImageLoader.LoadScaled(src, info, info.Width, info.Height);
            Check(half is not null, $"raw full load ok ({f})");
            if (half is null) continue;

            var buf = new byte[half.PixelWidth * 4];
            half.CopyPixels(new Int32Rect(0, half.PixelHeight / 2, half.PixelWidth, 1), buf, half.PixelWidth * 4, 0);
            double bMean = 0, gMean = 0, rMean = 0;
            for (int i = 0; i < half.PixelWidth; i++)
            {
                bMean += buf[i * 4];
                gMean += buf[i * 4 + 1];
                rMean += buf[i * 4 + 2];
            }
            bMean /= half.PixelWidth; gMean /= half.PixelWidth; rMean /= half.PixelWidth;
            Check(bMean > 5 && gMean > 5 && rMean > 5 && bMean < 250, $"raw midrow channels sane ({f}) B={bMean:0.#} G={gMean:0.#} R={rMean:0.#}");
            Check(bMean != gMean || gMean != rMean, $"raw color not collapsed ({f})");

            var processed = AdjustmentsProcessor.Apply(half, -10.0, 1.2, null, out _, autoStretch: false, MonoMode.None, 1);
            Check(processed is not null, $"raw adjustments pipeline ({f})");

            Debug.Log($"RAW TEST {f}: info={info.Width}x{info.Height} full={half.PixelWidth}x{half.PixelHeight}");
        }
    }

    private static float LumMean(BitmapSource img)
    {
        var buf = new byte[img.PixelWidth * 4];
        double total = 0;
        for (int y = 0; y < img.PixelHeight; y += Math.Max(1, img.PixelHeight / 40))
        {
            img.CopyPixels(new Int32Rect(0, y, img.PixelWidth, 1), buf, buf.Length, 0);
            for (int x = 0; x < img.PixelWidth; x++)
            {
                total += 0.2126 * buf[x * 4 + 2] + 0.7152 * buf[x * 4 + 1] + 0.0722 * buf[x * 4];
            }
        }
        return (float)(total / (img.PixelHeight / Math.Max(1, img.PixelHeight / 40)) / img.PixelWidth);
    }

    private static void TestPhotoExtras()
    {
        var src = MakeFlat(255, 255, 255, 255, 128, 128, 128, 0, 0, 0);

        var neutral = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false, extra: new PhotoAdjustments());
        Check(neutral is not null, "photo extras neutral apply");

        var exposed = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Exposure = 2.0 });
        Check(exposed is not null && LumMean(exposed) > LumMean(src) + 40,
              $"exposure brightens ({LumMean(src):0.#} -> {LumMean(exposed ?? src):0.#})");

        var desat = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Saturation = 0.0 });
        var d = desat;
        if (d is not null)
        {
            var px = ReadPixels(d);
            Check(px.dark.r == px.dark.g && px.dark.g == px.dark.b, "saturation 0 grays out");
        }

        var contrasted = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Contrast = 1.0 });
        Check(contrasted is not null, "contrast +100 applies");
        var tempSrc = MakeFlat(255, 255, 255, 255, 128, 128, 128, 60, 60, 60);
        var cooled = AdjustmentsProcessor.Apply(tempSrc, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Temperature = -1.0 });
        var cool = ReadPixels(cooled!);
        Check(cool.dark.b > cool.dark.r, $"cool temp raises blue over red (B={cool.dark.b} R={cool.dark.r})");

        var warmed = AdjustmentsProcessor.Apply(tempSrc, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Temperature = 1.0 });
        var warm = ReadPixels(warmed!);
        Check(warm.dark.r > warm.dark.b, $"warm temp raises red over blue (R={warm.dark.r} B={warm.dark.b})");

        var dehazed = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Dehaze = 1.0 });
        Check(dehazed is not null && LumMean(dehazed) < LumMean(src), "dehaze darkens flat");

        var noisy = MakeNoisySource(160, 120, seed: 7);
        var denoised = AdjustmentsProcessor.Apply(noisy, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Denoise = 1.0 });
        Check(denoised is not null && StddevRegion(denoised!, 40, 30, 60, 40) < StddevRegion(noisy, 40, 30, 60, 40),
              $"denoise lowers local stddev ({StddevRegion(noisy, 40, 30, 60, 40):0.##} -> {StddevRegion(denoised ?? noisy, 40, 30, 60, 40):0.##})");

        var blocky = MakeBlocksSource(200, 140);
        var crisp = AdjustmentsProcessor.Apply(blocky, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Clarity = 1.0 });
        var soft = AdjustmentsProcessor.Apply(blocky, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Clarity = -1.0 });
        float baseStd = StddevRegion(blocky, 20, 20, 160, 100);
        float crispStd = StddevRegion(crisp!, 20, 20, 160, 100);
        float softStd = StddevRegion(soft!, 20, 20, 160, 100);
        Check(Math.Abs(crispStd - baseStd) > 1 || Math.Abs(softStd - baseStd) > 1,
              $"clarity changes local contrast (base={baseStd:0.##} crisp={crispStd:0.##} soft={softStd:0.##})");

        var combo = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Exposure = 0.5, Contrast = 0.3, Temperature = 0.2, Saturation = 1.2, Clarity = 0.2, Dehaze = 0.3, Denoise = 0.4 });
        Check(combo is not null, "photo extras combined apply");

        TestPhotoExtras2();
    }

    private static void TestPhotoExtras2()
    {
        var split = MakeHighlightSource();
        var rec = AdjustmentsProcessor.Apply(split, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Highlights = -0.8 });
        float brightBefore = LumMeanRegion(split, 0, 0, 90, 100);
        float brightAfter = LumMeanRegion(rec!, 0, 0, 90, 100);
        float darkBefore = LumMeanRegion(split, 0, 100, 90, 100);
        float darkAfter = LumMeanRegion(rec!, 0, 100, 90, 100);
        Check(brightAfter < brightBefore - 5, $"highlights recovery darkens bright region ({brightBefore:0.#} -> {brightAfter:0.#})");
        Check(Math.Abs(darkAfter - darkBefore) < 2, $"highlights spares dark region ({darkBefore:0.#} -> {darkAfter:0.#})");

        var blocks = MakeBlocksSource(200, 140);
        var sharp = AdjustmentsProcessor.Apply(blocks, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { Sharpen = 1.0 });
        float baseStd = StddevRegion(blocks, 80, 20, 40, 100);
        float sharpStd = StddevRegion(sharp!, 80, 20, 40, 100);
        Check(sharpStd > baseStd, $"sharpen increases edge stddev ({baseStd:0.##} -> {sharpStd:0.##})");

        var chroma = MakeChromaNoiseSource(160, 120, seed: 11);
        var cnr = AdjustmentsProcessor.Apply(chroma, 0.0, 1.0, null, out _, autoStretch: false,
            extra: new PhotoAdjustments { ColorNoise = 1.0 });
        Check(cnr is not null && ChromaStddev(cnr!) < ChromaStddev(chroma),
              $"chroma NR reduces color noise ({ChromaStddev(chroma):0.###} -> {ChromaStddev(cnr ?? chroma):0.###})");

        double tWarmSample = PhotoOps.WbTemperatureFromSample(90, 130, 170);
        double tCoolSample = PhotoOps.WbTemperatureFromSample(170, 130, 90);
        double tNeutral = PhotoOps.WbTemperatureFromSample(128, 128, 128);
        Check(tWarmSample > 0.5, $"wb picker blue-ish sample -> warm correction ({tWarmSample:0.##})");
        Check(tCoolSample < -0.5, $"wb picker red-ish sample -> cool correction ({tCoolSample:0.##})");
        Check(Math.Abs(tNeutral) < 0.01, $"wb picker neutral sample -> no change ({tNeutral:0.###})");
    }

    private static BitmapSource MakeHighlightSource()
    {
        int w = 240, h = 200;
        int stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            byte v = (byte)(y < 100 ? 235 : 60);
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

    private static float LumMeanRegion(BitmapSource img, int x0, int y0, int w, int h)
    {
        var buf = new byte[w * 4];
        double total = 0;
        for (int y = 0; y < h; y++)
        {
            img.CopyPixels(new Int32Rect(x0, y0 + y, w, 1), buf, buf.Length, 0);
            for (int x = 0; x < w; x++)
            {
                total += 0.2126 * buf[x * 4 + 2] + 0.7152 * buf[x * 4 + 1] + 0.0722 * buf[x * 4];
            }
        }
        return (float)(total / Math.Max(1, w * h));
    }

    private static BitmapSource MakeChromaNoiseSource(int w, int h, int seed)
    {
        int stride = w * 4;
        var buf = new byte[stride * h];
        uint s = (uint)seed;
        for (int i = 0; i < buf.Length; i += 4)
        {
            s = s * 1664525u + 1013904223u;
            int d = (int)(((s >> 16) & 0xFF) % 60) - 30;
            buf[i + 0] = (byte)Math.Clamp(128 + d, 0, 255);
            s = s * 1664525u + 1013904223u;
            d = (int)(((s >> 16) & 0xFF) % 60) - 30;
            buf[i + 1] = (byte)Math.Clamp(128 + d, 0, 255);
            s = s * 1664525u + 1013904223u;
            d = (int)(((s >> 16) & 0xFF) % 60) - 30;
            buf[i + 2] = (byte)Math.Clamp(128 + d, 0, 255);
            buf[i + 3] = 255;
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        src.Freeze();
        return src;
    }

    private static float ChromaStddev(BitmapSource img)
    {
        var buf = new byte[img.PixelWidth * img.PixelHeight * 4];
        int w = img.PixelWidth, h = img.PixelHeight;
        img.CopyPixels(buf, w * 4, 0);
        double sum = 0, sum2 = 0;
        long n = 0;
        for (int i = 0; i < buf.Length - 1; i += 4)
        {
            double d = (double)buf[i + 2] - buf[i + 1];
            sum += d;
            sum2 += d * d;
            n++;
        }
        double mean = sum / n;
        return (float)Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
    }

    private static BitmapSource MakeNoisySource(int w, int h, int seed)
    {
        int stride = w * 4;
        var buf = new byte[stride * h];
        uint s = (uint)seed;
        for (int i = 0; i < buf.Length; i += 4)
        {
            s = s * 1664525u + 1013904223u;
            byte v = (byte)((s >> 16) & 0xFF);
            buf[i] = v; buf[i + 1] = v; buf[i + 2] = v; buf[i + 3] = 255;
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        src.Freeze();
        return src;
    }

    private static BitmapSource MakeBlocksSource(int w, int h)
    {
        int stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int v = (x / 40 + y / 40) % 2 == 0 ? 90 : 180;
                buf[y * stride + x * 4 + 0] = (byte)v;
                buf[y * stride + x * 4 + 1] = (byte)v;
                buf[y * stride + x * 4 + 2] = (byte)v;
                buf[y * stride + x * 4 + 3] = 255;
            }
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        src.Freeze();
        return src;
    }

    private static float StddevRegion(BitmapSource img, int x0, int y0, int w, int h)
    {
        var buf = new byte[w * h * 4];
        img.CopyPixels(new Int32Rect(x0, y0, w, h), buf, w * 4, 0);
        double mean = 0;
        for (int i = 0; i < buf.Length; i += 4)
        {
            mean += 0.2126 * buf[i + 2] + 0.7152 * buf[i + 1] + 0.0722 * buf[i];
        }
        mean /= w * h;
        double var = 0;
        for (int i = 0; i < buf.Length; i += 4)
        {
            double lum = 0.2126 * buf[i + 2] + 0.7152 * buf[i + 1] + 0.0722 * buf[i];
            var += (lum - mean) * (lum - mean);
        }
        return (float)Math.Sqrt(var / (w * h));
    }

    private static float RowMean(WriteableBitmap bmp, int row)
    {
        int w = bmp.PixelWidth;
        var buf = new byte[w * 4];
        bmp.CopyPixels(new Int32Rect(0, row, w, 1), buf, w * 4, 0);
        long s = 0;
        for (int x = 0; x < w; x++) s += buf[x * 4 + 1];
        return s / (float)w;
    }

    private static void TestMono()
    {
        var src = MakeFlat(255, 255, 255, 255, 128, 128, 128, 0, 0, 0);
        var mono = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false, mono: MonoMode.Documentary);
        Check(mono is not null, "mono apply");
        var m = ReadPixels(mono!);
        Check(m.bright.b == m.bright.g && m.bright.g == m.bright.r && m.dark.b == m.dark.g && m.dark.g == m.dark.r,
              "mono collapses channels");

        var color = AdjustmentsProcessor.Apply(src, 0.0, 1.0, null, out _, autoStretch: false, mono: MonoMode.None);
        var c = ReadPixels(color!);
        Check(c.bright.b == 255, "no-mono keeps color");

        Check(m.bright.g >= 250, "mono white stays white");
        Check(m.dark.g <= 8 && m.dark.g >= 1, "mono black toe lifted slightly (m.bright)");
        Check(m.mid.g is >= 125 and <= 140, "mono mid gently lifted");

        var warm = MakeFlat(255, 240, 60, 10, 240, 60, 10, 240, 60, 10);
        var cool = MakeFlat(255, 10, 60, 240, 10, 60, 240, 10, 60, 240);
        var mw = ReadPixels(AdjustmentsProcessor.Apply(warm, 0.0, 1.0, null, out _, autoStretch: false, mono: MonoMode.Documentary)!);
        var mc = ReadPixels(AdjustmentsProcessor.Apply(cool, 0.0, 1.0, null, out _, autoStretch: false, mono: MonoMode.Documentary)!);
        Check(mw.bright.g - mc.bright.g > 40, $"red-light documentary tone (warm {mw.bright.g} vs cool {mc.bright.g})");

        bool monoIn = false, monoOut = false;
        var ramp = MakeRampSource();
        var rm = AdjustmentsProcessor.Apply(ramp, 0.0, 1.0, null, out _, autoStretch: false, mono: MonoMode.Documentary);
        var rc = AdjustmentsProcessor.Apply(ramp, 0.0, 1.0, null, out _, autoStretch: false, mono: MonoMode.None);
        int a0 = ReadRow(rm!, 0).g, a1 = ReadRow(rm!, 1).g, a2 = ReadRow(rm!, 2).g;
        int b0 = ReadRow(rc!, 0).g, b1 = ReadRow(rc!, 1).g, b2 = ReadRow(rc!, 2).g;
        monoIn = a0 < a1 && a1 < a2;
        monoOut = a1 - b1 >= 4;
        Check(monoIn, "mono S-curve monotonic on ramp");
        Check(monoOut, $"mono S-curve adds mid contrast ({a1} vs {b1})");
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

    private static void MakeExifJpegBySplice(string src, string dst, ushort orientation)
    {
        byte[] jpg = File.ReadAllBytes(src);
        if (jpg[0] != 0xFF || jpg[1] != 0xD8) throw new InvalidDataException("not a jpeg");

        // TIFF (little endian) IFD0 z jednym tagiem: Orientation (0x0112) SHORT, count 1, value
        var tiff = new byte[]
        {
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };
        var head = new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00 };
        var payload = new byte[head.Length + tiff.Length];
        head.CopyTo(payload, 0);
        tiff.CopyTo(payload, head.Length);

        int app1Len = payload.Length + 2;
        var app1 = new byte[2 + 2 + payload.Length];
        app1[0] = 0xFF; app1[1] = 0xE1;
        app1[2] = (byte)(app1Len >> 8); app1[3] = (byte)(app1Len & 0xFF);
        payload.CopyTo(app1, 4);

        var outb = new byte[2 + app1.Length + (jpg.Length - 2)];
        outb[0] = 0xFF; outb[1] = 0xD8;
        app1.CopyTo(outb, 2);
        Array.Copy(jpg, 2, outb, 2 + app1.Length, jpg.Length - 2);
        File.WriteAllBytes(dst, outb);
    }

    private static void ProbeDecode(string path)
    {
        try
        {
            var info = ImageLoader.GetInfo(path);
            Console.WriteLine($"{Path.GetFileName(path)}: info={info.Width}x{info.Height} orient={info.Orientation}");
            try
            {
                var img = ImageLoader.LoadScaled(path, info, 1000, 800);
                Console.WriteLine($"  load ok: {img?.PixelWidth}x{img?.PixelHeight}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  LoadScaled FAIL: [{ex.GetType().Name}] {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Path.GetFileName(path)}: GetInfo FAIL [{ex.GetType().Name}] {ex.Message}");
        }
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

            var proc = AdjustmentsProcessor.Apply(img, 0.5, 1.0, out var histOut);
            if (proc is not null)
            {
                int pw = proc.PixelWidth;
                int ps = pw * 4;
                var pbuf = new byte[(long)ps * proc.PixelHeight];
                proc.CopyPixels(pbuf, ps, 0);
                var hOut = new int[3][];
                for (int c = 0; c < 3; c++) hOut[c] = c == 0 ? histOut!.B : c == 1 ? histOut!.G : histOut!.R;
                Console.Write("  shadows 50% output p0.5%:");
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

    private static void TestExifRotation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opencode");
        Directory.CreateDirectory(dir);
        string baseJpg = Path.Combine(dir, "exif_base.jpg");
        if (!File.Exists(baseJpg))
        {
            var bmp = MakeGraySource(403, 302);
            var enc = new JpegBitmapEncoder { QualityLevel = 90 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(baseJpg);
            enc.Save(fs);
        }
        string jpg = Path.Combine(dir, "exif_rot6.jpg");
        MakeExifJpegBySplice(baseJpg, jpg, 6);

        var info = ImageLoader.GetInfo(jpg);
        Check(info.Orientation == 6 && info.RotatedQuarter, $"exif orientation parsed (orient={info.Orientation})");
        Check(info.Width < info.Height, $"exif display portrait ({info.Width}x{info.Height})");

        try
        {
            var img = ImageLoader.LoadScaled(jpg, info, 300, 400);
            Check(img is not null && img.PixelHeight > img.PixelWidth,
                  $"exif decode+rotate portrait ({img?.PixelWidth}x{img?.PixelHeight})");
            Check(Math.Abs(img!.DpiX - 96) < 0.01, $"exif result dpi 96 ({img.DpiX:0.#})");
            img.CopyPixels(new Int32Rect(0, 0, 1, 1), new byte[4], 4, 0);
            Check(true, "exif result fully materialized (CopyPixels)");
        }
        catch (Exception ex)
        {
            Check(false, $"exif load threw: [{ex.GetType().Name}] {ex.Message}");
        }
    }

    private static BitmapSource MakeGraySource(int w, int h)
    {
        int stride = w * 4;
        var buf = new byte[stride * h];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i] = 128;
            buf[i + 1] = 128;
            buf[i + 2] = 128;
            buf[i + 3] = 255;
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        src.Freeze();
        return src;
    }

    private static void TestCrossImageSwitch()
    {
        var vp = new Size(800, 533);
        var land = MakeGraySource(380, 285);
        var landNative = new Size(4000, 3000);
        var port = MakeGraySource(285, 380);
        var portNative = new Size(3000, 4000);
        var portHi = MakeGraySource(570, 760);

        var v = MakeViewer(vp, land, landNative);
        v.SetZoom(2.0);
        double zoomA = v.Zoom;
        double wA = v.TestDisplayedWidth, hA = v.TestDisplayedHeight;

        v.SetSource(port, portNative);
        double wB = v.TestDisplayedWidth, hB = v.TestDisplayedHeight;
        Check(Math.Abs(wB / hB - 0.75) < 0.01,
              $"portrait aspect correct after zoomed switch (w/h={wB / hB:0.####})");
        Check(Math.Abs(v.Zoom - zoomA) < 0.005, $"zoom preserved across switch ({zoomA:0.####} -> {v.Zoom:0.####})");

        v.SetSource(portHi, portNative);
        double wC = v.TestDisplayedWidth, hC = v.TestDisplayedHeight;
        Check(Math.Abs(wC - wB) < 0.5 && Math.Abs(hC - hB) < 0.5,
              $"higher-res redecode keeps displayed size ({wB:0.#}x{hB:0.#} -> {wC:0.#}x{hC:0.#})");
        Check(Math.Abs(wC / hC - 0.75) < 0.01, $"redecode aspect kept (w/h={wC / hC:0.####})");

        v.SetSource(land, landNative);
        double wD = v.TestDisplayedWidth, hD = v.TestDisplayedHeight;
        Check(Math.Abs(wD / hD - wA / hA) < 0.01 && Math.Abs(wD - wA) < 0.5 && Math.Abs(hD - hA) < 0.5,
              $"switch back restores landscape aspect (was {wA:0.#}x{hA:0.#}, now {wD:0.#}x{hD:0.#})");
    }

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


