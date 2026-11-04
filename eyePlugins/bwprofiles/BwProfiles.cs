using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.BwProfiles;

// ---------------------------------------------------------------------
//  B&W profiles: a choice of black & white looks for fine-art photography (colour filters, infrared, high / low key,
//  film grain, toning) with fine-tuning sliders on top, plus up to 10 profiles of the user's own, saved from the
//  current settings ("Save as my profile…") into PluginData\<id>\profiles.txt.
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

/// <summary>Built-in profiles and the user's own (slots 1..10, kept in a small text file).</summary>
public static class BwProfileStore
{
    public const int MaxCustom = 10;

    //                                                                  R    O    Y    G    C    B    M
    public static readonly BwProfile[] BuiltIn =
    {
        BwProfile.Make("neutral", "Neutral", new double[] { 0, 0, 0, 0, 0, 0, 0 }),
        BwProfile.Make("red", "Red filter — dramatic sky", new double[] { 60, 45, 20, -25, -55, -75, 20 }, contrast: 25),
        BwProfile.Make("orange", "Orange filter — portrait", new double[] { 35, 45, 25, -10, -25, -35, 10 }, contrast: 10, brightness: 5),
        BwProfile.Make("green", "Green filter — foliage", new double[] { -25, -10, 20, 50, 20, -10, -20 }, contrast: 10),
        BwProfile.Make("blue", "Blue filter — mist & mood", new double[] { -40, -30, -20, -5, 30, 50, 10 }, contrast: -10),
        BwProfile.Make("infrared", "Infrared", new double[] { 40, 30, 60, 100, -40, -90, 10 }, contrast: 35, brightness: 5, grain: 20, grainSize: 45),
        BwProfile.Make("highkey", "High key", new double[] { 20, 25, 25, 10, 0, -10, 10 }, contrast: -15, brightness: 35, blacks: 10),
        BwProfile.Make("lowkey", "Low key — noir", new double[] { 10, 10, 5, -10, -20, -30, 0 }, contrast: 55, brightness: -25, blacks: -15, grain: 15),
        BwProfile.Make("trix", "Pushed film — grainy", new double[] { 5, 10, 10, 0, -10, -15, 0 }, contrast: 40, grain: 45, grainSize: 55),
        BwProfile.Make("panf", "Fine grain — smooth", new double[] { 5, 5, 5, 0, -5, -5, 0 }, contrast: 15, grain: 8, grainSize: 10),
        BwProfile.Make("matte", "Matte fade", new double[] { 10, 10, 10, 0, -5, -10, 0 }, contrast: -20, brightness: 5, blacks: 25),
        BwProfile.Make("selenium", "Selenium tone", new double[] { 15, 15, 10, -5, -15, -20, 5 }, contrast: 20, shadow: "#4C3F66", highlight: "#808080", toning: 55, balance: -30),
        BwProfile.Make("sepia", "Sepia", new double[] { 20, 20, 15, 0, -10, -15, 5 }, contrast: 5, shadow: "#6B4A2E", highlight: "#C99A5B", toning: 55),
        BwProfile.Make("platinum", "Platinum / palladium", new double[] { 15, 15, 10, 0, -10, -15, 5 }, contrast: -10, blacks: 8, shadow: "#5C4635", highlight: "#D9CCB3", toning: 40, balance: 10),
        BwProfile.Make("cyanotype", "Cyanotype", new double[] { -10, -5, 0, 10, 20, 20, 0 }, contrast: 5, shadow: "#1C3A66", highlight: "#A8C4DD", toning: 80, balance: -10),
    };

    private static volatile BwProfile[] _custom = new BwProfile[MaxCustom];

    /// <summary>File the custom profiles are kept in (null: not stored, e.g. in tests).</summary>
    public static string FilePath { get; set; }

    public static string CustomId(int slot) => "custom" + slot.ToString(CultureInfo.InvariantCulture);

