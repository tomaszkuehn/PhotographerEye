using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using ImgViewer;
using ImgViewer.Imaging;
using ImgViewer.Plugins;

internal static partial class Program
{
    private static void TestHeal()
    {
        var path = FindPluginDll("plugins-heal", "ImgViewer.Plugins.Heal.dll");
        if (path is null) { Check(false, "heal dll present (build plugins-heal first)"); return; }
        var entry = PluginManager.LoadFromFile(path);
        Check(entry.Error is null && entry.Plugins.Count == 1 && entry.Plugins[0].Error is null, $"heal loads ({entry.Error})");
        var reg = new PluginRegistry(entry.Plugins);
        const string P = "photographereye.heal:";
        var spots = Enumerable.Range(1, 8).Select(i => reg.FindFilter(P + "spot" + i)).ToList();
        Check(spots.All(f => f is not null && f.D.Stage == FilterStage.SceneLinear && !f.D.SupportsMask),
              "heal: 8 spot filters, scene-linear stage");
        Check(spots.All(f => f?.D.RepeatGroup == "Spot"), "heal: spots form one repeat group (one panel tool, max 8)");
        // Auto-placed source: 2.5 radii to the right, or another side near the edge, always inside the photo.
        var s1 = ImgViewer.MainWindow.RepeatSourceSpot(0.3, 0.4, 0.02, 0.03);
        var s2 = ImgViewer.MainWindow.RepeatSourceSpot(0.98, 0.5, 0.02, 0.03);
        var s3 = ImgViewer.MainWindow.RepeatSourceSpot(0.97, 0.97, 0.02, 0.03);
        Check(Math.Abs(s1.X - 0.35) < 1e-9 && s1.Y == 0.4 && Math.Abs(s2.X - 0.93) < 1e-9 && s2.Y == 0.5 &&
              s3.X - 0.02 >= 0 && s3.X + 0.02 <= 1 && s3.Y - 0.03 >= 0 && s3.Y + 0.03 <= 1,
              $"heal: auto source placed next to the defect inside the photo ({s1}, {s2}, {s3})");
        var f = spots[0];
        if (f is null) return;

        static string Pt(double x, double y) => x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture);
        FilterParams Params(string? target, string? source, double size, double feather, string mode, double opacity = 100)
        {
            var text = new Dictionary<string, string> { ["mode"] = mode };
            if (target is not null) text["target"] = target;
            if (source is not null) text["source"] = source;
            var vals = new Dictionary<string, double> { ["size"] = size, ["feather"] = feather, ["opacity"] = opacity };
            return new FilterParams(f.D.Params, vals, text);
        }

