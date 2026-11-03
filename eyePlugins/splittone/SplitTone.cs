using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.SplitTone;

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
//  Color grade: two colors + curve, preset touching core sliders
// ---------------------------------------------------------------------

public sealed class ColorGradePlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.grade",
        Name = "Split-tone grade",
        Version = "2.0",
        Author = "PhotographerEye",
        Description = "Tints shadows and highlights with two colors and applies a tone curve."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddFilter(new FilterDescriptor
        {
            Id = "grade",
            Label = "Split-tone grade",
            Group = "Color",
            Stage = FilterStage.Display,
            Params = new[]
            {
                ParamDef.Slider("amount", "Amount", 0, 100, 0),
                ParamDef.Color("shadows", "Shadows", "#2E5C8A"),
                ParamDef.Color("highlights", "Highlights", "#F2A65A"),
                ParamDef.Slider("balance", "Balance", -100, 100, 0),
                ParamDef.Curve("curve", "Tone curve")
            },
            IsIdentity = p => p.Get("amount") <= 0 && p.GetCurve("curve").IsIdentity,
            Render = Render
        });
        host.AddPreset(new PresetDescriptor
        {
            Id = "cinematic",
            Name = "Cinematic teal/orange",
            Values = new Dictionary<string, double> { ["grade/amount"] = 55, ["core.Contrast"] = 12, ["core.Saturation"] = -8 },
            Text = new Dictionary<string, string> { ["grade/curve"] = "0,0.03;0.25,0.2;0.75,0.8;1,0.97" }
        });
    }

    private static void Render(PluginImage img, FilterParams p, RenderContext ctx)
    {
        float amount = (float)(p.Get("amount") / 100.0);
        p.GetColor("shadows", out var sr, out var sg, out var sb);
        p.GetColor("highlights", out var hr, out var hg, out var hb);
        float bal = (float)(p.Get("balance") / 100.0);
        var curve = p.GetCurve("curve");
        float[] lut = curve.IsIdentity ? null : curve.ToLut(4096);
        // Tints as offsets from their own luminance so the grade does not change brightness.
        float sl = U.Luma(sr, sg, sb), hl = U.Luma(hr, hg, hb);
        float sdr = sr - sl, sdg = sg - sl, sdb = sb - sl;
        float hdr = hr - hl, hdg = hg - hl, hdb = hb - hl;
        float pivot = 0.5f + 0.3f * bal;
        Parallel.For(0, img.Height, y =>
        {
            int row = y * img.Width;
            for (int i = row; i < row + img.Width; i++)
            {
                float r = img.R[i], g = img.G[i], b = img.B[i];
                if (lut != null)
                {
                    r = lut[(int)(U.Clamp01(r) * 4095f + 0.5f)];
                    g = lut[(int)(U.Clamp01(g) * 4095f + 0.5f)];
                    b = lut[(int)(U.Clamp01(b) * 4095f + 0.5f)];
                }
                if (amount > 0)
                {
                    float l = U.Luma(r, g, b);
                    float hw = U.Smooth((l - pivot + 0.35f) / 0.7f);
                    float sw = 1f - hw;
                    float a = amount * 0.6f;
                    r += a * (sw * sdr + hw * hdr);
                    g += a * (sw * sdg + hw * hdg);
                    b += a * (sw * sdb + hw * hdb);
                }
                img.R[i] = U.Clamp01(r);
                img.G[i] = U.Clamp01(g);
                img.B[i] = U.Clamp01(b);
            }
        });
    }
}
