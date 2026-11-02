using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImgViewer.Plugins;

namespace ImgViewer;

/// <summary>Built-in filters registered under plugin id "core": color profiles (3D LUT) and a maskable local adjustment.</summary>
public static class BuiltInFilters
{
    public const string CoreId = "core";
    public const string LutId = "core:lut";
    public const string LocalId = "core:local";

    public static string UserLutDir => Path.Combine(PluginManager.AppDataDir, "LUTs");
    public static string AppLutDir => Path.Combine(AppContext.BaseDirectory, "luts");

    private static readonly string[] BuiltinProfiles = { "warm", "cool", "tealorange", "matte", "vivid" };

    public static List<Registered<FilterDescriptor>> Create()
    {
        var list = new List<Registered<FilterDescriptor>>
        {
            Wrap(CreateLut(), 0),
            Wrap(CreateLocal(), 1)
        };
        return list;
    }

    private static Registered<FilterDescriptor> Wrap(FilterDescriptor d, int index) => new()
    {
        PluginId = CoreId,
        PluginName = "PhotographerEye",
        FullId = CoreId + ":" + d.Id,
        LocalId = d.Id,
        D = d,
        Owner = null,
        Index = index
    };

    // ------------------------------------------------------------------
    //  Color profiles (.cube)
    // ------------------------------------------------------------------

