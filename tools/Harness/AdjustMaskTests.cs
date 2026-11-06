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

        // Any mask can hold values of its own (a filter's mask, a link from a filter), not only the Adjustments mask.
        var pm = new PluginEditState();
        pm.Masks[fk] = new MaskSpec { Kind = MaskRender.Radial, P = new[] { 0.25, 0.5, 0.15, 0.15, 0.3 }, Adjust = new Dictionary<string, double> { ["Exposure"] = -1 } };
        pm.Masks["sample.other:other"] = new MaskSpec { Kind = MaskRender.Link, Ref = fk, Invert = true, Adjust = new Dictionary<string, double> { ["Contrast"] = 10 } };
        Check(AdjustMask.For(pm) is null && AdjustMask.For(pm, fk)?.Kind == MaskRender.Radial, "filter mask values: the filter's mask by its key");
        Check(AdjustMask.Values(pm, fk)?["Exposure"] == -1 && AdjustMask.Values(pm) is null, "filter mask values: kept on the filter's mask");
        Check(AdjustMask.For(pm, "sample.other:other") is { Invert: true } && AdjustMask.Values(pm, "sample.other:other")?["Contrast"] == 10,
              "filter mask values: a link has the source shape and values of its own");
        pm.Masks["x.none:none"] = new MaskSpec { Kind = "", Adjust = new Dictionary<string, double> { ["Exposure"] = 1 } };
        Check(AdjustMask.For(pm, "x.none:none") is null, "filter mask values: whole photo (no kind) is no mask");

        // Two masks add their changes onto each other: left radial -> full, right radial -> full2.
        var two = new PluginEditState();
        two.Masks[AdjustMask.Key] = new MaskSpec { Kind = MaskRender.Radial, P = new[] { 0.2, 0.5, 0.1, 0.1, 0.0 } };
        two.Masks[fk] = new MaskSpec { Kind = MaskRender.Radial, P = new[] { 0.8, 0.5, 0.1, 0.1, 0.0 } };
        var job2 = new PluginRenderJob { Registry = PluginRegistry.Empty, State = two };
        var full2 = new Raw16Image(W, H);
        for (int i = 0; i < W * H; i++) full2.R[i] = full2.G[i] = full2.B[i] = 10000;
        var step1 = AdjustMask.Blend16(full, neutral, AdjustMask.For(two)!, job2, W, H);
        var step2 = AdjustMask.Blend16(full2, neutral, AdjustMask.For(two, fk)!, job2, W, H, onto: step1);
        int left = (H / 2) * W + (int)(0.2 * W), right = (H / 2) * W + (int)(0.8 * W);
        Check(Math.Abs(step2.R[left] - 50000) < 300 && Math.Abs(step2.R[right] - 10000) < 300 && step2.R[k] == 20000,
              $"two masks: each changes only its own area (left {step2.R[left]}, right {step2.R[right]}, corner {step2.R[k]})");
        var nb2 = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { nb2[i * 4] = nb2[i * 4 + 1] = nb2[i * 4 + 2] = 10; nb2[i * 4 + 3] = 255; }
        var f2bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, nb2, W * 4);
        f2bmp.Freeze();
        var s8 = AdjustMask.Blend8(f2bmp, nbmp, AdjustMask.For(two, fk)!, job2, W, H,
            onto: AdjustMask.Blend8(fbmp, nbmp, AdjustMask.For(two)!, job2, W, H));
        var sb8 = new byte[W * H * 4];
        s8.CopyPixels(sb8, W * 4, 0);
        Check(Math.Abs(sb8[left * 4 + 2] - 220) <= 2 && Math.Abs(sb8[right * 4 + 2] - 10) <= 2 && sb8[2] == 60,
              $"two masks: 8-bit (left R {sb8[left * 4 + 2]}, right R {sb8[right * 4 + 2]}, corner R {sb8[2]})");

        // MASK-module masks: independent library masks ("mask:N") that tools follow by a link.
        var lib = new PluginEditState();
        Check(PluginEditState.IsLibraryKey("mask:2") && !PluginEditState.IsLibraryKey("core:adjustments") && !PluginEditState.IsLibraryKey(fk),
              "library masks: IsLibraryKey");
        Check(PluginEditState.LibraryIndex("mask:7") == 7 && PluginEditState.LibraryIndex(fk) == 0, "library masks: LibraryIndex");
        string k1 = lib.NewLibraryMaskKey();
        Check(k1 == "mask:1", "library masks: the first key is mask:1");
        lib.Masks[k1] = MaskRender.DefaultFor(MaskRender.Radial);
        lib.Masks[fk] = new MaskSpec { Kind = MaskRender.Link, Ref = k1 };
        Check(lib.NewLibraryMaskKey() == "mask:2", "library masks: the next key skips the used ones");
        string k2 = lib.NewLibraryMaskKey();
        lib.Masks[k2] = MaskRender.DefaultFor(MaskRender.Brush);
        // creating the second mask did not touch the first: the same shape, the tool still follows the first
        Check(lib.Masks[k1].Kind == MaskRender.Radial && lib.Resolve(lib.Masks[fk]) is { Kind: MaskRender.Radial } && lib.LinksTo(k1).SequenceEqual(new[] { fk }),
              "library masks: creating one leaves the others and their users untouched");
        Check(lib.Resolve(lib.Masks[fk]) is { Kind: MaskRender.Radial } rm && ReferenceEquals(rm, lib.Masks[k1]),
              "library masks: a link resolves to the library record (rendered once per pass)");
        lib.Masks[k1] = lib.Masks[k1] with { P = new[] { 0.7, 0.3, 0.1, 0.1, 0.5 } };
        Check(lib.Resolve(lib.Masks[fk])?.P?[0] == 0.7, "library masks: a tool follows edits of the mask it uses");
        lib.DetachLinksTo(k1);
        lib.Masks.Remove(k1);
        Check(lib.Masks[fk] is { Kind: MaskRender.Radial, Ref: null }, "library masks: removing the mask leaves the tool an own copy");
        Check(lib.NewLibraryMaskKey() == "mask:1", "library masks: a removed key is free again");
        lib.Masks[k1] = MaskRender.DefaultFor(MaskRender.Radial);
        lib.Masks["sample.other:grade"] = new MaskSpec { Kind = MaskRender.Radial };
        var missing = lib.MissingPlugins(PluginRegistry.Empty);
        Check(missing.SequenceEqual(new[] { "sample.grade", "sample.other" }), "library masks: library keys are not missing plugins");

        // Feather of a radial mask by the outer outline: 0 = on the inner ellipse, 100 % = twice its diameter.
        Check(MaskRender.RadialBandFraction(0) == 0 && Math.Abs(MaskRender.RadialBandFraction(1) - 0.5) < 1e-12,
              "radial feather: 0 % is a hard edge, 100 % stores the band 0.5");
        Check(Math.Abs(MaskRender.RadialBandFraction(0.5) - 1.0 / 3) < 1e-12, "radial feather: 50 % stores the band 1/3");
        Check(MaskRender.RadialUserFeather(0) == 0 && Math.Abs(MaskRender.RadialUserFeather(0.5) - 1) < 1e-12 &&
              Math.Abs(MaskRender.RadialUserFeather(1.0 / 3) - 0.5) < 1e-12,
              "radial feather: the user scale of stored bands (legacy bands over 0.5 read as 100 %)");
        bool roundTrip = true;
        foreach (double f in new[] { 0.0, 0.25, 0.5, 1.0 })
            if (Math.Abs(MaskRender.RadialUserFeather(MaskRender.RadialBandFraction(f)) - f) > 1e-12) roundTrip = false;
        Check(roundTrip, "radial feather: band and user scale are inverse");
        var (grab, grabScale) = MaskRender.RadialFeatherDrag(0.5, 2.0); // pointer on the grabbed outer outline
        Check(Math.Abs(grab - 0.5) < 1e-12 && Math.Abs(grabScale - 1) < 1e-12, "radial feather drag: grabbing the outline does not move it");
        var (hard, hardScale) = MaskRender.RadialFeatherDrag(0.5, 1.0); // dragged onto the inner ellipse
        Check(hard == 0 && Math.Abs(hardScale - 0.5) < 1e-12, "radial feather drag: 0 % = the outer sits on the inner");
        var (soft, softScale) = MaskRender.RadialFeatherDrag(0.5, 3.0); // past twice the inner: capped at 100 %
        Check(Math.Abs(soft - 0.5) < 1e-12 && Math.Abs(softScale - 1) < 1e-12, "radial feather drag: 100 % = twice the inner, no further");
        var (fromHard, fromHardScale) = MaskRender.RadialFeatherDrag(0.0, 2.0); // from a hard edge out to twice the circle
        Check(Math.Abs(fromHard - 0.5) < 1e-12 && Math.Abs(fromHardScale - 2) < 1e-12, "radial feather drag: from a hard edge the outer grows to twice the inner");
    }
}
