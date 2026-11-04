using System;
using System.Linq;
using System.Threading.Tasks;
using ImgViewer.Plugins;

namespace ImgViewer.Plugins.BwProfiles;

// ---------------------------------------------------------------------
//  Black & white conversion shared by the B&W plugins: a profile (colour mix over 7 hue ranges, contrast,
//  brightness, blacks, film grain, split toning) and the engine that applies it. Linked into eyePlugins/bwclassic.
// ---------------------------------------------------------------------

/// <summary>One black & white look. Values in slider units (mostly -100..100).</summary>
public sealed class BwProfile
{
    public const int Hues = 7;
    /// <summary>Hue centres (degrees) of <see cref="Mix"/>: reds, oranges, yellows, greens, cyans, blues, magentas.</summary>
    public static readonly float[] HueCentres = { 0f, 30f, 60f, 120f, 180f, 240f, 300f };
    public static readonly string[] HueNames = { "Reds", "Oranges", "Yellows", "Greens", "Cyans", "Blues", "Magentas" };
    public static readonly string[] HueIds = { "red", "orange", "yellow", "green", "cyan", "blue", "magenta" };

    public string Id = "";
    public string Name = "";
    /// <summary>Lightness of each colour range in the conversion, -100 (darker) .. 100 (lighter).</summary>
    public double[] Mix = new double[Hues];
    public double Contrast, Brightness, Blacks, Grain, GrainSize = 30, Toning, Balance;
    public string ShadowTint = "#808080", HighlightTint = "#808080";

    public BwProfile Clone()
    {
        var c = (BwProfile)MemberwiseClone();
        c.Mix = (double[])Mix.Clone();
        return c;
    }

    public static BwProfile Make(string id, string name, double[] mix, double contrast = 0, double brightness = 0, double blacks = 0,
        double grain = 0, double grainSize = 30, string shadow = "#808080", string highlight = "#808080", double toning = 0, double balance = 0)
        => new BwProfile
        {
            Id = id, Name = name, Mix = mix, Contrast = contrast, Brightness = brightness, Blacks = blacks, Grain = grain, GrainSize = grainSize,
            ShadowTint = shadow, HighlightTint = highlight, Toning = toning, Balance = balance
        };
}

/// <summary>The conversion itself (display-referred sRGB 0..1 in, monochrome or toned out).</summary>
public static class BwEngine
{
    private static readonly float[] Decode = BuildDecode();

