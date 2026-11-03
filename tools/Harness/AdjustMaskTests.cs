using System;
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
    }
}
