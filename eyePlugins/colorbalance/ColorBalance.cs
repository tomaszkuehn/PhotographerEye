using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.ColorBalance;

/// <summary>
/// Color balance: Cyan–Red, Magenta–Green and Yellow–Blue shifts separately for shadows, midtones and highlights,
/// optionally preserving luminosity. One panel section with three sub-sections (one filter per tonal range).
/// </summary>
public sealed class ColorBalancePlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "photographereye.colorbalance",
        Name = "Color balance",
        Version = "1.0",
        Author = "PhotographerEye",
        Description = "Cyan–Red, Magenta–Green and Yellow–Blue balance for shadows, midtones and highlights, with luminosity preservation."
    };

    public void Initialize(IPluginHost host)
    {
        int order = 0;
        foreach (var range in new[] { ColorBalance.Range.Shadows, ColorBalance.Range.Midtones, ColorBalance.Range.Highlights })
        {
            var r = range;
            host.AddFilter(new FilterDescriptor
            {
                Id = ColorBalance.FilterId(r),
                Label = ColorBalance.Label(r),
                Description = "Color balance of the " + ColorBalance.Label(r).ToLowerInvariant(),
                Group = "Color",
                Stage = FilterStage.Display,
                Order = order++,
                SupportsMask = true,
                Params = ColorBalance.Params(),
                IsIdentity = ColorBalance.IsIdentity,
                Render = (img, p, ctx) => ColorBalance.Apply(img, r, p.Get("cr"), p.Get("mg"), p.Get("yb"), p.GetBool("lum"))
            });
        }

        //                                       shadows        midtones       highlights
        //                                       cr   mg   yb    cr   mg   yb    cr   mg   yb
        Preset(host, "warm", "Warm",             4,   0,  -6,   10,   0, -10,    6,   0, -12);
        Preset(host, "cool", "Cool",            -6,   0,   8,   -8,   0,  10,   -4,   0,   6);
        Preset(host, "teal-orange", "Teal & orange", -14, 0, 16,  4,   0,  -4,   12,   0, -16);
        Preset(host, "faded-film", "Faded film", 0,  -4,   8,    6,   4,  -4,    0,  -4,  -8);
    }

    private static void Preset(IPluginHost host, string id, string name,
        double sCr, double sMg, double sYb, double mCr, double mMg, double mYb, double hCr, double hMg, double hYb)
    {
        host.AddPreset(new PresetDescriptor
        {
            Id = id,
            Name = name,
            Group = "Color balance",
            Values = new Dictionary<string, double>
            {
                ["shadows/cr"] = sCr, ["shadows/mg"] = sMg, ["shadows/yb"] = sYb,
                ["midtones/cr"] = mCr, ["midtones/mg"] = mMg, ["midtones/yb"] = mYb,
                ["highlights/cr"] = hCr, ["highlights/mg"] = hMg, ["highlights/yb"] = hYb
            }
        });
    }
}

public static class ColorBalance
{
    public enum Range { Shadows, Midtones, Highlights }

    /// <summary>Largest channel offset (sRGB encoded, 0..1) at a slider value of ±100 and full tonal weight.</summary>
    public const float MaxShift = 0.25f;

    public static string FilterId(Range r) => r == Range.Shadows ? "shadows" : r == Range.Midtones ? "midtones" : "highlights";
    public static string Label(Range r) => r == Range.Shadows ? "Shadows" : r == Range.Midtones ? "Midtones" : "Highlights";

    public static IReadOnlyList<ParamDef> Params() => new[]
    {
        Tip(ParamDef.Slider("cr", "Cyan ↔ Red", -100, 100, 0), "− adds cyan, + adds red"),
        Tip(ParamDef.Slider("mg", "Magenta ↔ Green", -100, 100, 0), "− adds magenta, + adds green"),
        Tip(ParamDef.Slider("yb", "Yellow ↔ Blue", -100, 100, 0), "− adds yellow, + adds blue"),
        Tip(ParamDef.Toggle("lum", "Preserve luminosity", true), "Keeps the brightness of every pixel, only the color changes")
    };

    private static ParamDef Tip(ParamDef d, string tip) { d.Tooltip = tip; return d; }

    public static bool IsIdentity(FilterParams p) =>
        Math.Abs(p.Get("cr")) < 0.5 && Math.Abs(p.Get("mg")) < 0.5 && Math.Abs(p.Get("yb")) < 0.5;

    /// <summary>
    /// Tonal weight of a range at luma <paramref name="l"/> (sRGB encoded). The three weights sum to 1:
    /// shadows fade out by 0.55, highlights fade in from 0.45, midtones take the rest (0 at pure black and white).
    /// </summary>
    public static float Weight(Range r, float l)
    {
        float ws = 1f - SmoothStep(0f, 0.55f, l);
        float wh = SmoothStep(0.45f, 1f, l);
        return r == Range.Shadows ? ws : r == Range.Highlights ? wh : Math.Max(0f, 1f - ws - wh);
    }

    /// <summary>Applies the balance of one tonal range in place (sRGB-encoded planes). Slider values are −100..100.</summary>
    public static void Apply(PluginImage img, Range range, double cr, double mg, double yb, bool preserveLum)
    {
        float dr = (float)(Clamp(cr, -100, 100) / 100.0) * MaxShift;
        float dg = (float)(Clamp(mg, -100, 100) / 100.0) * MaxShift;
        float db = (float)(Clamp(yb, -100, 100) / 100.0) * MaxShift;
        if (dr == 0f && dg == 0f && db == 0f) return;

        // Weight LUT over luma (fast and identical for preview and export).
        const int N = 1024;
        var wl = new float[N + 1];
        for (int i = 0; i <= N; i++) wl[i] = Weight(range, i / (float)N);

        int w = img.Width;
        Parallel.For(0, img.Height, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++)
            {
                float r = img.R[i], g = img.G[i], b = img.B[i];
                float l = Luma(r, g, b);
                float wt = wl[(int)(Clamp01(l) * N + 0.5f)];
                if (wt <= 1e-4f) continue;
                float nr = r + dr * wt, ng = g + dg * wt, nb = b + db * wt;
                if (preserveLum)
                {
                    // Equal shift on all channels restores the luma without changing the new chroma.
                    float d = l - Luma(nr, ng, nb);
                    nr += d; ng += d; nb += d;
                    // Into gamut along the line to gray (keeps luma, never flips hue).
                    float mx = Math.Max(nr, Math.Max(ng, nb)), mn = Math.Min(nr, Math.Min(ng, nb));
                    float t = 1f;
                    if (mx > 1f && mx - l > 1e-6f) t = Math.Min(t, (1f - l) / (mx - l));
                    if (mn < 0f && l - mn > 1e-6f) t = Math.Min(t, l / (l - mn));
                    if (t < 1f) { nr = l + (nr - l) * t; ng = l + (ng - l) * t; nb = l + (nb - l) * t; }
                }
                img.R[i] = Clamp01(nr); img.G[i] = Clamp01(ng); img.B[i] = Clamp01(nb);
            }
        });
    }

    private static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    private static float SmoothStep(float a, float b, float x)
    {
        float t = Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }
}
