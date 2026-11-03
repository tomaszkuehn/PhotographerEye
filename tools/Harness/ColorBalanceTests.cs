using System;
using System.IO;
using System.Linq;
using System.Threading;
using ImgViewer;
using ImgViewer.Imaging;
using ImgViewer.Plugins;

internal static partial class Program
{
    private static string? FindPluginDll(string folder, string dll)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var found = new[] { "Release", "Debug" }
                .Select(cfg => Path.Combine(dir.FullName, "eyePlugins", folder, "bin", cfg, "netstandard2.0", dll))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }

    private static void TestColorBalance()
    {
        var path = FindPluginDll("colorbalance", "ImgViewer.Plugins.ColorBalance.dll");
        if (path is null) { Check(false, "color balance dll present (build eyePlugins/colorbalance first)"); return; }
        var entry = PluginManager.LoadFromFile(path);
        Check(entry.Error is null && entry.Plugins.Count == 1 && entry.Plugins[0].Error is null, $"color balance loads ({entry.Error})");
        var reg = new PluginRegistry(entry.Plugins);
        const string P = "photographereye.colorbalance:";
        var fs = reg.FindFilter(P + "shadows");
        var fm = reg.FindFilter(P + "midtones");
        var fh = reg.FindFilter(P + "highlights");
        Check(fs is not null && fm is not null && fh is not null &&
              new[] { fs, fm, fh }.All(f => f!.D.Stage == FilterStage.Display && f.D.Placement == FilterPlacement.Panel && f.D.SupportsMask),
              "color balance: shadows/midtones/highlights panel filters, display stage, maskable");
        Check(reg.Presets.Count(p => p.FullId.StartsWith(P, StringComparison.Ordinal)) == 4, "color balance: 4 presets");
        if (fs is null || fm is null || fh is null) return;

        // Gray ramp 0..1 (sRGB encoded), one row.
        const int W = 256;
        var ramp = new Raw16Image(W, 1);
        for (int x = 0; x < W; x++) { ushort v = (ushort)Math.Round(x / (double)(W - 1) * 65535); ramp.R[x] = v; ramp.G[x] = v; ramp.B[x] = v; }

        Raw16Image? Run(Registered<FilterDescriptor> f, double cr, double mg, double yb, bool lum = true)
        {
            var st = new PluginEditState();
            st.Active.Add(f.FullId);
            void Set(string id, double v) => st.SetValue(f, f.D.Params.First(p => p.Id == id), v);
            Set("cr", cr); Set("mg", mg); Set("yb", yb); Set("lum", lum ? 1 : 0);
            var job = new PluginRenderJob { Registry = reg, State = st, Meta = new ImageMeta { Width = W, Height = 1 }, IsPreview = false, Cancel = CancellationToken.None };
            return PluginPipeline.ApplyRaw16(ramp, FilterStage.Display, job, W, 1);
        }
        static double C(ushort v) => v / 65535.0;
        static double Lu(Raw16Image im, int i) => 0.2126 * C(im.R[i]) + 0.7152 * C(im.G[i]) + 0.0722 * C(im.B[i]);
        static double RedCast(Raw16Image im, int i) => C(im.R[i]) - (C(im.G[i]) + C(im.B[i])) / 2;
        int dark = 40, mid = 128, bright = 225;

        var zero = Run(fm, 0, 0, 0);
        Check(zero is null || Enumerable.Range(0, W).All(i => zero.R[i] == ramp.R[i] && zero.G[i] == ramp.G[i] && zero.B[i] == ramp.B[i]),
              "color balance: all zero is identity");

        var sRed = Run(fs, 60, 0, 0);
        Check(sRed is not null && RedCast(sRed, dark) > 0.05 && Math.Abs(RedCast(sRed, bright)) < 0.01,
              $"color balance: shadows +red tints darks only (dark {RedCast(sRed!, dark):0.000}, bright {RedCast(sRed!, bright):0.000})");

        var hBlue = Run(fh, 0, 0, 60);
        double BlueCast(Raw16Image im, int i) => C(im.B[i]) - (C(im.R[i]) + C(im.G[i])) / 2;
        Check(hBlue is not null && BlueCast(hBlue, bright) > 0.05 && Math.Abs(BlueCast(hBlue, dark)) < 0.01,
              $"color balance: highlights +blue tints brights only (bright {BlueCast(hBlue!, bright):0.000}, dark {BlueCast(hBlue!, dark):0.000})");

        var mGreen = Run(fm, 0, 50, 0);
        double GreenCast(Raw16Image im, int i) => C(im.G[i]) - (C(im.R[i]) + C(im.B[i])) / 2;
        var mMag = Run(fm, 0, -50, 0);
        Check(mGreen is not null && mMag is not null && GreenCast(mGreen, mid) > 0.03 && GreenCast(mMag, mid) < -0.03 &&
              GreenCast(mGreen, mid) > Math.Abs(GreenCast(mGreen, 3)) * 5,
              $"color balance: midtones +green / -magenta at mid gray ({GreenCast(mGreen!, mid):+0.000;-0.000} / {GreenCast(mMag!, mid):+0.000;-0.000})");

        var cyan = Run(fm, -50, 0, 0);
        var yellow = Run(fm, 0, 0, -50);
        Check(cyan is not null && yellow is not null && RedCast(cyan, mid) < -0.03 && BlueCast(yellow, mid) < -0.03,
              "color balance: negative ends add cyan and yellow");

        double maxDl = 0;
        foreach (var im in new[] { sRed, hBlue, mGreen, mMag, cyan, yellow })
            for (int i = 0; i < W; i++) maxDl = Math.Max(maxDl, Math.Abs(Lu(im!, i) - Lu(ramp, i)));
        Check(maxDl < 1.5 / 255, $"color balance: preserve luminosity keeps luma (max diff {maxDl * 255:0.00}/255)");

        var full = Run(fh, 100, 100, 100);
        var fullS = Run(fs, -100, -100, -100);
        Check(full is not null && fullS is not null &&
              full.R[W - 1] == 65535 && full.G[W - 1] == 65535 && full.B[W - 1] == 65535 &&
              fullS.R[0] == 0 && fullS.G[0] == 0 && fullS.B[0] == 0,
              "color balance: pure white and black stay neutral with preserve luminosity");

        var noLum = Run(fm, 0, 0, 60, lum: false);
        Check(noLum is not null && BlueCast(noLum, mid) > 0.05 && Lu(noLum, mid) > Lu(ramp, mid) + 0.002,
              "color balance: without preserve luminosity the shift also changes brightness");

        var hRedS = Run(fs, 100, 0, 0);
        bool monotone = hRedS is not null && Enumerable.Range(1, W - 1).All(i => Lu(hRedS, i) >= Lu(hRedS, i - 1) - 1e-4);
        Check(monotone, "color balance: ramp stays monotonic at full strength");
    }
}
