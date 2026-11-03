using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ImgViewer;
using ImgViewer.Imaging;
using ImgViewer.Plugins;

internal static partial class Program
{
    private static string? FindAutoEnhanceDll()
    {
        // Newest of Debug/Release (the harness build rebuilds Debug through the app's project reference).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var found = new[] { "Release", "Debug" }
                .Select(cfg => Path.Combine(dir.FullName, "eyePlugins", "auto", "bin", cfg, "netstandard2.0", "ImgViewer.Plugins.AutoEnhance.dll"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }

    private static void TestAutoEnhance()
    {
        var path = FindAutoEnhanceDll();
        if (path is null) { Check(false, "auto enhance dll present (build eyePlugins/auto first)"); return; }
        var entry = PluginManager.LoadFromFile(path);
        Check(entry.Error is null && entry.Plugins.Count == 1 && entry.Plugins[0].Error is null, $"auto enhance loads ({entry.Error})");
        var reg = new PluginRegistry(entry.Plugins);
        var f = reg.FindFilter("photographereye.autoenhance:auto");
        Check(f is not null && f.D.Placement == FilterPlacement.Toolbar && f.D.Stage == FilterStage.Display, "auto filter: toolbar, display stage");
        Check(reg.Commands.Any(c => c.FullId == "photographereye.autoenhance:toggle" && c.D.Shortcut == "Ctrl+Shift+A"), "auto command Ctrl+Shift+A");
        if (f is null) return;

        Raw16Image? Run(Raw16Image src, int w, int h, double balance, double contrast, double denoise, bool straighten, bool preview = false, int fullW = 0, int fullH = 0, double wb = 0, double color = 0, string? sourcePath = null)
        {
            var st = new PluginEditState();
            st.Active.Add(f.FullId);
            void Set(string id, double v) => st.SetValue(f, f.D.Params.First(p => p.Id == id), v);
            Set("balance", balance); Set("contrast", contrast); Set("denoise", denoise); Set("straighten", straighten ? 1 : 0); Set("wb", wb); Set("color", color);
            int fw = fullW > 0 ? fullW : w, fh = fullH > 0 ? fullH : h;
            var job = new PluginRenderJob { Registry = reg, State = st, Meta = new ImageMeta { Width = fw, Height = fh, SourcePath = sourcePath ?? "" }, IsPreview = preview, Cancel = CancellationToken.None };
            return PluginPipeline.ApplyRaw16(src, FilterStage.Display, job, fw, fh);
        }

        var rnd = new Random(7);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        static ushort U(double v) => (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
        static double L(Raw16Image im, int i) => (0.2126 * im.R[i] + 0.7152 * im.G[i] + 0.0722 * im.B[i]) / 65535.0;

        // ---------- tilted horizon is levelled ----------
        Raw16Image Horizon(int w, int h, double deg, double noise)
        {
            var im = new Raw16Image(w, h);
            double t = Math.Tan(deg * Math.PI / 180);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double yb = h * 0.45 + t * (x - w / 2.0);
                    double a = Math.Clamp(y + 0.5 - yb, -0.5, 0.5) + 0.5; // 0 sky .. 1 ground (antialiased)
                    double sky = 0.78 - 0.1 * y / h, ground = 0.28 + 0.05 * Math.Sin(x * 0.05);
                    double v = sky + (ground - sky) * a + noise * Gauss();
                    int i = y * w + x;
                    im.R[i] = U(v * 0.95); im.G[i] = U(v); im.B[i] = U(v * 1.05);
                }
            return im;
        }
        // Horizon row (sub-pixel) at column x: first crossing of the mid level.
        double Edge(Raw16Image im, int w, int h, int x)
        {
            double mid = 0.53;
            for (int y = h / 6; y < h * 5 / 6; y++)
            {
                double a = 0, b = 0;
                for (int k = -2; k <= 2; k++) { a += L(im, y * w + x + k); b += L(im, (y + 1) * w + x + k); }
                a /= 5; b /= 5;
                if (a >= mid && b < mid) return y + (a - mid) / (a - b);
            }
            return double.NaN;
        }
        double Tilt(Raw16Image im, int w, int h)
        {
            int x0 = w / 5, x1 = w * 4 / 5;
            return Math.Atan2(Edge(im, w, h, x1) - Edge(im, w, h, x0), x1 - x0) * 180 / Math.PI;
        }

        foreach (var (deg, w, h) in new[] { (4.0, 900, 600), (-3.0, 900, 600), (2.5, 1800, 1200) })
        {
            var src = Horizon(w, h, deg, 0.01);
            double before = Tilt(src, w, h);
            var o = Run(src, w, h, 0, 0, 0, true);
            double after = o is null ? double.NaN : Tilt(o, w, h);
            Check(Math.Abs(before - deg) < 0.2 && Math.Abs(after) < 0.3, $"auto straighten {deg}° on {w}x{h}: {before:0.00}° -> {after:0.00}°");
        }
        // Preview of a 1800x1200 photo (buffer 900x600, Scale 0.5) levels it the same way.
        {
            var src = Horizon(900, 600, 2.5, 0.01);
            var o = Run(src, 900, 600, 0, 0, 0, true, preview: true, fullW: 1800, fullH: 1200);
            double after = o is null ? double.NaN : Tilt(o, 900, 600);
            Check(Math.Abs(after) < 0.3, $"auto straighten in preview scale: residual {after:0.00}°");
        }
        // The angle is detected once per photo: tone changes of the same photo must not change the rotation.
        {
            var src = Horizon(900, 600, 3.0, 0.01);
            var first = Run(src, 900, 600, 0, 0, 0, true, sourcePath: path);
            var level = Horizon(900, 600, 0, 0.01);
            var again = Run(level, 900, 600, 0, 0, 0, true, sourcePath: path);
            double t1 = first is null ? double.NaN : Tilt(first, 900, 600), t2 = again is null ? double.NaN : Tilt(again, 900, 600);
            Check(Math.Abs(t1) < 0.3 && Math.Abs(t2 + 3.0) < 0.3, $"auto straighten: angle cached per photo (first {t1:0.00}°, re-render with different pixels {t2:0.00}°)");
        }
        // Level horizon, random texture and a large (intentional) tilt stay unrotated.
        {
            var src = Horizon(900, 600, 0, 0.01);
            var o = Run(src, 900, 600, 0, 0, 0, true);
            Check(o is not null && MaxDiff(src, o) < 0.002, "auto straighten: level horizon untouched");
            var tex = new Raw16Image(600, 400);
            for (int i = 0; i < tex.R.Length; i++) { var v = U(0.5 + 0.2 * Gauss()); tex.R[i] = tex.G[i] = tex.B[i] = v; }
            var ot = Run(tex, 600, 400, 0, 0, 0, true);
            Check(ot is not null && MaxDiff(tex, ot) < 0.002, "auto straighten: random texture not rotated");
            // One short tilted edge (an arm, a shadow) is not evidence of a tilted camera.
            var seg = new Raw16Image(900, 600);
            double ts = Math.Tan(3 * Math.PI / 180);
            for (int y = 0; y < 600; y++)
                for (int x = 0; x < 900; x++)
                {
                    int i = y * 900 + x;
                    bool on = x > 300 && x < 560 && y > 300 + ts * (x - 300) && y < 340 + ts * (x - 300);
                    var v = U((on ? 0.8 : 0.3) + 0.01 * Gauss());
                    seg.R[i] = seg.G[i] = seg.B[i] = v;
                }
            var os = Run(seg, 900, 600, 0, 0, 0, true);
            Check(os is not null && MaxDiff(seg, os) < 0.002, "auto straighten: a single short tilted edge does not rotate");
            var big = Horizon(900, 600, 20, 0.01);
            var ob = Run(big, 900, 600, 0, 0, 0, true);
            Check(ob is not null && MaxDiff(big, ob) < 0.002, "auto straighten: 20° tilt treated as intentional");
        }

        // ---------- noise reduction ----------
        {
            int w = 400, h = 300;
            var src = new Raw16Image(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    double v = (x < w / 2 ? 0.35 : 0.65) + 0.03 * Gauss();
                    src.R[i] = src.G[i] = src.B[i] = U(v);
                }
            var o = Run(src, w, h, 0, 0, 80, false);
            double sIn = FlatStd(src, w, 20, 20, 150, 260), sOut = o is null ? 1 : FlatStd(o, w, 20, 20, 150, 260);
            double step = 0;
            if (o is not null)
            {
                for (int y = 20; y < h - 20; y++) step += L(o, y * w + w / 2 + 2) - L(o, y * w + w / 2 - 3);
                step /= h - 40;
            }
            Check(sOut < sIn * 0.4, $"auto denoise: flat noise {sIn:0.0000} -> {sOut:0.0000}");
            Check(step > 0.27, $"auto denoise keeps the edge (step {step:0.000} of 0.30)");

            // Color speckle only.
            var cs = new Raw16Image(w, h);
            for (int i = 0; i < w * h; i++)
            {
                double cb = 0.04 * Gauss(), cr = 0.04 * Gauss();
                cs.R[i] = U(0.5 + cr); cs.B[i] = U(0.5 + cb); cs.G[i] = U(0.5 - (0.299 * cr + 0.114 * cb) / 0.587);
            }
            var oc = Run(cs, w, h, 0, 0, 80, false);
            double cIn = ChromaStd(cs), cOut = oc is null ? 1 : ChromaStd(oc);
            Check(cOut < cIn * 0.35, $"auto denoise removes color speckle: chroma {cIn:0.0000} -> {cOut:0.0000}");

            // Clean smooth image stays (nearly) the same.
            var clean = new Raw16Image(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    clean.R[i] = U(0.2 + 0.6 * x / w); clean.G[i] = U(0.3 + 0.4 * y / h); clean.B[i] = U(0.5 + 0.2 * Math.Sin(x * 0.02));
                }
            var ocl = Run(clean, w, h, 0, 0, 100, false);
            Check(ocl is not null && MeanDiff(clean, ocl) < 0.002, $"auto denoise leaves a clean image alone (mean diff {(ocl is null ? 1 : MeanDiff(clean, ocl)):0.0000})");
        }

        // ---------- histogram balance + contrast ----------
        {
            int w = 600, h = 400;
            var flat = new Raw16Image(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    double v = 0.38 + 0.18 * (0.5 + 0.5 * Math.Sin(x * 0.03) * Math.Cos(y * 0.025)) + 0.004 * Gauss();
                    flat.R[i] = U(v * 1.08); flat.G[i] = U(v); flat.B[i] = U(v * 0.9);
                }
            var o = Run(flat, w, h, 60, 50, 0, false);
            var (p1, p99) = Pct(flat);
            var (q1, q99) = o is null ? (0.0, 0.0) : Pct(o);
            Check(q99 - q1 > 1.8 * (p99 - p1), $"auto balance expands a flat histogram: {p99 - p1:0.000} -> {q99 - q1:0.000}");
            bool warm = o is not null && Enumerable.Range(0, 2000).All(k => { int i = k * 97 % (w * h); return o.R[i] >= o.G[i] && o.G[i] >= o.B[i]; });
            Check(warm, "auto balance keeps the color cast order (luma-ratio tone mapping)");

            // A dark night scene keeps its key (no lifting to mid gray).
            var dark = new Raw16Image(w, h);
            for (int i = 0; i < w * h; i++)
            {
                int x = i % w, y = i / w;
                double v = 0.06 + 0.12 * (0.5 + 0.5 * Math.Sin(x * 0.02 + y * 0.01));
                if ((x - 300) * (x - 300) + (y - 150) * (y - 150) < 400) v = 0.95; // a lamp
                dark.R[i] = dark.G[i] = dark.B[i] = U(v);
            }
            var od = Run(dark, w, h, 60, 50, 0, false);
            double mIn = MeanL(dark), mOut = od is null ? 1 : MeanL(od);
            Check(mOut < 0.4 && mOut > mIn * 0.8, $"auto balance keeps a dark scene dark: mean {mIn:0.000} -> {mOut:0.000}");

            // Already well exposed full-range image: small change.
            var good = new Raw16Image(w, h);
            for (int i = 0; i < w * h; i++)
            {
                int x = i % w, y = i / w;
                double v = Math.Clamp(0.5 + 0.48 * Math.Sin(x * 0.011) * Math.Cos(y * 0.017), 0, 1);
                good.R[i] = good.G[i] = good.B[i] = U(v);
            }
            var og = Run(good, w, h, 60, 50, 0, false);
            Check(og is not null && MeanDiff(good, og) < 0.06, $"auto tone: well spread image changes little (mean diff {(og is null ? 1 : MeanDiff(good, og)):0.000})");
        }

        // ---------- white balance + color ----------
        {
            static double Enc(double v) { v = Math.Clamp(v, 0, 1); return v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055; }
            static double Dec(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            int w = 600, h = 400;
            // Scene in linear light: gray card strip + colored patches; lit by (ir, ig, ib).
            double[][] patches =
            {
                new[] { 0.40, 0.18, 0.10 }, new[] { 0.08, 0.25, 0.06 }, new[] { 0.06, 0.10, 0.35 }, new[] { 0.45, 0.40, 0.08 },
                new[] { 0.30, 0.12, 0.25 }, new[] { 0.10, 0.30, 0.30 }, new[] { 0.50, 0.32, 0.22 }, new[] { 0.20, 0.20, 0.20 },
            };
            Raw16Image Scene(double ir, double ig, double ib, bool grays = true, double[][]? pal = null, bool white = false)
            {
                pal ??= patches;
                var im = new Raw16Image(w, h);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        double r, g, b;
                        if (grays && y < 100) { double v = 0.05 + 0.6 * x / w; r = g = b = v; }
                        else { var c = pal[(x / 150 + (y / 100) * 4) % pal.Length]; double sh = 0.85 + 0.15 * Math.Sin(x * 0.05 + y * 0.03); r = c[0] * sh; g = c[1] * sh; b = c[2] * sh; }
                        if (white && x > 540 && y > 340) { im.R[i] = im.G[i] = im.B[i] = 65535; continue; }
                        im.R[i] = U(Enc(r * ir) + 0.003 * Gauss()); im.G[i] = U(Enc(g * ig) + 0.003 * Gauss()); im.B[i] = U(Enc(b * ib) + 0.003 * Gauss());
                    }
                return im;
            }
            // Log cast of the gray strip.
            (double, double) GrayCast(Raw16Image im)
            {
                double r = 0, g = 0, b = 0;
                for (int y = 20; y < 80; y++)
                    for (int x = 100; x < 500; x++) { int i = y * w + x; r += Dec(im.R[i] / 65535.0); g += Dec(im.G[i] / 65535.0); b += Dec(im.B[i] / 65535.0); }
                return (Math.Log(r / g), Math.Log(b / g));
            }
            double Mag((double a, double b) c) => Math.Sqrt(c.a * c.a + c.b * c.b);

            foreach (var (ir, ib, maxResidual, name) in new[] { (1.10, 0.90, 0.15, "mild warm"), (0.88, 1.15, 0.15, "mild cool"), (1.25, 0.75, 0.35, "strong tungsten") })
            {
                var cast = Scene(ir, 1, ib);
                var o = Run(cast, w, h, 0, 0, 0, false, wb: 100);
                double before = Mag(GrayCast(cast)), after = o is null ? 9 : Mag(GrayCast(o));
                Check(after < before * maxResidual, $"auto wb removes a {name} cast: gray {before:0.000} -> {after:0.000}");
            }

            var neutral = Scene(1, 1, 1);
            var on = Run(neutral, w, h, 0, 0, 0, false, wb: 100);
            Check(on is not null && Mag(GrayCast(on)) < 0.03, $"auto wb: neutral scene stays neutral (gray {(on is null ? 9 : Mag(GrayCast(on))):0.000})");

            // Foliage only, no neutrals: must not turn magenta.
            double[][] leaves = { new[] { 0.06, 0.22, 0.04 }, new[] { 0.10, 0.30, 0.05 }, new[] { 0.04, 0.15, 0.05 }, new[] { 0.15, 0.32, 0.08 } };
            var fol = Scene(1, 1, 1, grays: false, pal: leaves);
            var of = Run(fol, w, h, 0, 0, 0, false, wb: 100);
            double shift = 0;
            if (of is not null)
            {
                double gIn = 0, gOut = 0;
                for (int i = 0; i < w * h; i += 7) { gIn += Dec(fol.G[i] / 65535.0) / Math.Max(1e-4, Dec(fol.R[i] / 65535.0) + Dec(fol.B[i] / 65535.0)); gOut += Dec(of.G[i] / 65535.0) / Math.Max(1e-4, Dec(of.R[i] / 65535.0) + Dec(of.B[i] / 65535.0)); }
                shift = Math.Abs(Math.Log(gOut / gIn));
            }
            Check(of is not null && shift < 0.15, $"auto wb: foliage-only scene keeps its green (log shift {shift:0.000})");

            var cw = Scene(1.2, 1, 0.8, white: true);
            var ow = Run(cw, w, h, 0, 0, 0, false, wb: 100);
            int wi = 380 * w + 580;
            Check(ow is not null && ow.R[wi] > 64500 && ow.G[wi] > 64500 && ow.B[wi] > 64500, $"auto wb: clipped white stays white ({ow?.R[wi]},{ow?.G[wi]},{ow?.B[wi]})");

            // Vibrance: a dull photo gets more saturated, skin less than other hues; luminance kept.
            static double Sat(Raw16Image im, int x0, int y0, int x1, int y1, int ww)
            {
                double s = 0; int c = 0;
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = y * ww + x; double mx = Math.Max(im.R[i], Math.Max(im.G[i], im.B[i])), mn = Math.Min(im.R[i], Math.Min(im.G[i], im.B[i])); s += (mx - mn) / Math.Max(1, mx); c++; }
                return s / c;
            }
            double[][] dull = { new[] { 0.30, 0.25, 0.22 }, new[] { 0.20, 0.22, 0.28 }, new[] { 0.22, 0.26, 0.20 }, new[] { 0.25, 0.24, 0.21 } };
            var dl = Scene(1, 1, 1, grays: false, pal: dull);
            var od2 = Run(dl, w, h, 0, 0, 0, false, color: 100);
            double skinIn = Sat(dl, 10, 10, 140, 90, w), blueIn = Sat(dl, 160, 10, 290, 90, w);
            double skinOut = od2 is null ? 0 : Sat(od2, 10, 10, 140, 90, w), blueOut = od2 is null ? 0 : Sat(od2, 160, 10, 290, 90, w);
            Check(blueOut > blueIn * 1.25, $"auto color boosts a dull photo: blue sat {blueIn:0.000} -> {blueOut:0.000}");
            Check(skinOut / skinIn < blueOut / blueIn && skinOut >= skinIn * 0.98, $"auto color protects skin: x{skinOut / skinIn:0.00} vs blue x{blueOut / blueIn:0.00}");
            Check(od2 is not null && Math.Abs(MeanL(od2) - MeanL(dl)) < 0.01, "auto color keeps luminance");

            double[][] vivid = { new[] { 0.60, 0.02, 0.02 }, new[] { 0.02, 0.45, 0.02 }, new[] { 0.02, 0.05, 0.70 }, new[] { 0.60, 0.50, 0.01 } };
            var vv = Scene(1, 1, 1, grays: false, pal: vivid);
            var ov = Run(vv, w, h, 0, 0, 0, false, color: 100);
            double vIn = Sat(vv, 0, 0, w, h, w), vOut = ov is null ? 9 : Sat(ov, 0, 0, w, h, w);
            Check(vOut <= vIn && vOut > vIn * 0.85, $"auto color tones down an oversaturated photo slightly: {vIn:0.000} -> {vOut:0.000}");
        }

        // ---------- all at once at 24 MP ----------
        {
            int w = 6000, h = 4000;
            var big = Horizon(w, h, 1.5, 0.02);
            var sw = Stopwatch.StartNew();
            var o = Run(big, w, h, 60, 50, 60, true, wb: 80, color: 40);
            sw.Stop();
            double after = o is null ? double.NaN : Tilt(o, w, h);
            Check(o is not null && Math.Abs(after) < 0.3 && sw.Elapsed.TotalSeconds < 20,
                $"auto enhance 24 MP full render: {sw.Elapsed.TotalSeconds:0.0} s, residual tilt {after:0.00}°");
        }
    }

    /// <summary>--auto-probe dir: tilt the straightener detects on real photos (RAW half size + JPEG).</summary>
    private static void AutoEnhanceProbe(string dir, string? args2 = null)
    {
        if (args2 is not null) Directory.CreateDirectory(args2);
        var path = FindAutoEnhanceDll();
        if (path is null) { Console.WriteLine("auto enhance dll not found"); return; }
        var t = System.Reflection.Assembly.LoadFrom(path).GetType("ImgViewer.Plugins.AutoEnhance.AutoEnhance")!;
        var detect = t.GetMethod("DetectTilt")!;
        var down = t.GetMethod("Downscale")!;
        foreach (var file in Directory.GetFiles(dir).OrderBy(p => p))
        {
            float[] l; int w, h;
            if (RawDecoder.IsRaw(file))
            {
                var im = LinearRender.ApplyGain(RawDecoder.Decode16(file, halfSize: true), RawDecoder.GetBaseEv(file), 0.0);
                w = im.W; h = im.H; l = new float[w * h];
                for (int i = 0; i < l.Length; i++)
                {
                    double v = Math.Clamp((0.2126 * im.R[i] + 0.7152 * im.G[i] + 0.0722 * im.B[i]) / 65535.0, 0, 1);
                    l[i] = (float)(v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);
                }
            }
            else if (file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                var bmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(file)), System.Windows.Media.PixelFormats.Bgra32, null, 0);
                w = bmp.PixelWidth; h = bmp.PixelHeight;
                var px = new byte[w * h * 4];
                bmp.CopyPixels(px, w * 4, 0);
                l = new float[w * h];
                for (int i = 0; i < l.Length; i++) l[i] = (0.0722f * px[i * 4] + 0.7152f * px[i * 4 + 1] + 0.2126f * px[i * 4 + 2]) / 255f;
            }
            else continue;
            var dargs = new object[] { l, w, h, 1024, 0, 0 };
            var small = (float[])down.Invoke(null, dargs)!;
            var targs = new object[] { small, (int)dargs[4], (int)dargs[5], 8.0, 0.0 };
            double a = (double)detect.Invoke(null, targs)!;
            var info = (string)t.GetField("LastInfo")!.GetValue(null)!;
            // Same photo rotated by +3° (zoomed to fill): the detector should report about -3°.
            int sw = (int)dargs[4], sh = (int)dargs[5];
            var rot = new float[small.Length];
            double c = Math.Cos(3 * Math.PI / 180), s = Math.Sin(3 * Math.PI / 180);
            double z = Math.Max(c + s * sh / sw, c + s * sw / sh);
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    double dx = x - sw / 2.0, dy = y - sh / 2.0;
                    double fx = Math.Clamp((dx * c - dy * s) / z + sw / 2.0, 0, sw - 1.001);
                    double fy = Math.Clamp((dx * s + dy * c) / z + sh / 2.0, 0, sh - 1.001);
                    int x0 = (int)fx, y0 = (int)fy;
                    double tx = fx - x0, ty = fy - y0;
                    double top = small[y0 * sw + x0] * (1 - tx) + small[y0 * sw + x0 + 1] * tx;
                    double bot = small[(y0 + 1) * sw + x0] * (1 - tx) + small[(y0 + 1) * sw + x0 + 1] * tx;
                    rot[y * sw + x] = (float)(top * (1 - ty) + bot * ty);
                }
            var rargs = new object[] { rot, sw, sh, 8.0, 0.0 };
            double ar = (double)detect.Invoke(null, rargs)!;
            var rinfo = (string)t.GetField("LastInfo")!.GetValue(null)!;
            Console.WriteLine($"{Path.GetFileName(file),-36} {w}x{h}  tilt {a,6:0.00}° (conf {(double)targs[4]:0.0})   rotated +3°: {ar,6:0.00}° (conf {(double)rargs[4]:0.0})");
            if (args2 is not null)
            {
                // Side by side: as shot | straightened by the detected angle, with a level grid.
                var pair = new byte[sw * 2 * sh];
                double ca = Math.Cos(a * Math.PI / 180), sa = Math.Sin(a * Math.PI / 180), as_ = Math.Abs(sa);
                double za = Math.Max(ca + as_ * sh / sw, ca + as_ * sw / sh);
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                    {
                        double dx = x - sw / 2.0, dy = y - sh / 2.0;
                        int fx = (int)Math.Clamp((dx * ca - dy * sa) / za + sw / 2.0, 0, sw - 1);
                        int fy = (int)Math.Clamp((dx * sa + dy * ca) / za + sh / 2.0, 0, sh - 1);
                        bool grid = y % 48 == 0 || x % 48 == 0;
                        pair[y * sw * 2 + x] = grid ? (byte)255 : (byte)(Math.Clamp(small[y * sw + x], 0, 1) * 255);
                        pair[y * sw * 2 + sw + x] = grid ? (byte)255 : (byte)(Math.Clamp(small[fy * sw + fx], 0, 1) * 255);
                    }
                var bs = System.Windows.Media.Imaging.BitmapSource.Create(sw * 2, sh, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, pair, sw * 2);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bs));
                using var fs = File.Create(Path.Combine(args2, Path.GetFileNameWithoutExtension(file) + "_tilt.png"));
                enc.Save(fs);
            }
            Console.WriteLine($"      {info}");
            Console.WriteLine($"   r  {rinfo}");
        }
    }

    /// <summary>--auto-color dir outDir: full auto enhance (default params) on real photos, saves before|after JPEGs and logs the decisions.</summary>
    private static void AutoColorProbe(string dir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var path = FindAutoEnhanceDll();
        if (path is null) { Console.WriteLine("auto enhance dll not found"); return; }
        var entry = PluginManager.LoadFromFile(path);
        var reg = new PluginRegistry(entry.Plugins);
        var f = reg.FindFilter("photographereye.autoenhance:auto")!;
        foreach (var file in Directory.GetFiles(dir).OrderBy(p => p))
        {
            Raw16Image src;
            if (RawDecoder.IsRaw(file))
            {
                var im = LinearRender.ApplyGain(RawDecoder.Decode16(file, halfSize: true), RawDecoder.GetBaseEv(file), 0.0);
                src = new Raw16Image(im.W, im.H);
                for (int i = 0; i < im.R.Length; i++) { src.R[i] = EncU(im.R[i]); src.G[i] = EncU(im.G[i]); src.B[i] = EncU(im.B[i]); }
            }
            else if (file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                var bmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(file)), System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int bw = bmp.PixelWidth, bh = bmp.PixelHeight;
                var px = new byte[bw * bh * 4];
                bmp.CopyPixels(px, bw * 4, 0);
                src = new Raw16Image(bw, bh);
                for (int i = 0; i < bw * bh; i++) { src.R[i] = (ushort)(px[i * 4 + 2] * 257); src.G[i] = (ushort)(px[i * 4 + 1] * 257); src.B[i] = (ushort)(px[i * 4] * 257); }
            }
            else continue;
            var st = new PluginEditState();
            st.Active.Add(f.FullId);
            foreach (var p in f.D.Params) st.SetValue(f, p, p.Default);
            var job = new PluginRenderJob { Registry = reg, State = st, Meta = new ImageMeta { Width = src.W, Height = src.H }, IsPreview = false, Cancel = CancellationToken.None };
            var sw = Stopwatch.StartNew();
            var o = PluginPipeline.ApplyRaw16(src, FilterStage.Display, job, src.W, src.H) ?? src;
            Console.WriteLine($"{Path.GetFileName(file),-30} {src.W}x{src.H} {sw.Elapsed.TotalSeconds:0.0}s");
            // Side by side, long side of each half <= 1200.
            int step = Math.Max(1, (int)Math.Ceiling(Math.Max(src.W, src.H) / 1200.0));
            int sw2 = src.W / step, sh2 = src.H / step;
            var pair = new byte[sw2 * 2 * sh2 * 3];
            for (int y = 0; y < sh2; y++)
                for (int x = 0; x < sw2; x++)
                {
                    int si = y * step * src.W + x * step;
                    int a = (y * sw2 * 2 + x) * 3, b = a + sw2 * 3;
                    pair[a] = (byte)(src.R[si] >> 8); pair[a + 1] = (byte)(src.G[si] >> 8); pair[a + 2] = (byte)(src.B[si] >> 8);
                    pair[b] = (byte)(o.R[si] >> 8); pair[b + 1] = (byte)(o.G[si] >> 8); pair[b + 2] = (byte)(o.B[si] >> 8);
                }
            var bs = System.Windows.Media.Imaging.BitmapSource.Create(sw2 * 2, sh2, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, pair, sw2 * 2 * 3);
            var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bs));
            using var fs = File.Create(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "_auto.jpg"));
            enc.Save(fs);
        }

        static ushort EncU(ushort v)
        {
            double x = v / 65535.0;
            x = x <= 0.0031308 ? x * 12.92 : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
            return (ushort)Math.Round(Math.Clamp(x, 0, 1) * 65535);
        }
    }

    private static double MaxDiff(Raw16Image a, Raw16Image b)
    {
        int m = 0;
        for (int i = 0; i < a.R.Length; i++)
            m = Math.Max(m, Math.Max(Math.Abs(a.R[i] - b.R[i]), Math.Max(Math.Abs(a.G[i] - b.G[i]), Math.Abs(a.B[i] - b.B[i]))));
        return m / 65535.0;
    }

    private static double MeanDiff(Raw16Image a, Raw16Image b)
    {
        double s = 0;
        for (int i = 0; i < a.R.Length; i++) s += Math.Abs(a.R[i] - b.R[i]) + Math.Abs(a.G[i] - b.G[i]) + Math.Abs(a.B[i] - b.B[i]);
        return s / (3.0 * a.R.Length * 65535);
    }

    private static double MeanL(Raw16Image a)
    {
        double s = 0;
        for (int i = 0; i < a.R.Length; i++) s += 0.2126 * a.R[i] + 0.7152 * a.G[i] + 0.0722 * a.B[i];
        return s / (a.R.Length * 65535.0);
    }

    private static double FlatStd(Raw16Image a, int w, int x0, int y0, int x1, int y1)
    {
        double s = 0, s2 = 0; int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) { double v = a.G[y * w + x] / 65535.0; s += v; s2 += v * v; n++; }
        double m = s / n;
        return Math.Sqrt(Math.Max(0, s2 / n - m * m));
    }

    private static double ChromaStd(Raw16Image a)
    {
        double s = 0; int n = a.R.Length;
        for (int i = 0; i < n; i++)
        {
            double r = a.R[i] / 65535.0, g = a.G[i] / 65535.0, b = a.B[i] / 65535.0;
            double y = 0.299 * r + 0.587 * g + 0.114 * b;
            s += (r - y) * (r - y) + (b - y) * (b - y);
        }
        return Math.Sqrt(s / (2 * n));
    }

    private static (double, double) Pct(Raw16Image a)
    {
        var l = new double[a.R.Length];
        for (int i = 0; i < l.Length; i++) l[i] = (0.2126 * a.R[i] + 0.7152 * a.G[i] + 0.0722 * a.B[i]) / 65535.0;
        Array.Sort(l);
        return (l[(int)(l.Length * 0.01)], l[(int)(l.Length * 0.99)]);
    }
}