    public static IEnumerable<string> LutFiles()
    {
        foreach (var dir in new[] { AppLutDir, UserLutDir })
        {
            string[] files;
            try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cube") : Array.Empty<string>(); }
            catch (Exception) { continue; }
            foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) yield return f;
        }
    }

    private static FilterDescriptor CreateLut()
    {
        var choices = new List<ParamChoice>
        {
            new("none", "None"),
            new("builtin:warm", "Warm"),
            new("builtin:cool", "Cool"),
            new("builtin:tealorange", "Teal & orange"),
            new("builtin:matte", "Matte"),
            new("builtin:vivid", "Vivid")
        };
        foreach (var f in LutFiles()) choices.Add(new ParamChoice("file:" + f, Path.GetFileNameWithoutExtension(f)));
        choices.Add(new ParamChoice("custom", "Custom .cube file…"));
        return new FilterDescriptor
        {
            Id = "lut",
            Label = "Color profile",
            Description = "3D LUT (.cube). Put .cube files into %APPDATA%\\PhotographerEye\\LUTs to list them here.",
            Group = "Color",
            Stage = FilterStage.Display,
            Order = -100,
            Params = new[]
            {
                ParamDef.Choice("profile", "Profile", "none", choices.ToArray()),
                ParamDef.File("file", "Custom file", "Cube LUT (*.cube)|*.cube"),
                ParamDef.Slider("amount", "Amount", 0, 100, 100)
            },
            IsIdentity = p =>
            {
                var prof = p.GetText("profile");
                if (prof == "none" || p.Get("amount") <= 0) return true;
                if (prof == "custom") return string.IsNullOrWhiteSpace(p.GetText("file"));
                return false;
            },
            Render = (img, p, ctx) =>
            {
                var prof = p.GetText("profile");
                var lut = prof switch
                {
                    "custom" => CubeLut.Load(p.GetText("file")),
                    _ when prof.StartsWith("file:", StringComparison.Ordinal) => CubeLut.Load(prof.Substring(5)),
                    _ when prof.StartsWith("builtin:", StringComparison.Ordinal) => CubeLut.Builtin(prof.Substring(8)),
                    _ => null
                };
                if (lut is null) return;
                lut.Apply(img, (float)(p.Get("amount") / 100.0), ctx.Space == PluginColorSpace.LinearSrgb);
            }
        };
    }

    // ------------------------------------------------------------------
    //  Local adjustment (meant to be used with a mask)
    // ------------------------------------------------------------------

    private static FilterDescriptor CreateLocal()
    {
        return new FilterDescriptor
        {
            Id = "local",
            Label = "Local adjustment",
            Description = "Exposure, contrast, white balance and saturation. Attach a gradient, radial, brush or luminance mask to limit it to part of the photo.",
            Group = "Local",
            Stage = FilterStage.Display,
            Order = 100,
            Params = new[]
            {
                ParamDef.Slider("exposure", "Exposure", -4, 4, 0, 0.05, "+0.00;-0.00;0.00"),
                ParamDef.Slider("contrast", "Contrast", -100, 100, 0),
                ParamDef.Slider("temperature", "Temperature", -100, 100, 0),
                ParamDef.Slider("tint", "Tint", -100, 100, 0),
                ParamDef.Slider("saturation", "Saturation", -100, 100, 0)
            },
            Render = (img, p, ctx) => ApplyLocal(img, p, ctx.Space == PluginColorSpace.LinearSrgb)
        };
    }

    private static void ApplyLocal(PluginImage img, FilterParams p, bool linearInput)
    {
        float gain = (float)Math.Pow(2, p.Get("exposure"));
        float t = (float)(p.Get("temperature") / 100.0);
        float tint = (float)(p.Get("tint") / 100.0);
        float c = (float)(p.Get("contrast") / 100.0);
        float sat = (float)(1 + p.Get("saturation") / 100.0);
        float rm = gain * (1 + 0.25f * t), gm = gain * (1 - 0.18f * tint), bm = gain * (1 - 0.25f * t);
        Parallel.For(0, img.Height, y =>
        {
            int row = y * img.Width;
            for (int i = row; i < row + img.Width; i++)
            {
                float r = img.R[i], g = img.G[i], b = img.B[i];
                if (!linearInput)
                {
                    r = PluginImaging.SrgbToLinearF(Math.Clamp(r, 0f, 1f));
                    g = PluginImaging.SrgbToLinearF(Math.Clamp(g, 0f, 1f));
                    b = PluginImaging.SrgbToLinearF(Math.Clamp(b, 0f, 1f));
                }
                r *= rm;
                g *= gm;
                b *= bm;
                // Soft roll-off instead of hard clipping when brightening.
                r = Roll(r);
                g = Roll(g);
                b = Roll(b);
                float er = PluginImaging.LinearToSrgbF(r), eg = PluginImaging.LinearToSrgbF(g), eb = PluginImaging.LinearToSrgbF(b);
                if (c != 0)
                {
                    er = Contrast(er, c);
                    eg = Contrast(eg, c);
                    eb = Contrast(eb, c);
                }
                if (sat != 1)
                {
                    float l = 0.2126f * er + 0.7152f * eg + 0.0722f * eb;
                    er = l + (er - l) * sat;
                    eg = l + (eg - l) * sat;
                    eb = l + (eb - l) * sat;
                }
                er = Math.Clamp(er, 0f, 1f);
                eg = Math.Clamp(eg, 0f, 1f);
                eb = Math.Clamp(eb, 0f, 1f);
                if (linearInput)
                {
                    er = PluginImaging.SrgbToLinearF(er);
                    eg = PluginImaging.SrgbToLinearF(eg);
                    eb = PluginImaging.SrgbToLinearF(eb);
                }
                img.R[i] = er;
                img.G[i] = eg;
                img.B[i] = eb;
            }
        });
    }

    private static float Roll(float v)
    {
        if (v <= 0.8f) return Math.Max(v, 0f);
        float x = v - 0.8f;
        return 0.8f + 0.2f * x / (x + 0.2f);
    }

    private static float Contrast(float v, float c)
    {
        // S-curve around mid grey; c in -1..1.
        float k = c >= 0 ? 1 + 2.5f * c : 1 + c;
        float d = v - 0.5f;
        float s = 0.5f + d * k;
        if (c > 0)
        {
            // Smoothly compress towards the ends so contrast never hard-clips.
            float a = Math.Abs(d * k) * 2;
            float soft = a / (1 + 0.6f * Math.Max(0, a - 0.6f));
            s = 0.5f + Math.Sign(d) * soft / 2;
        }
        return Math.Clamp(s, 0f, 1f);
    }

    /// <summary>Lightweight check used by the presets / UI.</summary>
    public static bool IsBuiltinProfile(string id) => BuiltinProfiles.Contains(id);
}