    /// <summary>Slot (1..10) of a custom profile id, 0 for anything else.</summary>
    public static int SlotOf(string id)
    {
        if (id == null || !id.StartsWith("custom", StringComparison.Ordinal)) return 0;
        return int.TryParse(id.Substring(6), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= MaxCustom ? n : 0;
    }

    public static BwProfile Custom(int slot) => slot >= 1 && slot <= MaxCustom ? _custom[slot - 1] : null;

    /// <summary>Profile by id; unknown ids (a deleted custom profile) give null.</summary>
    public static BwProfile Find(string id)
    {
        int slot = SlotOf(id);
        if (slot > 0) return Custom(slot);
        foreach (var p in BuiltIn) if (p.Id == id) return p;
        return null;
    }

    public static void SetCustom(int slot, BwProfile p)
    {
        if (slot < 1 || slot > MaxCustom) throw new ArgumentOutOfRangeException(nameof(slot));
        var next = (BwProfile[])_custom.Clone();
        if (p != null)
        {
            p = p.Clone();
            p.Id = CustomId(slot);
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "My profile " + slot.ToString(CultureInfo.InvariantCulture);
        }
        next[slot - 1] = p;
        _custom = next;
    }

    /// <summary>Choice list of the profile parameter: built-in profiles, then the defined custom ones.</summary>
    public static ParamChoice[] Choices()
    {
        var list = BuiltIn.Select(p => new ParamChoice(p.Id, p.Name)).ToList();
        foreach (var p in _custom)
            if (p != null) list.Add(new ParamChoice(p.Id, "★ " + p.Name));
        return list.ToArray();
    }

    // ---- file: "[slot]" sections with key=value lines (invariant culture) ----

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("# PhotographerEye B&W profiles (custom slots 1..10)\n");
        for (int i = 0; i < MaxCustom; i++)
        {
            var p = _custom[i];
            if (p == null) continue;
            sb.Append('[').Append(i + 1).Append("]\n");
            sb.Append("name=").Append(p.Name.Replace("\r", " ").Replace("\n", " ")).Append('\n');
            sb.Append("mix=").Append(string.Join(",", p.Mix.Select(N))).Append('\n');
            sb.Append("contrast=").Append(N(p.Contrast)).Append('\n');
            sb.Append("brightness=").Append(N(p.Brightness)).Append('\n');
            sb.Append("blacks=").Append(N(p.Blacks)).Append('\n');
            sb.Append("grain=").Append(N(p.Grain)).Append('\n');
            sb.Append("grainsize=").Append(N(p.GrainSize)).Append('\n');
            sb.Append("shadowtint=").Append(p.ShadowTint).Append('\n');
            sb.Append("highlighttint=").Append(p.HighlightTint).Append('\n');
            sb.Append("toning=").Append(N(p.Toning)).Append('\n');
            sb.Append("balance=").Append(N(p.Balance)).Append('\n');
        }
        return sb.ToString();
    }

    public static void Deserialize(string text)
    {
        var next = new BwProfile[MaxCustom];
        BwProfile cur = null;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[' && line.EndsWith("]"))
            {
                cur = null;
                if (int.TryParse(line.Substring(1, line.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var slot) && slot >= 1 && slot <= MaxCustom)
                    next[slot - 1] = cur = new BwProfile { Id = CustomId(slot), Name = "My profile " + slot.ToString(CultureInfo.InvariantCulture) };
                continue;
            }
            int eq = line.IndexOf('=');
            if (cur == null || eq <= 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant(), val = line.Substring(eq + 1).Trim();
            double D(double lo, double hi, double def) =>
                double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d)
                    ? Math.Max(lo, Math.Min(hi, d)) : def;
            switch (key)
            {
                case "name": if (val.Length > 0) cur.Name = val; break;
                case "mix":
                    var parts = val.Split(',');
                    for (int h = 0; h < BwProfile.Hues && h < parts.Length; h++)
                        if (double.TryParse(parts[h].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && !double.IsNaN(m))
                            cur.Mix[h] = Math.Max(-100, Math.Min(100, m));
                    break;
                case "contrast": cur.Contrast = D(-100, 100, 0); break;
                case "brightness": cur.Brightness = D(-100, 100, 0); break;
                case "blacks": cur.Blacks = D(-100, 100, 0); break;
                case "grain": cur.Grain = D(0, 100, 0); break;
                case "grainsize": cur.GrainSize = D(0, 100, 30); break;
                case "shadowtint": cur.ShadowTint = IsHex(val) ? val : "#808080"; break;
                case "highlighttint": cur.HighlightTint = IsHex(val) ? val : "#808080"; break;
                case "toning": cur.Toning = D(0, 100, 0); break;
                case "balance": cur.Balance = D(-100, 100, 0); break;
            }
        }
        _custom = next;
    }

    public static bool IsHex(string s)
    {
        if (s == null || s.Length != 7 || s[0] != '#') return false;
        for (int i = 1; i < 7; i++) if (!Uri.IsHexDigit(s[i])) return false;
        return true;
    }

    public static void Load()
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;
        Deserialize(File.ReadAllText(FilePath, Encoding.UTF8));
    }

    public static void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, Serialize(), new UTF8Encoding(false));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }
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