    private static float[] BuildDecode()
    {
        var t = new float[4097];
        for (int i = 0; i <= 4096; i++)
        {
            double c = i / 4096.0;
            t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return t;
    }

    private static float ToLinear(float c)
    {
        if (c <= 0f) return c / 12.92f;
        if (c >= 1f) return (float)Math.Pow((c + 0.055) / 1.055, 2.4);
        float f = c * 4096f;
        int i = (int)f;
        if (i >= 4096) return Decode[4096];
        float a = f - i;
        return Decode[i] + (Decode[i + 1] - Decode[i]) * a;
    }

    private static float ToSrgb(float l)
    {
        if (l <= 0f) return 0f;
        return l <= 0.0031308f ? 12.92f * l : (float)(1.055 * Math.Pow(l, 1 / 2.4) - 0.055);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>Mix value (-1..1) at a hue: piecewise-linear between the hue centres, around the circle.</summary>
    public static float MixAt(float[] mix, float hue)
    {
        var c = BwProfile.HueCentres;
        int n = c.Length;
        for (int i = 0; i < n; i++)
        {
            float a = c[i], b = i + 1 < n ? c[i + 1] : 360f;
            if (hue >= a && hue < b)
            {
                float t = (hue - a) / (b - a);
                return mix[i] + (mix[(i + 1) % n] - mix[i]) * t;
            }
        }
        return mix[0];
    }

    /// <summary>Hash noise in -1..1 (triangular) on an integer lattice.</summary>
    public static float Hash(int x, int y)
    {
        uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ 0x5BD1E995u;
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
        return (h & 0xFFFF) / 65535f + (h >> 16) / 65535f - 1f;
    }

    /// <summary>Value noise with cells of <paramref name="cell"/> full-resolution px, smoothly interpolated.</summary>
    private static float Grain(double fx, double fy, double cell)
    {
        double gx = fx / cell, gy = fy / cell;
        int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
        float tx = (float)(gx - ix), ty = (float)(gy - iy);
        tx = tx * tx * (3 - 2 * tx);
        ty = ty * ty * (3 - 2 * ty);
        float a = Hash(ix, iy), b = Hash(ix + 1, iy), c = Hash(ix, iy + 1), d = Hash(ix + 1, iy + 1);
        return (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
    }

    /// <summary>Converts <paramref name="img"/> in place. <paramref name="amount"/> 0..1 blends with the colour original.</summary>
    public static void Apply(PluginImage img, BwProfile p, double amount, double scale, System.Threading.CancellationToken cancel = default)
    {
        amount = Clamp(amount, 0, 1);
        if (amount <= 0) return;
        if (scale <= 0 || double.IsNaN(scale)) scale = 1;
        int w = img.Width, h = img.Height;
        var R = img.R; var G = img.G; var B = img.B;

        var mix = p.Mix.Select(m => (float)(Clamp(m, -100, 100) / 100.0)).ToArray();
        bool anyMix = mix.Any(m => Math.Abs(m) > 1e-4f);
        // Contrast: rational S-curve around mid grey with fixed end points; slope at the middle 1+k.
        double c = Clamp(p.Contrast, -100, 100) / 100.0;
        float k = (float)(c >= 0 ? 1.6 * c : 0.65 * c);
        float gamma = (float)Math.Pow(2.0, -Clamp(p.Brightness, -100, 100) / 100.0 * 1.1);
        double bl = Clamp(p.Blacks, -100, 100) / 100.0;
        float lift = (float)(bl > 0 ? 0.25 * bl : 0), crush = (float)(bl < 0 ? -0.15 * bl : 0);

        // Grain: deterministic on full-resolution coordinates; a downscaled preview averages it (weaker), like the
        // photo seen at that size.
        double cell = 1.0 + 3.0 * Clamp(p.GrainSize, 0, 100) / 100.0;
        double att = Math.Min(1.0, cell * scale);
        float grainAmp = (float)(Clamp(p.Grain, 0, 100) / 100.0 * 0.16 * att);

        FilterParams.ParseColor(p.ShadowTint, out var sr, out var sg, out var sb);
        FilterParams.ParseColor(p.HighlightTint, out var hr, out var hg, out var hb);
        float sl = 0.2126f * sr + 0.7152f * sg + 0.0722f * sb, hl = 0.2126f * hr + 0.7152f * hg + 0.0722f * hb;
        float tone = (float)(Clamp(p.Toning, 0, 100) / 100.0 * 1.6);
        float dsr = (sr - sl) * tone, dsg = (sg - sl) * tone, dsb = (sb - sl) * tone;
        float dhr = (hr - hl) * tone, dhg = (hg - hl) * tone, dhb = (hb - hl) * tone;
        bool toned = tone > 0 && (Math.Abs(dsr) + Math.Abs(dsg) + Math.Abs(dsb) + Math.Abs(dhr) + Math.Abs(dhg) + Math.Abs(dhb)) > 1e-4f;
        // Balance moves the split between shadow and highlight tint (-1: more highlight tint, +1: more shadow tint).
        float split = (float)(0.5 + 0.35 * Clamp(p.Balance, -100, 100) / 100.0);
        float a = (float)amount;

        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            int o = y * w;
            double fy = (y + 0.5) / scale;
            for (int x = 0; x < w; x++, o++)
            {
                float r0 = R[o], g0 = G[o], b0 = B[o];
                float lr = ToLinear(r0), lg = ToLinear(g0), lb = ToLinear(b0);
                float lum = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
                if (anyMix)
                {
                    float rc = Clamp01(r0), gc = Clamp01(g0), bc = Clamp01(b0);
                    float max = Math.Max(rc, Math.Max(gc, bc)), min = Math.Min(rc, Math.Min(gc, bc)), d = max - min;
                    if (d > 1e-4f)
                    {
                        float hue;
                        if (max == rc) hue = 60f * (((gc - bc) / d) % 6f);
                        else if (max == gc) hue = 60f * ((bc - rc) / d + 2f);
                        else hue = 60f * ((rc - gc) / d + 4f);
                        if (hue < 0) hue += 360f;
                        // Chroma-weighted exposure of that colour range: up to about +-1.6 EV for a pure colour.
                        float adj = MixAt(mix, hue) * d * 1.6f;
                        lum *= (float)Math.Pow(2.0, adj);
                    }
                }
                float v = ToSrgb(lum);
                if (crush > 0) v = (v - crush) / (1f - crush);
                v = Clamp01(v);
                if (gamma != 1f && v > 0f) v = (float)Math.Pow(v, gamma);
                if (k != 0f)
                {
                    float t = v - 0.5f;
                    v = 0.5f + t * (1f + k) / (1f + k * 2f * Math.Abs(t));
                }
                if (lift > 0) v = lift + v * (1f - lift);
                if (grainAmp > 0)
                {
                    double fx = (x + 0.5) / scale;
                    // Film-like: most grain in the midtones, none in pure black and white.
                    float wl = v <= 0f || v >= 1f ? 0f : (float)Math.Sqrt(4f * v * (1f - v));
                    v = Clamp01(v + grainAmp * wl * Grain(fx, fy, cell));
                }
                float or = v, og = v, ob = v;
                if (toned)
                {
                    float ws, wh;
                    if (v < split) { ws = 1f - v / split; wh = 0f; }
                    else { ws = 0f; wh = (v - split) / (1f - split); }
                    ws = (float)Math.Sqrt(ws) * Math.Min(1f, v * 8f);
                    wh = (float)Math.Sqrt(wh) * Math.Min(1f, (1f - v) * 8f);
                    // Mid-tones get a little of both, so the toning flows from one colour to the other.
                    float mid = 0.35f * (float)Math.Sqrt(Math.Max(0f, 4f * v * (1f - v)));
                    float s = ws + mid * 0.5f, hh = wh + mid * 0.5f;
                    or = Clamp01(v + dsr * s + dhr * hh);
                    og = Clamp01(v + dsg * s + dhg * hh);
                    ob = Clamp01(v + dsb * s + dhb * hh);
                }
                if (a < 1f)
                {
                    or = r0 + (or - r0) * a;
                    og = g0 + (og - g0) * a;
                    ob = b0 + (ob - b0) * a;
                }
                R[o] = or; G[o] = og; B[o] = ob;
            }
        });
    }
}
