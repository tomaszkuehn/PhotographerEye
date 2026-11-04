using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;

internal static partial class Program
{
    private static void TestAdjustMask()
    {
        const int W = 64, H = 48;
        var neutral = new Raw16Image(W, H);
        var full = new Raw16Image(W, H);
        for (int i = 0; i < W * H; i++)
        {
            neutral.R[i] = neutral.G[i] = neutral.B[i] = 20000;
            full.R[i] = 50000; full.G[i] = 40000; full.B[i] = 30000;
        }
        var state = new PluginEditState();
        var job = new PluginRenderJob { Registry = PluginRegistry.Empty, State = state };
        Check(AdjustMask.For(state) is null, "adjust mask: none by default");
        state.Masks[AdjustMask.Key] = MaskRender.DefaultFor(MaskRender.Radial);
        var spec = AdjustMask.For(state);
        Check(spec is not null, "adjust mask: stored under the panel key");

        var o = AdjustMask.Blend16(full, neutral, spec!, job, W, H);
        int c = (H / 2) * W + W / 2, k = 0;
        Check(Math.Abs(o.R[c] - 50000) < 300 && Math.Abs(o.B[c] - 30000) < 300, $"adjust mask: centre gets the panel values ({o.R[c]})");
        Check(o.R[k] == 20000 && o.G[k] == 20000 && o.B[k] == 20000, $"adjust mask: corner keeps the neutral render ({o.R[k]})");

        var inv = AdjustMask.Blend16(full, neutral, spec! with { Invert = true }, job, W, H);
        Check(inv.R[k] == 50000 && Math.Abs(inv.R[c] - 20000) < 300, "adjust mask: invert swaps inside and outside");

        // 8-bit path (BGRA).
        byte[] nb = new byte[W * H * 4], fb = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            nb[i * 4] = nb[i * 4 + 1] = nb[i * 4 + 2] = 60; nb[i * 4 + 3] = 255;
            fb[i * 4] = 30; fb[i * 4 + 1] = 120; fb[i * 4 + 2] = 220; fb[i * 4 + 3] = 255;
        }
        var nbmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, nb, W * 4);
        var fbmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, fb, W * 4);
        nbmp.Freeze(); fbmp.Freeze();
        var o8 = AdjustMask.Blend8(fbmp, nbmp, spec!, job, W, H);
        var ob = new byte[W * H * 4];
        o8.CopyPixels(ob, W * 4, 0);
        Check(Math.Abs(ob[c * 4 + 2] - 220) <= 2 && Math.Abs(ob[c * 4] - 30) <= 2 && ob[2] == 60 && ob[3] == 255,
              $"adjust mask: 8-bit blend (centre R {ob[c * 4 + 2]}, corner R {ob[2]})");

        // Whole-photo (empty kind) is not a mask.
        state.Masks[AdjustMask.Key] = new MaskSpec { Kind = "" };
        Check(AdjustMask.For(state) is null, "adjust mask: empty kind means whole photo");

        // Linked masks: a filter uses the Adjustments radial; edits of the source follow, invert is relative, removal detaches.
        const string fk = "sample.grade:grade";
        var ls = new PluginEditState();
        ls.Masks[AdjustMask.Key] = new MaskSpec { Kind = MaskRender.Radial, P = new[] { 0.3, 0.4, 0.1, 0.1, 0.5 } };
        ls.Masks[fk] = new MaskSpec { Kind = MaskRender.Link, Ref = AdjustMask.Key };
        var r1 = ls.Resolve(ls.Masks[fk]);
        Check(r1 is { Kind: MaskRender.Radial } && r1.P![0] == 0.3 && !r1.Invert, "linked mask: resolves to the Adjustments radial");
        ls.Masks[AdjustMask.Key] = ls.Masks[AdjustMask.Key] with { P = new[] { 0.6, 0.4, 0.1, 0.1, 0.5 } };
        Check(ls.Resolve(ls.Masks[fk])?.P?[0] == 0.6, "linked mask: follows edits of the source");
        ls.Masks[fk] = ls.Masks[fk] with { Invert = true };
        Check(ls.Resolve(ls.Masks[fk])?.Invert == true, "linked mask: invert of the link");
        ls.Masks[AdjustMask.Key] = ls.Masks[AdjustMask.Key] with { Invert = true };
        Check(ls.Resolve(ls.Masks[fk])?.Invert == false && AdjustMask.For(ls)?.Invert == true, "linked mask: invert is relative to the source");
        Check(ls.LinksTo(AdjustMask.Key).SequenceEqual(new[] { fk }), "linked mask: LinksTo lists the users");
        ls.Masks[AdjustMask.Key] = new MaskSpec { Kind = MaskRender.Link, Ref = fk };
        Check(AdjustMask.For(ls) is null && ls.Resolve(ls.Masks[fk]) is null, "linked mask: a link to a link is not used (no chains)");
        ls.Masks[AdjustMask.Key] = new MaskSpec { Kind = MaskRender.Brush, Strokes = new List<double[]> { new[] { 1, 0.05, 0.2, 0.2, 0.3, 0.3 } } };
        ls.DetachLinksTo(AdjustMask.Key);
        ls.Masks.Remove(AdjustMask.Key);
        var own = ls.Masks.TryGetValue(fk, out var om) ? om : null;
        Check(own is { Kind: MaskRender.Brush, Ref: null, Invert: true } && own.Strokes!.Count == 1, "linked mask: removing the source leaves an own copy");
        ls.Masks[fk] = new MaskSpec { Kind = MaskRender.Link, Ref = AdjustMask.Key };
        Check(ls.Resolve(ls.Masks[fk]) is null, "linked mask: a missing source means whole photo");
        var cl = new PluginEditState();
        cl.CopyFrom(ls);
        Check(cl.Masks[fk].Ref == AdjustMask.Key, "linked mask: copies keep the link");

        // Two sets of values: the mask's own add to the whole-photo values, clamped to the slider range.
        var exp = CoreParams.Find("Exposure")!;
        var vs = new PluginEditState();
        vs.Masks[AdjustMask.Key] = new MaskSpec { Kind = MaskRender.Radial, P = new[] { 0.5, 0.5, 0.2, 0.2, 0.5 } };
        Check(AdjustMask.Values(vs) is null, "mask values: a new mask has none (the photo does not change)");
        Check(AdjustMask.Combine(exp, 0.7, null) == 0.7, "mask values: none keeps the whole-photo value");
        vs.Masks[AdjustMask.Key] = vs.Masks[AdjustMask.Key] with { Adjust = new Dictionary<string, double> { ["Exposure"] = 0.5 } };
        var vals = AdjustMask.Values(vs);
        Check(vals is not null && Math.Abs(AdjustMask.Combine(exp, 0.7, vals) - 1.2) < 1e-9, "mask values: added to the whole-photo value");
        Check(AdjustMask.Combine(exp, 4.8, vals) == exp.Max, "mask values: sum clamped to the slider range");
        Check(AdjustMask.Combine(CoreParams.Find("Contrast")!, 12, vals) == 12, "mask values: other sliders keep the whole-photo value");
        var vc = PluginEditState.CloneMask(vs.Masks[AdjustMask.Key]);
        vc.Adjust!["Exposure"] = 2;
        Check(vs.Masks[AdjustMask.Key].Adjust!["Exposure"] == 0.5, "mask values: a cloned mask has its own copy");
        var vs2 = new PluginEditState();
        vs2.CopyFrom(vs);
        Check(vs2.ContentEquals(vs), "mask values: copies are equal");
        vs2.Masks[AdjustMask.Key] = vs2.Masks[AdjustMask.Key] with { Adjust = new Dictionary<string, double> { ["Exposure"] = 0.6 } };
        Check(!vs2.ContentEquals(vs), "mask values: a change is seen by undo/history");
        // the Adjustments mask linked to a filter's mask keeps its values when the source goes away
        var lv = new PluginEditState();
        lv.Masks[fk] = new MaskSpec { Kind = MaskRender.Radial, P = new[] { 0.3, 0.3, 0.1, 0.1, 0.5 } };
        lv.Masks[AdjustMask.Key] = new MaskSpec { Kind = MaskRender.Link, Ref = fk, Adjust = new Dictionary<string, double> { ["Exposure"] = -1 } };
        Check(AdjustMask.Values(lv)?["Exposure"] == -1 && AdjustMask.For(lv)?.Kind == MaskRender.Radial, "mask values: a linked Adjustments mask keeps its values");
        lv.DetachLinksTo(fk);
        Check(lv.Masks[AdjustMask.Key] is { Kind: MaskRender.Radial, Ref: null } dm && dm.Adjust?["Exposure"] == -1, "mask values: kept when the link becomes an own copy");
        var eyeJson = System.Text.Json.JsonSerializer.Serialize(new MaskSpec { Kind = MaskRender.Radial, Adjust = new Dictionary<string, double> { ["Tint"] = 5 } });
        Check(System.Text.Json.JsonSerializer.Deserialize<MaskSpec>(eyeJson)?.Adjust?["Tint"] == 5, "mask values: stored in the .eye JSON");
    }
}
