using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using ImgViewer.Plugins;

namespace ImgViewer.Services.Ai;

/// <summary>Statistics of a rendered (display sRGB, 0..1) image: what the assistant "sees" besides the preview.</summary>
public sealed record ImageStats
{
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Mean luminance 0..100 (Rec.709 weights on sRGB values).</summary>
    public double MeanLuma { get; init; }
    /// <summary>Luminance percentiles 1, 5, 50, 95, 99 (0..100).</summary>
    public double[] LumaPercentiles { get; init; } = Array.Empty<double>();
    /// <summary>% of pixels with a channel at the top (blown) and with all channels at the bottom (blocked).</summary>
    public double ClippedHighlightsPct { get; init; }
    public double ClippedShadowsPct { get; init; }
    /// <summary>Mean HSV saturation 0..100.</summary>
    public double MeanSaturation { get; init; }
    /// <summary>Mean R, G, B 0..100.</summary>
    public double[] MeanRgb { get; init; } = Array.Empty<double>();
    /// <summary>"neutral", "warm (orange)", "cool (blue)", "green", "magenta".</summary>
    public string ColorCast { get; init; } = "neutral";
    public bool Monochrome { get; init; }
    /// <summary>Mean luminance 0..100 of the 3×3 zones, row by row from the top left.</summary>
    public double[] Zones { get; init; } = Array.Empty<double>();
    /// <summary>Spread of luminance (p95 − p5, 0..100): low = flat, high = contrasty.</summary>
    public double TonalRange => LumaPercentiles.Length == 5 ? LumaPercentiles[3] - LumaPercentiles[1] : 0;

    public JsonObject ToJson()
    {
        static JsonArray Arr(double[] a) => new(a.Select(v => (JsonNode)JsonValue.Create(Math.Round(v, 1))!).ToArray());
        return new JsonObject
        {
            ["width"] = Width,
            ["height"] = Height,
            ["mean_luminance"] = Math.Round(MeanLuma, 1),
            ["luminance_percentiles_1_5_50_95_99"] = Arr(LumaPercentiles),
            ["tonal_range_p5_p95"] = Math.Round(TonalRange, 1),
            ["clipped_highlights_pct"] = Math.Round(ClippedHighlightsPct, 2),
            ["clipped_shadows_pct"] = Math.Round(ClippedShadowsPct, 2),
            ["mean_saturation"] = Math.Round(MeanSaturation, 1),
            ["mean_rgb"] = Arr(MeanRgb),
            ["color_cast"] = ColorCast,
            ["monochrome"] = Monochrome,
            ["zones_3x3_luminance"] = Arr(Zones),
            ["scale"] = "all values 0..100"
        };
    }

    /// <summary>One line for the transcript.</summary>
    public string Summary() => string.Format(CultureInfo.InvariantCulture,
        "luma {0:0} (p5 {1:0} / p95 {2:0}), clip {3:0.0}%/{4:0.0}%, sat {5:0}, {6}",
        MeanLuma, LumaPercentiles.ElementAtOrDefault(1), LumaPercentiles.ElementAtOrDefault(3),
        ClippedHighlightsPct, ClippedShadowsPct, MeanSaturation, Monochrome ? "monochrome" : ColorCast);
}

public static class ImageProbe
{
    /// <summary>Statistics of a display-referred sRGB image (values 0..1; others are clamped).</summary>
    public static ImageStats Analyze(PluginImage img)
    {
        int w = img.Width, h = img.Height, n = img.Length;
        var hist = new int[1001];
        double sumL = 0, sumS = 0, sr = 0, sg = 0, sb = 0;
        long hi = 0, lo = 0;
        var zoneSum = new double[9];
        var zoneCnt = new int[9];
        double chromaSum = 0;
        for (int y = 0; y < h; y++)
        {
            int zy = Math.Min(2, y * 3 / h);
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                double r = Math.Clamp(img.R[i], 0f, 1f), g = Math.Clamp(img.G[i], 0f, 1f), b = Math.Clamp(img.B[i], 0f, 1f);
                double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                hist[(int)Math.Round(l * 1000)]++;
                sumL += l;
                sr += r;
                sg += g;
                sb += b;
                double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                sumS += mx <= 1e-6 ? 0 : (mx - mn) / mx;
                chromaSum += mx - mn;
                if (mx >= 0.995) hi++;
                if (mx <= 0.005) lo++;
                int z = zy * 3 + Math.Min(2, x * 3 / w);
                zoneSum[z] += l;
                zoneCnt[z]++;
            }
        }
        double[] pct = { 0.01, 0.05, 0.50, 0.95, 0.99 };
        var perc = pct.Select(p => Percentile(hist, n, p) * 100).ToArray();
        double mr = sr / n, mg = sg / n, mb = sb / n;
        bool mono = chromaSum / n < 0.01;
        return new ImageStats
        {
            Width = w,
            Height = h,
            MeanLuma = sumL / n * 100,
            LumaPercentiles = perc,
            ClippedHighlightsPct = hi * 100.0 / n,
            ClippedShadowsPct = lo * 100.0 / n,
            MeanSaturation = sumS / n * 100,
            MeanRgb = new[] { mr * 100, mg * 100, mb * 100 },
            ColorCast = mono ? "neutral" : Cast(mr, mg, mb),
            Monochrome = mono,
            Zones = Enumerable.Range(0, 9).Select(z => zoneCnt[z] == 0 ? 0 : zoneSum[z] / zoneCnt[z] * 100).ToArray()
        };
    }

    private static double Percentile(int[] hist, int n, double p)
    {
        long target = (long)Math.Ceiling(p * n);
        long acc = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            acc += hist[i];
            if (acc >= target) return i / 1000.0;
        }
        return 1.0;
    }

    /// <summary>Dominant tint of the mean colour: warm/cool along blue-yellow, green/magenta along the green axis.</summary>
    public static string Cast(double r, double g, double b)
    {
        double m = (r + g + b) / 3;
        if (m <= 1e-6) return "neutral";
        double warm = (r - b) / m;          // + warm, − cool
        double green = (g - (r + b) / 2) / m; // + green, − magenta
        const double t = 0.06;
        if (Math.Abs(warm) < t && Math.Abs(green) < t) return "neutral";
        if (Math.Abs(warm) >= Math.Abs(green)) return warm > 0 ? "warm (orange)" : "cool (blue)";
        return green > 0 ? "green" : "magenta";
    }
}