/// <summary>3D LUT (Adobe/Resolve .cube), applied with trilinear interpolation on sRGB encoded values.</summary>
public sealed class CubeLut
{
    public int Size { get; }
    public string Title { get; }
    private readonly float[] _data; // (r + g*N + b*N*N) * 3
    private readonly float _dMin0, _dMin1, _dMin2, _dMax0, _dMax1, _dMax2;

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, CubeLut Lut)> FileCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CubeLut> BuiltinCache = new();

    public CubeLut(int size, float[] data, string title = "", float[]? domainMin = null, float[]? domainMax = null)
    {
        if (size < 2 || data.Length < size * size * size * 3) throw new ArgumentException("bad LUT size");
        Size = size;
        _data = data;
        Title = title;
        _dMin0 = domainMin?[0] ?? 0; _dMin1 = domainMin?[1] ?? 0; _dMin2 = domainMin?[2] ?? 0;
        _dMax0 = domainMax?[0] ?? 1; _dMax1 = domainMax?[1] ?? 1; _dMax2 = domainMax?[2] ?? 1;
    }

    public static CubeLut? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("LUT file not found", path);
        var stamp = File.GetLastWriteTimeUtc(path);
        if (FileCache.TryGetValue(path, out var c) && c.Stamp == stamp) return c.Lut;
        var lut = Parse(File.ReadAllLines(path));
        FileCache[path] = (stamp, lut);
        return lut;
    }

    public static CubeLut Parse(IEnumerable<string> lines)
    {
        int size = 0;
        string title = "";
        float[] dmin = { 0, 0, 0 }, dmax = { 1, 1, 1 };
        var values = new List<float>();
        var ci = CultureInfo.InvariantCulture;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (char.IsLetter(line[0]))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0].ToUpperInvariant())
                {
                    case "TITLE":
                        title = line.Substring(5).Trim().Trim('"');
                        break;
                    case "LUT_3D_SIZE":
                        size = int.Parse(parts[1], ci);
                        break;
                    case "DOMAIN_MIN":
                        dmin = parts.Skip(1).Take(3).Select(s => float.Parse(s, ci)).ToArray();
                        break;
                    case "DOMAIN_MAX":
                        dmax = parts.Skip(1).Take(3).Select(s => float.Parse(s, ci)).ToArray();
                        break;
                    case "LUT_1D_SIZE":
                        throw new InvalidDataException("1D .cube LUTs are not supported");
                }
                continue;
            }
            var nums = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length < 3) continue;
            for (int k = 0; k < 3; k++) values.Add(float.Parse(nums[k], NumberStyles.Float, ci));
        }
        if (size < 2 || size > 256) throw new InvalidDataException("missing or invalid LUT_3D_SIZE");
        if (values.Count < size * size * size * 3) throw new InvalidDataException($"LUT has {values.Count / 3} entries, expected {size * size * size}");
        return new CubeLut(size, values.ToArray(), title, dmin, dmax);
    }

    public static CubeLut? Builtin(string name)
    {
        if (!BuiltInFilters.IsBuiltinProfile(name)) return null;
        return BuiltinCache.GetOrAdd(name, n => Generate(n, 33));
    }

    private static CubeLut Generate(string name, int n)
    {
        var data = new float[n * n * n * 3];
        for (int b = 0; b < n; b++)
        for (int g = 0; g < n; g++)
        for (int r = 0; r < n; r++)
        {
            float fr = r / (float)(n - 1), fg = g / (float)(n - 1), fb = b / (float)(n - 1);
            float l = 0.2126f * fr + 0.7152f * fg + 0.0722f * fb;
            float vr, vg, vb;
            switch (name)
            {
                case "warm":
                    vr = fr * 1.05f + 0.015f; vg = fg * 1.01f; vb = fb * 0.90f;
                    break;
                case "cool":
                    vr = fr * 0.92f; vg = fg * 1.0f + 0.005f; vb = fb * 1.06f + 0.015f;
                    break;
                case "tealorange":
                {
                    float hw = Smooth(l), sw = 1 - hw;
                    vr = fr + 0.08f * hw - 0.06f * sw;
                    vg = fg + 0.02f * hw + 0.02f * sw;
                    vb = fb - 0.08f * hw + 0.07f * sw;
                    float nl = 0.2126f * vr + 0.7152f * vg + 0.0722f * vb;
                    vr += l - nl; vg += l - nl; vb += l - nl;
                    break;
                }
                case "matte":
                {
                    float s = 0.88f;
                    vr = l + (fr - l) * s; vg = l + (fg - l) * s; vb = l + (fb - l) * s;
                    vr = 0.06f + vr * 0.89f; vg = 0.06f + vg * 0.89f; vb = 0.07f + vb * 0.87f;
                    break;
                }
                default: // vivid
                {
                    float s = 1.3f;
                    vr = l + (fr - l) * s; vg = l + (fg - l) * s; vb = l + (fb - l) * s;
                    vr = S(vr); vg = S(vg); vb = S(vb);
                    break;
                }
            }
            int i = (r + g * n + b * n * n) * 3;
            data[i] = Math.Clamp(vr, 0f, 1f);
            data[i + 1] = Math.Clamp(vg, 0f, 1f);
            data[i + 2] = Math.Clamp(vb, 0f, 1f);
        }
        return new CubeLut(n, data, name);
    }

    private static float Smooth(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3 - 2 * t); }
    private static float S(float v) { v = Math.Clamp(v, 0f, 1f); return v + 0.15f * (Smooth(v) - v); }

    /// <summary>Applies the LUT in place. <paramref name="linear"/>: buffer is linear light (encode, map, decode).</summary>
    public void Apply(PluginImage img, float amount, bool linear)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        int n = Size;
        float max = n - 1;
        Parallel.For(0, img.Height, y =>
        {
            int row = y * img.Width;
            Span<float> o = stackalloc float[3];
            for (int i = row; i < row + img.Width; i++)
            {
                float r = img.R[i], g = img.G[i], b = img.B[i];
                if (linear)
                {
                    r = PluginImaging.LinearToSrgbF(Math.Clamp(r, 0f, 1f));
                    g = PluginImaging.LinearToSrgbF(Math.Clamp(g, 0f, 1f));
                    b = PluginImaging.LinearToSrgbF(Math.Clamp(b, 0f, 1f));
                }
                Sample(Norm(r, _dMin0, _dMax0) * max, Norm(g, _dMin1, _dMax1) * max, Norm(b, _dMin2, _dMax2) * max, o);
                float nr = r + amount * (o[0] - r), ng = g + amount * (o[1] - g), nb = b + amount * (o[2] - b);
                if (linear)
                {
                    nr = PluginImaging.SrgbToLinearF(Math.Clamp(nr, 0f, 1f));
                    ng = PluginImaging.SrgbToLinearF(Math.Clamp(ng, 0f, 1f));
                    nb = PluginImaging.SrgbToLinearF(Math.Clamp(nb, 0f, 1f));
                }
                img.R[i] = nr;
                img.G[i] = ng;
                img.B[i] = nb;
            }
        });
    }

    private static float Norm(float v, float lo, float hi) => Math.Clamp((v - lo) / (hi - lo), 0f, 1f);

    private void Sample(float x, float y, float z, Span<float> o)
    {
        int n = Size;
        int x0 = Math.Min((int)x, n - 2), y0 = Math.Min((int)y, n - 2), z0 = Math.Min((int)z, n - 2);
        float fx = x - x0, fy = y - y0, fz = z - z0;
        for (int c = 0; c < 3; c++)
        {
            float c000 = _data[(x0 + y0 * n + z0 * n * n) * 3 + c];
            float c100 = _data[(x0 + 1 + y0 * n + z0 * n * n) * 3 + c];
            float c010 = _data[(x0 + (y0 + 1) * n + z0 * n * n) * 3 + c];
            float c110 = _data[(x0 + 1 + (y0 + 1) * n + z0 * n * n) * 3 + c];
            float c001 = _data[(x0 + y0 * n + (z0 + 1) * n * n) * 3 + c];
            float c101 = _data[(x0 + 1 + y0 * n + (z0 + 1) * n * n) * 3 + c];
            float c011 = _data[(x0 + (y0 + 1) * n + (z0 + 1) * n * n) * 3 + c];
            float c111 = _data[(x0 + 1 + (y0 + 1) * n + (z0 + 1) * n * n) * 3 + c];
            float c00 = c000 + fx * (c100 - c000), c10 = c010 + fx * (c110 - c010);
            float c01 = c001 + fx * (c101 - c001), c11 = c011 + fx * (c111 - c011);
            float c0 = c00 + fy * (c10 - c00), c1 = c01 + fy * (c11 - c01);
            o[c] = c0 + fz * (c1 - c0);
        }
    }
}