public sealed class BwProfilesPlugin : IEyePlugin
{
    public const string FilterId = "bw";

    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "photographereye.bwprofiles",
        Name = "B&W profiles",
        Version = "1.0",
        Author = "PhotographerEye",
        Description = "Black & white profiles for fine-art photography (colour filters, infrared, high/low key, film grain, toning) plus up to 10 of your own."
    };

    private ParamDef _profileDef;

    private static ParamDef Tip(ParamDef d, string tip) { d.Tooltip = tip; return d; }

    /// <summary>Panel parameters: the profile, its strength, then fine-tuning on top of the profile.</summary>
    public static ParamDef[] BuildParams(out ParamDef profile)
    {
        profile = Tip(ParamDef.Choice("profile", "Profile", "neutral", BwProfileStore.Choices()),
            "Black & white look. ★ marks your own profiles (Save as my profile…)");
        var list = new List<ParamDef>
        {
            profile,
            Tip(ParamDef.Slider("amount", "Strength", 0, 100, 100), "100 = full black & white; lower values blend with the colour photo"),
            Tip(ParamDef.Slider("contrast", "Contrast", -100, 100, 0), "Added to the profile's contrast"),
            Tip(ParamDef.Slider("brightness", "Brightness", -100, 100, 0), "Added to the profile's brightness (mid-tones)"),
            Tip(ParamDef.Slider("blacks", "Blacks", -100, 100, 0), "Added to the profile: right lifts the blacks (matte), left deepens them"),
        };
        for (int i = 0; i < BwProfile.Hues; i++)
            list.Add(Tip(ParamDef.Slider("mix" + BwProfile.HueIds[i], BwProfile.HueNames[i], -100, 100, 0),
                $"How light the {BwProfile.HueNames[i].ToLowerInvariant()} of the colour photo become, added to the profile's colour mix"));
        list.Add(Tip(ParamDef.Slider("grain", "Grain", -100, 100, 0), "Added to the profile's film grain"));
        list.Add(Tip(ParamDef.Slider("grainsize", "Grain size", -100, 100, 0), "Added to the profile's grain size"));
        list.Add(Tip(ParamDef.Slider("toning", "Toning", -100, 100, 0), "Added to the profile's toning strength"));
        list.Add(Tip(ParamDef.Slider("balance", "Toning balance", -100, 100, 0), "Added to the profile's balance: right gives more of the shadow colour, left more of the highlight colour"));
        list.Add(Tip(ParamDef.Toggle("owntint", "Own toning colours"), "Use the two colours below instead of the profile's toning colours"));
        list.Add(Tip(ParamDef.Color("shadowtint", "Shadow colour", "#5A4632"), "Toning colour of the shadows (with Own toning colours)"));
        list.Add(Tip(ParamDef.Color("highlighttint", "Highlight colour", "#D2BE96"), "Toning colour of the highlights (with Own toning colours)"));
        return list.ToArray();
    }

    /// <summary>Profile with the panel's fine-tuning applied (what is rendered, and what "Save as my profile" stores).</summary>
    public static BwProfile Effective(FilterParams p)
    {
        var baseP = BwProfileStore.Find(p.GetText("profile")) ?? BwProfileStore.BuiltIn[0];
        var e = baseP.Clone();
        for (int i = 0; i < BwProfile.Hues; i++) e.Mix[i] = Clamp(e.Mix[i] + p.Get("mix" + BwProfile.HueIds[i]), -100, 100);
        e.Contrast = Clamp(e.Contrast + p.Get("contrast"), -100, 100);
        e.Brightness = Clamp(e.Brightness + p.Get("brightness"), -100, 100);
        e.Blacks = Clamp(e.Blacks + p.Get("blacks"), -100, 100);
        e.Grain = Clamp(e.Grain + p.Get("grain"), 0, 100);
        e.GrainSize = Clamp(e.GrainSize + p.Get("grainsize"), 0, 100);
        e.Toning = Clamp(e.Toning + p.Get("toning"), 0, 100);
        e.Balance = Clamp(e.Balance + p.Get("balance"), -100, 100);
        if (p.GetBool("owntint"))
        {
            e.ShadowTint = Hex(p.GetText("shadowtint"));
            e.HighlightTint = Hex(p.GetText("highlighttint"));
            // Own colours with a profile that has no toning: start from a visible strength.
            if (baseP.Toning <= 0 && p.Get("toning") == 0) e.Toning = 50;
        }
        return e;
    }

    private static string Hex(string s) => BwProfileStore.IsHex(s) ? s.ToUpperInvariant() : "#808080";
    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    public void Initialize(IPluginHost host)
    {
        try
        {
            BwProfileStore.FilePath = Path.Combine(host.DataDirectory, "profiles.txt");
            BwProfileStore.Load();
        }
        catch (Exception ex)
        {
            host.Log("B&W profiles: could not read the custom profiles: " + ex.Message);
        }
        var defs = BuildParams(out _profileDef);
        host.AddFilter(new FilterDescriptor
        {
            Id = FilterId,
            Label = "B&W profiles",
            Description = "Black & white profiles for fine-art photography, with fine-tuning and up to 10 profiles of your own",
            Group = "Black & white",
            Stage = FilterStage.Display,
            Params = defs,
            IsIdentity = p => p.Get("amount") <= 0,
            Render = (img, p, ctx) => BwEngine.Apply(img, Effective(p), p.Get("amount") / 100.0, ctx.Scale, ctx.Cancel)
        });
        host.AddCommand(new CommandDescriptor
        {
            Id = "save",
            Label = "Save the current B&W look as one of my profiles",
            ToolbarText = "Save as my profile…",
            Placement = CommandPlacement.Section,
            SectionFilter = FilterId,
            Execute = SaveCustom
        });
        host.AddCommand(new CommandDescriptor
        {
            Id = "delete",
            Label = "Delete one of my B&W profiles",
            ToolbarText = "Delete my profile…",
            Placement = CommandPlacement.Section,
            SectionFilter = FilterId,
            Execute = DeleteCustom
        });
    }

    private static int _askSerial;

    private static string SlotLabel(int slot)
    {
        var p = BwProfileStore.Custom(slot);
        return slot.ToString(CultureInfo.InvariantCulture) + " — " + (p == null ? "(empty)" : p.Name);
    }

    /// <summary>The filter's current parameters on this photo.</summary>
    private static FilterParams CurrentFilterParams(ICommandContext c)
    {
        var defs = BuildParams(out _);
        var values = new Dictionary<string, double>();
        var text = new Dictionary<string, string>();
        foreach (var d in defs)
        {
            if (d.IsText) text[d.Id] = c.GetText(FilterId, d.Id);
            else values[d.Id] = c.GetValue(FilterId, d.Id);
        }
        return new FilterParams(defs, values, text);
    }

    private void SaveCustom(ICommandContext c)
    {
        var cur = CurrentFilterParams(c);
        var look = Effective(cur);
        string curId = cur.GetText("profile");
        int curSlot = BwProfileStore.SlotOf(curId);
        int defSlot = curSlot > 0 && BwProfileStore.Custom(curSlot) != null ? curSlot : FirstEmptySlot();
        var baseP = BwProfileStore.Find(curId) ?? BwProfileStore.BuiltIn[0];
        string defName = curSlot > 0 ? baseP.Name : "My " + baseP.Name.Split('—')[0].Trim().ToLowerInvariant();

        // Fresh parameter ids per call: the dialog remembers values per id, and these defaults depend on the photo.
        string sfx = "_" + (++_askSerial).ToString(CultureInfo.InvariantCulture);
        var slots = Enumerable.Range(1, BwProfileStore.MaxCustom)
            .Select(s => new ParamChoice(s.ToString(CultureInfo.InvariantCulture), SlotLabel(s))).ToArray();
        var ask = c.Ask("Save as my B&W profile", new[]
        {
            ParamDef.Choice("slot" + sfx, "Slot (a used slot is replaced)", defSlot.ToString(CultureInfo.InvariantCulture), slots),
            // A text box: the dialog shows text kinds other than Choice and File as a plain text field.
            new ParamDef { Id = "name" + sfx, Label = "Name", Kind = ParamKind.Color, DefaultText = defName }
        });
        if (ask == null) return;
        if (!int.TryParse(ask.GetText("slot" + sfx), NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot < 1 || slot > BwProfileStore.MaxCustom)
            return;
        string name = (ask.GetText("name" + sfx) ?? "").Trim();
        if (name.Length == 0) name = defName;
        if (name.Length > 60) name = name.Substring(0, 60);
        look.Name = name;
        var before = BwProfileStore.Custom(slot);
        BwProfileStore.SetCustom(slot, look);
        try { BwProfileStore.Save(); }
        catch (Exception ex)
        {
            BwProfileStore.SetCustom(slot, before);
            c.ShowMessage("The profile could not be saved: " + ex.Message);
            return;
        }
        RefreshChoices();
        // The photo now uses the saved profile, with the fine-tuning folded into it.
        c.StepLabel = "B&W profile: " + name;
        c.SetText(FilterId, "profile", BwProfileStore.CustomId(slot));
        foreach (var d in BuildParams(out _))
            if (d.Kind == ParamKind.Slider && d.Id != "amount") c.SetValue(FilterId, d.Id, 0);
        c.SetValue(FilterId, "owntint", 0);
        c.SetStatus($"Saved as my profile {slot}: {name}");
    }

    private void DeleteCustom(ICommandContext c)
    {
        var used = Enumerable.Range(1, BwProfileStore.MaxCustom).Where(s => BwProfileStore.Custom(s) != null).ToList();
        if (used.Count == 0) { c.SetStatus("No profiles of your own yet — use Save as my profile…"); return; }
        var cur = CurrentFilterParams(c);
        int curSlot = BwProfileStore.SlotOf(cur.GetText("profile"));
        int defSlot = used.Contains(curSlot) ? curSlot : used[0];
        string sfx = "_" + (++_askSerial).ToString(CultureInfo.InvariantCulture);
        var ask = c.Ask("Delete my B&W profile", new[]
        {
            ParamDef.Choice("slot" + sfx, "Profile to delete", defSlot.ToString(CultureInfo.InvariantCulture),
                used.Select(s => new ParamChoice(s.ToString(CultureInfo.InvariantCulture), SlotLabel(s))).ToArray())
        });
        if (ask == null) return;
        if (!int.TryParse(ask.GetText("slot" + sfx), NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || BwProfileStore.Custom(slot) == null)
            return;
        var before = BwProfileStore.Custom(slot);
        BwProfileStore.SetCustom(slot, null);
        try { BwProfileStore.Save(); }
        catch (Exception ex)
        {
            BwProfileStore.SetCustom(slot, before);
            c.ShowMessage("The profile could not be deleted: " + ex.Message);
            return;
        }
        RefreshChoices();
        if (slot == curSlot)
        {
            // This photo keeps its look: the deleted profile's values become the fine-tuning of Neutral.
            c.StepLabel = "Delete B&W profile: " + before.Name;
            var neutral = BwProfileStore.BuiltIn[0];
            c.SetText(FilterId, "profile", neutral.Id);
            for (int i = 0; i < BwProfile.Hues; i++) c.SetValue(FilterId, "mix" + BwProfile.HueIds[i], before.Mix[i] - neutral.Mix[i]);
            c.SetValue(FilterId, "contrast", before.Contrast);
            c.SetValue(FilterId, "brightness", before.Brightness);
            c.SetValue(FilterId, "blacks", before.Blacks);
            c.SetValue(FilterId, "grain", before.Grain);
            c.SetValue(FilterId, "grainsize", before.GrainSize - neutral.GrainSize);
            c.SetValue(FilterId, "toning", before.Toning);
            c.SetValue(FilterId, "balance", before.Balance);
            bool tinted = before.Toning > 0;
            c.SetValue(FilterId, "owntint", tinted ? 1 : 0);
            if (tinted)
            {
                c.SetText(FilterId, "shadowtint", before.ShadowTint);
                c.SetText(FilterId, "highlighttint", before.HighlightTint);
            }
        }
        c.SetStatus($"Deleted my profile {slot}: {before.Name}");
    }

    private static int FirstEmptySlot()
    {
        for (int s = 1; s <= BwProfileStore.MaxCustom; s++) if (BwProfileStore.Custom(s) == null) return s;
        return 1;
    }

    private void RefreshChoices()
    {
        if (_profileDef != null) _profileDef.Choices = BwProfileStore.Choices();
    }
}