        // Clean image: smooth gradient (different per channel) times a fine texture. Defect: dark blob at (0.3, 0.4).
        const int W = 400, H = 300;
        static float Clean(int c, int x, int y)
        {
            double g = 0.15 + 0.5 * x / W + 0.2 * y / H + 0.05 * c;
            double tex = 1 + 0.08 * Math.Sin(x * 0.9) * Math.Cos(y * 0.7);
            return (float)(g * tex);
        }
        PluginImage Make(bool defect)
        {
            var im = new PluginImage(W, H);
            var planes = new[] { im.R, im.G, im.B };
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double dd = Math.Sqrt((x - 120.0) * (x - 120.0) + (y - 120.0) * (y - 120.0));
                double k = defect && dd < 6 ? 0.1 : 1;
                for (int c = 0; c < 3; c++) planes[c][y * W + x] = (float)(Clean(c, x, y) * k);
            }
            return im;
        }
        var ctx = new RenderContext { Stage = FilterStage.SceneLinear, Space = PluginColorSpace.LinearSrgb, FullWidth = W, FullHeight = H, Cancel = CancellationToken.None };
        double DiskErr(PluginImage im, double cx, double cy, double r)
        {
            double e = 0;
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if ((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy) > r * r) continue;
                e = Math.Max(e, Math.Abs(im.G[y * W + x] - Clean(1, x, y)) / Clean(1, x, y));
            }
            return e;
        }

        // Identity: no points, one point only, same point, zero opacity.
        Check(f.D.IsIdentity(Params(null, null, 2, 40, "heal")) && f.D.IsIdentity(Params(Pt(0.3, 0.4), null, 2, 40, "heal")) &&
              f.D.IsIdentity(Params(Pt(0.3, 0.4), Pt(0.3, 0.4), 2, 40, "heal")) && f.D.IsIdentity(Params(Pt(0.3, 0.4), Pt(0.6, 0.4), 2, 40, "heal", 0)) &&
              !f.D.IsIdentity(Params(Pt(0.3, 0.4), Pt(0.6, 0.4), 2, 40, "heal")),
              "heal: spot without both points (or same point / zero opacity) is identity");

        // Heal: source at the same texture phase but much brighter (gradient) -> tone matched to the surroundings.
        // Size 3% of 400 px = 12 px radius around (120.5, 120.5); source shifted by +2π/0.9·k px in x (texture repeats).
        double shift = 2 * Math.PI / 0.9 * 22; // ≈ 153.6 px
        var tgt = Pt(120.5 / W, 120.5 / H);
        var src = Pt((120.5 + shift) / W, 120.5 / H);
        var dirty = Make(true);
        double before = DiskErr(dirty, 120.5, 120.5, 6);
        var healed = Make(true);
        f.D.Render(healed, Params(tgt, src, 3, 30, "heal"), ctx);
        double after = DiskErr(healed, 120.5, 120.5, 12);
        Check(before > 0.5 && after < 0.03, $"heal: defect removed with the tone of the surroundings (max err {before:0.000} -> {after:0.000})");

        var cloned = Make(true);
        f.D.Render(cloned, Params(tgt, src, 3, 0, "clone"), ctx);
        double cloneErr = DiskErr(cloned, 120.5, 120.5, 11);
        Check(cloneErr > 0.15, $"heal: clone copies the brighter source as is (err vs clean {cloneErr:0.000})");
        // Clone with hard edge: pixels inside are the source pixels (integer shift => exact copy).
        var cl2 = Make(true);
        f.D.Render(cl2, Params(tgt, Pt((120.5 + 150) / W, 120.5 / H), 3, 0, "clone"), ctx);
        var orig = Make(true);
        bool exact = true, outside = true;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double d = Math.Sqrt((x + 0.5 - 120.5) * (x + 0.5 - 120.5) + (y + 0.5 - 120.5) * (y + 0.5 - 120.5));
            int i = y * W + x;
            if (d < 11.5 && Math.Abs(cl2.G[i] - orig.G[i + 150]) > 1e-4f) exact = false;
            if (d > 12.5 && (cl2.R[i] != orig.R[i] || cl2.G[i] != orig.G[i] || cl2.B[i] != orig.B[i])) outside = false;
        }
        Check(exact, "heal: clone with no feather copies the source pixels exactly");
        Check(outside, "heal: pixels outside the spot are untouched");

        // Opacity 50 -> halfway between the original and the repair at the centre.
        var half = Make(true);
        f.D.Render(half, Params(tgt, Pt((120.5 + 150) / W, 120.5 / H), 3, 0, "clone", 50), ctx);
        int ci = 120 * W + 120;
        Check(Math.Abs(half.G[ci] - (orig.G[ci] + orig.G[ci + 150]) / 2) < 1e-4, "heal: opacity blends with the original");

        // Preview at half resolution matches the full render (fractions and % radius are scale-free).
        var small = new PluginImage(W / 2, H / 2);
        for (int y = 0; y < H / 2; y++)
        for (int x = 0; x < W / 2; x++)
        {
            int i = y * (W / 2) + x;
            int j = 2 * y * W + 2 * x;
            small.R[i] = (healed.R[j] + healed.R[j + 1] + healed.R[j + W] + healed.R[j + W + 1]) / 4;
        }
        var dirtySmall = new PluginImage(W / 2, H / 2);
        var dirtyFull = Make(true);
        for (int y = 0; y < H / 2; y++)
        for (int x = 0; x < W / 2; x++)
        {
            int i = y * (W / 2) + x, j = 2 * y * W + 2 * x;
            dirtySmall.R[i] = (dirtyFull.R[j] + dirtyFull.R[j + 1] + dirtyFull.R[j + W] + dirtyFull.R[j + W + 1]) / 4;
            dirtySmall.G[i] = (dirtyFull.G[j] + dirtyFull.G[j + 1] + dirtyFull.G[j + W] + dirtyFull.G[j + W + 1]) / 4;
            dirtySmall.B[i] = (dirtyFull.B[j] + dirtyFull.B[j + 1] + dirtyFull.B[j + W] + dirtyFull.B[j + W + 1]) / 4;
        }
        var pctx = new RenderContext { Stage = FilterStage.SceneLinear, Space = PluginColorSpace.LinearSrgb, IsPreview = true, Scale = 0.5, FullWidth = W, FullHeight = H, Cancel = CancellationToken.None };
        f.D.Render(dirtySmall, Params(tgt, src, 3, 30, "heal"), pctx);
        double pe = 0;
        for (int y = 52; y < 68; y++)
        for (int x = 52; x < 68; x++)
            pe = Math.Max(pe, Math.Abs(dirtySmall.R[y * (W / 2) + x] - small.R[y * (W / 2) + x]) / small.R[y * (W / 2) + x]);
        Check(pe < 0.05, $"heal: half-resolution preview matches the full render (max rel diff {pe:0.000})");

        // Spot at the image edge (patch partly outside) does not throw and stays finite.
        var edge = Make(true);
        f.D.Render(edge, Params(Pt(0.002, 0.995), Pt(0.5, 0.5), 5, 50, "heal"), ctx);
        Check(edge.R.All(v => float.IsFinite(v) && v >= 0), "heal: spot at the image corner stays finite");

        // Pipeline: an active spot without points renders nothing; with points it runs.
        var raw = new Raw16Image(64, 48);
        for (int i = 0; i < raw.R.Length; i++) { raw.R[i] = (ushort)(i * 7 % 60000); raw.G[i] = (ushort)(i * 13 % 60000); raw.B[i] = 30000; }
        Raw16Image? Run(string? t, string? s)
        {
            var st = new PluginEditState();
            st.Active.Add(f.FullId);
            if (t is not null) st.SetText(f, f.D.Params.First(p => p.Id == "target"), t);
            if (s is not null) st.SetText(f, f.D.Params.First(p => p.Id == "source"), s);
            var job = new PluginRenderJob { Registry = reg, State = st, Meta = new ImageMeta { Width = 64, Height = 48 }, IsPreview = false, Cancel = CancellationToken.None };
            return PluginPipeline.ApplyRaw16(raw, FilterStage.SceneLinear, job, 64, 48);
        }
        Check(Run(null, null) is null, "heal: pipeline skips a spot without points");
        var ran = Run(Pt(0.3, 0.5), Pt(0.7, 0.5));
        Check(ran is not null && Enumerable.Range(0, raw.R.Length).Any(i => ran.R[i] != raw.R[i]), "heal: pipeline applies a spot with both points");
    }
}
