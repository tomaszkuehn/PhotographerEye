using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ImgViewer.Imaging;
using ImgViewer.Plugins;

namespace ImgViewer.Services;

/// <summary>Edit-state helpers behind the plugin command context: core keys, curves, masks, pixel downscale.</summary>
public static class PluginEditOps
{
    /// <summary>Highest "Mono" value (0 colour, 1 documentary, 2 Tri-X).</summary>
    public const int MonoMax = (int)MonoMode.TriX;
    /// <summary>Plugin keys of the basic settings, in panel order (<see cref="CoreParams"/>).</summary>
    public static IReadOnlyList<string> CoreKeyNames => CoreParams.Keys;

    public static readonly string[] CropAspects = { "original", "3:2", "4:3", "16:9", "16:10" };

    /// <summary>Area-averaged downscale of 16-bit data to float, multiplied by <paramref name="scale"/>.</summary>
    public static PluginImage Downscale(Raw16Image raw, int maxLongSide, float scale)
    {
        int lon = Math.Max(raw.W, raw.H);
        if (maxLongSide <= 0 || lon <= maxLongSide)
        {
            var full = new PluginImage(raw.W, raw.H);
            Parallel.For(0, raw.H, y =>
            {
                for (int i = y * raw.W, e = i + raw.W; i < e; i++)
                {
                    full.R[i] = raw.R[i] * scale;
                    full.G[i] = raw.G[i] * scale;
                    full.B[i] = raw.B[i] * scale;
                }
            });
            return full;
        }
        double f = (double)lon / maxLongSide;
        int tw = Math.Max(1, (int)Math.Round(raw.W / f)), th = Math.Max(1, (int)Math.Round(raw.H / f));
        var img = new PluginImage(tw, th);
        Parallel.For(0, th, ty =>
        {
            int y0 = (int)((long)ty * raw.H / th), y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * raw.H / th));
            for (int tx = 0; tx < tw; tx++)
            {
                int x0 = (int)((long)tx * raw.W / tw), x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * raw.W / tw));
                double r = 0, g = 0, b = 0;
                for (int y = y0; y < y1; y++)
                    for (int i = y * raw.W + x0, e = y * raw.W + x1; i < e; i++)
                    {
                        r += raw.R[i];
                        g += raw.G[i];
                        b += raw.B[i];
                    }
                double k = scale / (double)((y1 - y0) * (x1 - x0));
                int o = ty * tw + tx;
                img.R[o] = (float)(r * k);
                img.G[o] = (float)(g * k);
                img.B[o] = (float)(b * k);
            }
        });
        return img;
    }


    /// <summary>Area-averaged 16-bit copy with the long side at most <paramref name="maxLongSide"/> (same image when smaller).</summary>
    public static Raw16Image Downscale16(Raw16Image raw, int maxLongSide)
    {
        if (maxLongSide <= 0 || Math.Max(raw.W, raw.H) <= maxLongSide) return raw;
        var f = Downscale(raw, maxLongSide, 1f);
        var o = new Raw16Image(f.Width, f.Height);
        for (int i = 0; i < f.Length; i++)
        {
            o.R[i] = (ushort)Math.Clamp(Math.Round(f.R[i]), 0, 65535);
            o.G[i] = (ushort)Math.Clamp(Math.Round(f.G[i]), 0, 65535);
            o.B[i] = (ushort)Math.Clamp(Math.Round(f.B[i]), 0, 65535);
        }
        return o;
    }

    /// <summary>Canonical core key ("Exposure", ...) for "exposure", "core.Exposure", "ShadowLift", ...; null when unknown.</summary>
    public static string? CoreKey(string? key) => CoreParams.Find(key)?.Key;

    public static double CoreGet(EyeState s, string k) => Param(k).Get(s);

    public static EyeState CoreSet(EyeState s, string k, double v) => Param(k).Set(s, v);

    private static CoreParam Param(string k) =>
        CoreParams.Find(k) is { } p && p.Key.Equals(k, StringComparison.OrdinalIgnoreCase) ? p : throw new ArgumentException($"unknown core setting '{k}'");

    /// <summary>Validated, sorted copy of curve points; clamps to [lo, hi]. Null or empty → null.</summary>
    public static double[][]? CleanCurve(double[][]? pts, double lo, double hi)
    {
        if (pts is null || pts.Length == 0) return null;
        var list = new List<double[]>(pts.Length);
        foreach (var p in pts)
        {
            if (p is not { Length: 2 } || !double.IsFinite(p[0]) || !double.IsFinite(p[1]))
                throw new ArgumentException("curve points must be finite [input, output] pairs");
            list.Add(new[] { Math.Clamp(p[0], lo, hi), Math.Clamp(p[1], lo, hi) });
        }
        return list.OrderBy(p => p[0]).ToArray();
    }

    public static double[][] CopyCurve(double[][]? pts) =>
        pts is null ? Array.Empty<double[]>() : pts.Where(p => p is not null).Select(p => (double[])p.Clone()).ToArray();

    public static MaskSpec? ToMaskSpec(MaskInfo? m)
    {
        if (m is null || string.IsNullOrWhiteSpace(m.Kind)) return null;
        string kind = m.Kind.Trim();
        double[]? p = m.P is null ? null : (double[])m.P.Clone();
        if (p is not null && p.Any(v => !double.IsFinite(v))) throw new ArgumentException("mask points must be finite");
        int need = kind switch { "linear" => 4, "radial" => 5, "luma" => 3, _ => 0 };
        if (kind is "linear" or "radial" or "luma")
        {
            if (kind == "radial" && p is { Length: 4 }) p = new[] { p[0], p[1], p[2], p[3], 0.5 };
            if (p is null || p.Length < need) throw new ArgumentException($"mask '{kind}' needs {need} values in P");
        }
        else if (kind == "brush")
        {
            if (m.Strokes is null || m.Strokes.Count == 0) throw new ArgumentException("mask 'brush' needs Strokes");
            foreach (var s in m.Strokes)
                if (s is null || s.Length < 4 || s.Any(v => !double.IsFinite(v)))
                    throw new ArgumentException("brush strokes are [mode, radius, x1, y1, ...] with finite values");
        }
        else if (!kind.StartsWith("plugin:", StringComparison.Ordinal))
        {
            throw new ArgumentException($"unknown mask kind '{kind}' (linear, radial, luma, brush or plugin:<pluginId:maskId>)");
        }
        return new MaskSpec
        {
            Kind = kind,
            P = p,
            Invert = m.Invert,
            Strokes = m.Strokes?.Select(s => (double[])s.Clone()).ToList(),
            Values = m.Values is null ? null : new Dictionary<string, double>(m.Values, StringComparer.Ordinal),
            Text = m.Text is null ? null : new Dictionary<string, string>(m.Text, StringComparer.Ordinal)
        };
    }

    public static MaskInfo? ToMaskInfo(MaskSpec? m) =>
        m is null || string.IsNullOrEmpty(m.Kind) ? null : new MaskInfo
        {
            Kind = m.Kind,
            P = m.P is null ? null : (double[])m.P.Clone(),
            Invert = m.Invert,
            Strokes = m.Strokes?.Select(s => (double[])s.Clone()).ToList(),
            Values = m.Values is null ? null : new Dictionary<string, double>(m.Values, StringComparer.Ordinal),
            Text = m.Text is null ? null : new Dictionary<string, string>(m.Text, StringComparer.Ordinal)
        };
}
