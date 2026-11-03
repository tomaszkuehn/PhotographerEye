using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.BwRed;

internal static class U
{
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    public static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
    public static float Smooth(float t) { t = Clamp01(t); return t * t * (3 - 2 * t); }

    public static void Rgb2Hsv(float r, float g, float b, out float h, out float s, out float v)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        v = max;
        float d = max - min;
        s = max > 1e-6f ? d / max : 0f;
        if (d < 1e-6f) { h = 0; return; }
        if (max == r) h = 60f * (((g - b) / d) % 6f);
        else if (max == g) h = 60f * ((b - r) / d + 2f);
        else h = 60f * ((r - g) / d + 4f);
        if (h < 0) h += 360f;
    }

    public static float HueDistance(float a, float b)
    {
        float d = Math.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }
}

// ---------------------------------------------------------------------
//  Black & white with red mix: slider + choice + toggle, preset
// ---------------------------------------------------------------------

public sealed class BwRedPlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.bwred",
        Name = "B&W red mix",
        Version = "2.0",
        Author = "PhotographerEye",
        Description = "Monochrome conversion with a red channel mix (red filter look), optional color key."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddFilter(new FilterDescriptor
        {
            Id = "bwred",
            Label = "B&W red mix",
            Group = "Black & white",
            Stage = FilterStage.Display,
            Params = new[]
            {
                ParamDef.Slider("mix", "Red mix", 0, 100, 0),
                ParamDef.Choice("keep", "Keep color", "none",
                    new ParamChoice("none", "None"),
                    new ParamChoice("red", "Reds"),
                    new ParamChoice("yellow", "Yellows"),
                    new ParamChoice("blue", "Blues")),
                ParamDef.Toggle("strong", "Strong contrast")
            },
            IsIdentity = p => p.Get("mix") <= 0,
            Render = Render
        });
        host.AddPreset(new PresetDescriptor
        {
            Id = "red-filter",
            Name = "Red filter B&W",
            Values = new Dictionary<string, double> { ["bwred/mix"] = 80, ["bwred/strong"] = 1, ["core.Contrast"] = 10 }
        });
    }

    private static void Render(PluginImage img, FilterParams p, RenderContext ctx)
    {
        float mix = (float)(p.Get("mix") / 100.0);
        bool strong = p.GetBool("strong");
        string keep = p.GetText("keep");
        float keepHue = keep == "red" ? 0f : keep == "yellow" ? 55f : keep == "blue" ? 215f : -1f;
        Parallel.For(0, img.Height, y =>
        {
            int row = y * img.Width;
            for (int i = row; i < row + img.Width; i++)
            {
                float r = img.R[i], g = img.G[i], b = img.B[i];
                float l = U.Luma(r, g, b);
                float m = l + (r - l) * mix;
                if (strong) m = U.Clamp01(0.5f + (m - 0.5f) * 1.35f);
                float k = 0f;
                if (keepHue >= 0)
                {
                    U.Rgb2Hsv(r, g, b, out var h, out var s, out _);
                    k = (1f - U.Smooth(U.HueDistance(h, keepHue) / 30f)) * U.Smooth(s * 3f);
                }
                img.R[i] = m + k * (r - m);
                img.G[i] = m + k * (g - m);
                img.B[i] = m + k * (b - m);
            }
        });
    }
}
