using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ImgViewer;
using ImgViewer.Controls;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;

internal static partial class Program
{
    private static void TestPluginApi()
    {
        // --- EditChanged keys ---
        var a = new EyeState();
        var b = a with { CropX = 0.1, CropW = 0.8, CropAspect = "3:2", Exposure = 0.5, ShadowLift = 10 };
        var k = EditChangeKeys.Map(a, b, out var tg);
        Check(k.Count(x => x == "core.Crop") == 1 && k.Contains("core.Exposure") && k.Contains("core.Shadows") && tg.Count == 0,
              $"api keys: crop fields collapse to core.Crop, ShadowLift → core.Shadows ({string.Join(",", k)})");
        k = EditChangeKeys.Map(a, a with { StretchEv = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.2 } } }, out _);
        Check(k.SequenceEqual(new[] { "core.Stretch" }), $"api keys: StretchEv → core.Stretch ({string.Join(",", k)})");
        var p1 = a with
        {
            PluginValues = new Dictionary<string, double> { ["p:f/amount"] = 1 },
            PluginActive = new List<string> { "p:f", "p:g" },
            PluginOrder = new List<string> { "p:f", "p:g" }
        };
        var p2 = p1 with
        {
            PluginValues = new Dictionary<string, double> { ["p:f/amount"] = 2 },
            PluginActive = new List<string> { "p:f", "p:h" },
            PluginOrder = new List<string> { "p:g", "p:f" },
            PluginMasks = new Dictionary<string, MaskSpec> { ["p:f"] = new MaskSpec { Kind = "radial", P = new[] { 0.5, 0.5, 0.2, 0.2, 0.5 } } }
        };
        k = EditChangeKeys.Map(p1, p2, out tg);
        Check(k.Contains("p:f/amount") && k.Contains("p:f#mask") && k.Contains("p:h#active") && k.Contains("p:g#active") && k.Contains("#order"),
              $"api keys: plugin value, mask, active and order keys ({string.Join(",", k)})");
        Check(tg.Count == 2 && tg.Contains(("p:h", true)) && tg.Contains(("p:g", false)), "api keys: FilterToggled on / off");
        Check(EditChangeKeys.Map(p1, p1 with { }, out _).Count == 0, "api keys: equal states give no keys");

        // --- core keys ---
        Check(PluginEditOps.CoreKey("core.exposure") == "Exposure" && PluginEditOps.CoreKey("ShadowLift") == "Shadows" &&
              PluginEditOps.CoreKey("MONO") == "Mono" && PluginEditOps.CoreKey("bogus") is null && PluginEditOps.CoreKey(null) is null,
              "api core: key aliases and case");
        var s = new EyeState();
        bool roundTrip = PluginEditOps.CoreKeyNames.All(n =>
        {
            var v = n == "Mono" ? 2 : 0.37;
            return Math.Abs(PluginEditOps.CoreGet(PluginEditOps.CoreSet(s, n, v), n) - v) < 1e-12;
        });
        Check(roundTrip && PluginEditOps.CoreSet(s, "Shadows", 12).ShadowLift == 12 && PluginEditOps.CoreSet(s, "Mono", 1.6).Mono == 2,
              "api core: get/set round-trip for every key, Mono rounded");
        Check(PluginEditOps.MonoMax == 2, "api core: Mono range 0..2");
        bool threw = false;
        try { PluginEditOps.CoreGet(s, "Nope"); } catch (ArgumentException) { threw = true; }
        Check(threw, "api core: unknown key throws ArgumentException");

        // --- curves ---
        var c = PluginEditOps.CleanCurve(new[] { new[] { 0.8, 1.5 }, new[] { -0.2, 0.1 }, new[] { 0.4, 0.4 } }, 0, 1)!;
        Check(c.Length == 3 && c[0][0] == 0 && c[0][1] == 0.1 && c[1][0] == 0.4 && c[2][1] == 1,
              "api curve: points clamped and sorted by input");
        Check(PluginEditOps.CleanCurve(null, 0, 1) is null && PluginEditOps.CleanCurve(Array.Empty<double[]>(), 0, 1) is null,
              "api curve: empty → null (reset)");
        threw = false;
        try { PluginEditOps.CleanCurve(new[] { new[] { 0.5 } }, 0, 1); } catch (ArgumentException) { threw = true; }
        bool threwNan = false;
        try { PluginEditOps.CleanCurve(new[] { new[] { 0.5, double.NaN } }, 0, 1); } catch (ArgumentException) { threwNan = true; }
        Check(threw && threwNan, "api curve: bad pairs and NaN throw");

        // --- masks ---
        var rad4 = PluginEditOps.ToMaskSpec(new MaskInfo { Kind = "radial", P = new[] { 0.5, 0.5, 0.2, 0.3 } })!;
        Check(rad4.P!.Length == 5 && rad4.P[4] == 0.5, "api mask: radial with 4 values gets feather 0.5");
        bool Bad(MaskInfo m) { try { PluginEditOps.ToMaskSpec(m); return false; } catch (ArgumentException) { return true; } }
        Check(Bad(new MaskInfo { Kind = "linear", P = new[] { 0.1, 0.2 } }) && Bad(new MaskInfo { Kind = "luma" }) &&
              Bad(new MaskInfo { Kind = "brush" }) && Bad(new MaskInfo { Kind = "brush", Strokes = new List<double[]> { new[] { 1.0, 0.1 } } }) &&
              Bad(new MaskInfo { Kind = "star", P = new[] { 0.0 } }) && Bad(new MaskInfo { Kind = "linear", P = new[] { 0, 0, double.PositiveInfinity, 1 } }),
              "api mask: invalid kinds and points rejected");
        Check(PluginEditOps.ToMaskSpec(null) is null && PluginEditOps.ToMaskSpec(new MaskInfo { Kind = " " }) is null, "api mask: empty kind → no mask");
        var brush = new MaskInfo
        {
            Kind = "brush", Invert = true,
            Strokes = new List<double[]> { new[] { 1.0, 0.05, 0.1, 0.1, 0.2, 0.2 } }
        };
        var bs = PluginEditOps.ToMaskSpec(brush)!;
        brush.Strokes[0][2] = 0.9;
        var back = PluginEditOps.ToMaskInfo(bs)!;
        Check(back.Kind == "brush" && back.Invert && back.Strokes.Count == 1 && back.Strokes[0][2] == 0.1,
              "api mask: brush round-trip, data copied (caller changes do not leak)");
        var pm = PluginEditOps.ToMaskSpec(new MaskInfo { Kind = "plugin:x:sky", Values = new Dictionary<string, double> { ["t"] = 0.4 } })!;
        Check(pm.Kind == "plugin:x:sky" && pm.Values!["t"] == 0.4 && PluginEditOps.ToMaskInfo(null) is null, "api mask: plugin mask with values");

        // --- Output stage masks: mapped rendering ---
        var lin = new MaskSpec { Kind = "linear", P = new[] { 0.25, 0.5, 0.75, 0.5 } };
        var m0 = MaskRender.RenderMapped(lin, 100, 50, GeoState.Default, 1000, 500);
        Check(m0[25 * 100 + 10] > 0.99 && m0[25 * 100 + 90] < 0.01 && Math.Abs(m0[25 * 100 + 50] - 0.5) < 0.05 &&
              Math.Abs(m0[0 * 100 + 50] - m0[49 * 100 + 50]) < 1e-4,
              $"api mask out: linear gradient along x without geometry ({m0[25 * 100 + 10]:0.00} {m0[25 * 100 + 50]:0.00} {m0[25 * 100 + 90]:0.00})");
        var rot = GeoState.Default.WithRotation(1);
        var mr = MaskRender.RenderMapped(lin, 50, 100, rot, 1000, 500);
        Check(Math.Abs(mr[10 * 50 + 5] - mr[10 * 50 + 45]) < 1e-3 && Math.Abs(mr[10 * 50 + 25] - (1 - mr[90 * 50 + 25])) < 0.05 &&
              Math.Abs(mr[10 * 50 + 25] - mr[90 * 50 + 25]) > 0.9,
              $"api mask out: rotated 90° the gradient runs vertically ({mr[10 * 50 + 25]:0.00} → {mr[90 * 50 + 25]:0.00})");
        var crop = GeoState.Default with { CropX = 0.5, CropW = 0.5 };
        var mc = MaskRender.RenderMapped(lin, 50, 50, crop, 1000, 500);
        Check(mc[25 * 50 + 1] > 0.45 && mc[25 * 50 + 1] < 0.55 && mc[25 * 50 + 48] < 0.01,
              $"api mask out: right-half crop starts at the gradient middle ({mc[25 * 50 + 1]:0.00})");
        var inv = MaskRender.RenderMapped(lin with { Invert = true }, 100, 50, GeoState.Default, 1000, 500);
        Check(Math.Abs(inv[25 * 100 + 10] + m0[25 * 100 + 10] - 1) < 1e-5, "api mask out: invert");

        // --- downscale ---
        var raw = new Raw16Image(4, 2);
        for (int i = 0; i < 8; i++) { raw.R[i] = (ushort)(i * 1000); raw.G[i] = 2000; raw.B[i] = 65535; }
        var d = PluginEditOps.Downscale(raw, 2, 1f / 65535);
        Check(d.Width == 2 && d.Height == 1 && Math.Abs(d.R[0] - (0 + 1000 + 4000 + 5000) / 4.0 / 65535) < 1e-6 && Math.Abs(d.B[1] - 1) < 1e-6,
              $"api pixels: area-averaged downscale and scale ({d.Width}x{d.Height})");
        var full = PluginEditOps.Downscale(raw, 0, 2f);
        Check(full.Width == 4 && full.R[7] == 14000, "api pixels: no limit keeps full size");

        // --- overlay handle ---
        var h = new OverlayHandle(new OverlayDescriptor { Id = "o", DefaultVisible = true });
        int refreshed = 0;
        h.OnRefresh = () => refreshed++;
        h.Visible = true;
        h.Visible = false;
        h.Refresh();
        Check(refreshed == 2 && !h.Visible, "api overlay: visibility change and Refresh call the host once each");

        // --- overlay canvas ---
        var v = MakeViewer(new Size(800, 533), MakeSource(1500, 1000), new Size(3000, 2000));
        var cv = new PluginOverlayCanvas();
        cv.Attach(v);
        cv.SetMapping(GeoState.Default, 3000, 2000);
        cv.SetLayers(new[]
        {
            new OverlayContent
            {
                Shapes = new List<OverlayShape>
                {
                    new() { Kind = OverlayShapeKind.Line, Points = new[] { 0.0, 0, 1, 1 } },
                    new() { Kind = OverlayShapeKind.Rectangle, Points = new[] { 0.1, 0.1, 0.2, 0.2 }, Dashed = true },
                    new() { Kind = OverlayShapeKind.Ellipse, Points = new[] { 0.5, 0.5, 0.1, 0.1 }, Fill = 0x80FF0000 },
                    new() { Kind = OverlayShapeKind.Polygon, Points = new[] { 0.0, 0, 0.5, 0, 0.5, 0.5 } },
                    new() { Kind = OverlayShapeKind.Text, Points = new[] { 0.5, 0.5 }, Text = "hi" },
                    new() { Kind = OverlayShapeKind.Text, Points = new[] { 0.5, 0.5 } },
                    new() { Kind = OverlayShapeKind.Line, Points = new[] { 0.0, double.NaN, 1, 1 } },
                    new() { Kind = OverlayShapeKind.Polyline, Points = new[] { 0.0 } },
                    new() { Kind = OverlayShapeKind.Line }
                },
                Bitmap = new OverlayBitmap { Width = 2, Height = 2, Rgba = new byte[16] }
            },
            new OverlayContent { Bitmap = new OverlayBitmap { Width = 4, Height = 4, Rgba = new byte[3] } }
        });
        Check(cv.LayerCount == 2 && cv.ElementCount == 6, $"api overlay: valid shapes and bitmap drawn, malformed skipped ({cv.ElementCount})");
        Check(!cv.IsHitTestVisible, "api overlay: canvas never takes the mouse");
        var f = v.ImageRect;
        cv.SetMapping(GeoState.Default with { CropX = 0.5, CropW = 0.5 }, 3000, 2000);
        cv.SetLayers(new[]
        {
            new OverlayContent
            {
                Space = OverlaySpace.Source,
                Shapes = new List<OverlayShape> { new() { Kind = OverlayShapeKind.Line, Points = new[] { 0.5, 0.0, 1.0, 1.0 } } }
            }
        });
        var ln = cv.Children.OfType<System.Windows.Shapes.Line>().Single();
        Check(Math.Abs(ln.X1 - f.X) < 0.5 && Math.Abs(ln.X2 - (f.X + f.Width)) < 0.5 && Math.Abs(ln.Y2 - (f.Y + f.Height)) < 0.5,
              "api overlay: source-space shapes follow the crop");
        cv.SetLayers(Array.Empty<OverlayContent>());
        Check(cv.ElementCount == 0, "api overlay: empty layers clear the canvas");
    }
}
