using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ImgViewer.Plugins;
using ImgViewer;
using ImgViewer.Imaging;
using ImgViewer.Services;

internal static partial class Program
{
    private static void TestTransNoise()
    {
        var path = FindPluginDll("transnoise", "ImgViewer.Plugins.TransNoise.dll");
        if (path is null) { Check(false, "trans-noise dll present (build eyePlugins/transnoise first)"); return; }
        var entry = PluginManager.LoadFromFile(path);
        Check(entry.Error is null && entry.Plugins.Count == 1 && entry.Plugins[0].Error is null, $"trans-noise loads ({entry.Error})");
        var reg = new PluginRegistry(entry.Plugins);
        var f = reg.FindFilter("photographereye.transnoise:transnoise");
        Check(f is not null && f.D.Stage == FilterStage.Display && f.D.SupportsMask, "trans-noise: display-stage filter with mask support");
        if (f is null) return;
        FilterParams Params(double amount, double grain = 100, double size = 40, double color = 60) =>
            new FilterParams(f.D.Params, new Dictionary<string, double> { ["amount"] = amount, ["grain"] = grain, ["size"] = size, ["color"] = color },
                             new Dictionary<string, string>());

        const int W = 320, H = 240, N = W * H;
        static float[] Blur(float[] p, double s)
        {
            int r = (int)Math.Ceiling(3 * s);
            var k = Enumerable.Range(-r, 2 * r + 1).Select(i => Math.Exp(-i * i / (2 * s * s))).ToArray();
            double ks = k.Sum();
            var t = new float[N];
            var o = new float[N];
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double a = 0;
                for (int i = -r; i <= r; i++) a += k[i + r] * p[y * W + Math.Clamp(x + i, 0, W - 1)];
                t[y * W + x] = (float)(a / ks);
            }
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double a = 0;
                for (int i = -r; i <= r; i++) a += k[i + r] * t[Math.Clamp(y + i, 0, H - 1) * W + x];
                o[y * W + x] = (float)(a / ks);
            }
            return o;
        }
        static float Clean(int x, int y) => (float)(0.4 + 0.1 * x / W + (x >= 160 ? 0.2 : 0.0));
        float[] Field(int seed, double std)
        {
            var rnd = new Random(seed);
            var p = new float[N];
            for (int i = 0; i < N; i++) p[i] = (float)(rnd.NextDouble() - 0.5);
            var b = Blur(p, 3.0);
            double sd = Math.Sqrt(b.Average(v => (double)v * v));
            return b.Select(v => (float)(v / sd * std)).ToArray();
        }
        var lumaN = Field(1, 0.03);
        var colorN = Field(2, 0.03);
        PluginImage Make(bool noisy)
        {
            var im = new PluginImage(W, H);
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float c = Clean(x, y), l = noisy ? lumaN[i] : 0f, cn = noisy ? colorN[i] : 0f;
                im.R[i] = c + l + cn;
                im.G[i] = c + l - 0.3f * cn;
                im.B[i] = c + l;
            }
            return im;
        }
        var ctx = new RenderContext { Stage = FilterStage.Display, Space = PluginColorSpace.Srgb, FullWidth = W, FullHeight = H, Cancel = CancellationToken.None };
        // Flat region left of the edge: residual vs the clean image split into coarse (blur 3 px) and fine parts.
        (double coarse, double fine, double mean) Stats(float[] plane)
        {
            var e = new float[N];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) e[y * W + x] = plane[y * W + x] - Clean(x, y);
            var c = Blur(e, 3.0);
            var m = Blur(e, 1.5);
            double sc = 0, sf = 0, sm = 0; int cnt = 0;
            for (int y = 20; y < H - 20; y++)
            for (int x = 20; x < 140; x++)
            {
                int i = y * W + x;
                sc += c[i] * (double)c[i]; sf += (e[i] - m[i]) * (double)(e[i] - m[i]); sm += e[i]; cnt++;
            }
            return (Math.Sqrt(sc / cnt), Math.Sqrt(sf / cnt), sm / cnt);
        }

        Check(f.D.IsIdentity(Params(0)) && !f.D.IsIdentity(Params(50)), "trans-noise: strength 0 is identity");

        var src = Make(true);
        var outIm = Make(true);
        f.D.Render(outIm, Params(100), ctx);
        var (c0, f0, m0) = Stats(src.G);
        var (c1, f1, m1) = Stats(outIm.G);
        Check(c1 < c0 * 0.5, $"trans-noise: coarse blotches taken out ({c0:0.0000} -> {c1:0.0000})");
        Check(f1 > f0 * 1.5 && f1 > c1, $"trans-noise: fine grain put back instead ({f0:0.0000} -> {f1:0.0000})");
        Check(Math.Abs(m1 - m0) < 0.002, $"trans-noise: average brightness kept (mean shift {m1 - m0:0.0000})");

        // Not a denoiser: with grain 0 only the blotch removal remains, with grain the residual stays clearly noisy.
        var noGrain = Make(true);
        f.D.Render(noGrain, Params(100, grain: 0), ctx);
        var (_, fz, _) = Stats(noGrain.G);
        Check(f1 > fz * 2, $"trans-noise: the grain slider controls the fine grain ({fz:0.0000} at 0 vs {f1:0.0000} at 100)");
        var bigGrain = Make(true);
        f.D.Render(bigGrain, Params(100, size: 100), ctx);
        var (_, fb, _) = Stats(bigGrain.G);
        Check(fb > 0 && Math.Abs(fb - f1) > 1e-4, $"trans-noise: grain size changes the grain ({f1:0.0000} vs {fb:0.0000})");

        // Edge contrast (0.2 step at x = 160) survives.
        double Side(PluginImage im, int x0, int x1)
        {
            double s = 0; int c = 0;
            for (int y = 20; y < H - 20; y++) for (int x = x0; x < x1; x++) { s += im.G[y * W + x] - Clean(x, y) + (x >= 160 ? 0.2 : 0); c++; }
            return s / c;
        }
        var cl = Make(false);
        f.D.Render(cl, Params(100), ctx);
        double step = Side(cl, 162, 170) - Side(cl, 150, 158);
        Check(Math.Abs(step - 0.2) < 0.02, $"trans-noise: edges keep their contrast (step {step:0.000})");
        double cleanDiff = 0;
        for (int i = 0; i < N; i++) cleanDiff += Math.Abs(cl.G[i] - Clean(i % W, i / W));
        cleanDiff /= N;
        Check(cleanDiff < 0.004, $"trans-noise: a clean photo stays nearly untouched (mean diff {cleanDiff:0.0000})");

        // Color blotches smoothed away.
        double ChromaStd(PluginImage im)
        {
            var d = new float[N];
            for (int i = 0; i < N; i++) d[i] = im.R[i] - im.B[i];
            var b = Blur(d, 3.0);
            double s = 0; int c = 0;
            for (int y = 20; y < H - 20; y++) for (int x = 20; x < 140; x++) { s += b[y * W + x] * (double)b[y * W + x]; c++; }
            return Math.Sqrt(s / c);
        }
        double ch0 = ChromaStd(src), ch1 = ChromaStd(outIm);
        Check(ch1 < ch0 * 0.5, $"trans-noise: color blotches smoothed ({ch0:0.0000} -> {ch1:0.0000})");

        // Deterministic (same grain on every render) and safe values.
        var again = Make(true);
        f.D.Render(again, Params(100), ctx);
        Check(Enumerable.Range(0, N).All(i => again.R[i] == outIm.R[i] && again.G[i] == outIm.G[i] && again.B[i] == outIm.B[i]),
              "trans-noise: the grain is the same on every render");
        var dark = new PluginImage(W, H);
        for (int i = 0; i < N; i++) { dark.R[i] = Math.Max(0, lumaN[i]); dark.G[i] = Math.Max(0, lumaN[i] * 0.5f); dark.B[i] = 0; }
        f.D.Render(dark, Params(100, grain: 200), ctx);
        Check(dark.R.Concat(dark.G).Concat(dark.B).All(v => float.IsFinite(v) && v >= 0), "trans-noise: near-black photo stays finite and non-negative");

        // Half-resolution preview: also turns blotches into grain and keeps the brightness.
        var small = new PluginImage(W / 2, H / 2);
        for (int y = 0; y < H / 2; y++)
        for (int x = 0; x < W / 2; x++)
        {
            int i = y * (W / 2) + x, j = 2 * y * W + 2 * x;
            small.R[i] = (src.R[j] + src.R[j + 1] + src.R[j + W] + src.R[j + W + 1]) / 4;
            small.G[i] = (src.G[j] + src.G[j + 1] + src.G[j + W] + src.G[j + W + 1]) / 4;
            small.B[i] = (src.B[j] + src.B[j + 1] + src.B[j + W] + src.B[j + W + 1]) / 4;
        }
        double before = small.G.Average(v => (double)v);
        var pctx = new RenderContext { Stage = FilterStage.Display, Space = PluginColorSpace.Srgb, IsPreview = true, Scale = 0.5, FullWidth = W, FullHeight = H, Cancel = CancellationToken.None };
        f.D.Render(small, Params(100), pctx);
        Check(small.G.All(float.IsFinite) && Math.Abs(small.G.Average(v => (double)v) - before) < 0.004,
              "trans-noise: half-resolution preview renders and keeps the brightness");

        // Several areas: one repeat group, each instance with its own mask and settings.
        var areas = reg.Filters.Where(o => o.PluginId == f.PluginId).OrderBy(o => o.D.Order).ToList();
        Check(areas.Count == 6 && areas.All(a => a.D.RepeatGroup == "Area" && a.D.SupportsMask) && areas[0].FullId == f.FullId
              && areas.Select(a => a.FullId).Distinct().Count() == areas.Count,
              $"trans-noise: {areas.Count} maskable areas in one repeat group, area 1 keeps the old id");

        var raw = new Raw16Image(W, H);
        for (int i = 0; i < N; i++)
        {
            raw.R[i] = (ushort)Math.Clamp(src.R[i] * 65535, 0, 65535);
            raw.G[i] = (ushort)Math.Clamp(src.G[i] * 65535, 0, 65535);
            raw.B[i] = (ushort)Math.Clamp(src.B[i] * 65535, 0, 65535);
        }
        static MaskSpec Disk(double cx, double cy) => new MaskSpec { Kind = MaskRender.Radial, P = new[] { cx, cy, 0.12, 0.15, 0.0 } };
        Raw16Image? RunAreas(double s1, double s2, bool mask2 = true)
        {
            var st = new PluginEditState();
            var amt = f.D.Params.First(p => p.Id == "amount");
            st.Active.Add(areas[0].FullId); st.SetValue(areas[0], amt, s1); st.Masks[areas[0].FullId] = Disk(0.25, 0.5);
            st.Active.Add(areas[1].FullId); st.SetValue(areas[1], amt, s2);
            if (mask2) st.Masks[areas[1].FullId] = Disk(0.75, 0.5);
            var job = new PluginRenderJob { Registry = reg, State = st, Meta = new ImageMeta { Width = W, Height = H }, IsPreview = false, Cancel = CancellationToken.None };
            return PluginPipeline.ApplyRaw16(raw, FilterStage.Display, job, W, H);
        }
        double Diff(Raw16Image? o, int x0, int x1, int y0, int y1)
        {
            if (o is null) return 0;
            double s = 0; int c = 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { s += Math.Abs(o.G[y * W + x] - raw.G[y * W + x]) / 65535.0; c++; }
            return s / c;
        }
        var two = RunAreas(100, 100);
        double in1 = Diff(two, 70, 90, 110, 130), in2 = Diff(two, 230, 250, 110, 130);
        double between = Diff(two, 150, 170, 20, 220), corner = Diff(two, 0, 20, 0, 20);
        Check(in1 > 0.003 && in2 > 0.003 && between < 1e-6 && corner < 1e-6,
              $"trans-noise: two areas with two masks change only inside them (in {in1:0.0000}/{in2:0.0000}, between {between:0.000000}, corner {corner:0.000000})");
        var oneOff = RunAreas(100, 0);
        Check(Diff(oneOff, 70, 90, 110, 130) > 0.003 && Diff(oneOff, 230, 250, 110, 130) < 1e-6,
              "trans-noise: each area has its own strength (area 2 at 0 leaves its mask untouched)");
        var global2 = RunAreas(100, 100, mask2: false);
        Check(Diff(global2, 150, 170, 20, 220) > 0.003, "trans-noise: an area without a mask covers the whole photo");
    }
}
