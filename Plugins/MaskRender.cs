using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImgViewer.Plugins;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>
/// Renders a <see cref="MaskSpec"/> to a 0..1 weight plane. Coordinates in MaskSpec are fractions of the
/// image at the stage the filter runs in (before crop/rotate), so the same mask fits preview and full resolution.
/// </summary>
public static class MaskRender
{
    public const string Linear = "linear";
    public const string Radial = "radial";
    public const string Brush = "brush";
    public const string Luma = "luma";
    public const string PluginPrefix = "plugin:";

    public static MaskSpec DefaultFor(string kind) => kind switch
    {
        Linear => new MaskSpec { Kind = Linear, P = new[] { 0.5, 0.25, 0.5, 0.6 } },
        Radial => new MaskSpec { Kind = Radial, P = new[] { 0.5, 0.5, 0.3, 0.3, 0.5 } },
        Brush => new MaskSpec { Kind = Brush, Strokes = new List<double[]>() },
        Luma => new MaskSpec { Kind = Luma, P = new[] { 0.6, 1.0, 0.15 } },
        _ => new MaskSpec { Kind = kind }
    };

    private static double P(MaskSpec m, int i, double def) => m.P is not null && m.P.Length > i ? m.P[i] : def;

    private static float Smooth(double t)
    {
        if (t <= 0) return 0f;
        if (t >= 1) return 1f;
        return (float)(t * t * (3 - 2 * t));
    }

    /// <summary>Returns null when the mask covers everything (no blending needed).</summary>
    public static float[]? Render(MaskSpec spec, PluginImage img, RenderContext ctx, PluginRegistry reg)
    {
        int w = img.Width, h = img.Height;
        float[] m;
        switch (spec.Kind)
        {
            case Linear:
                m = RenderLinear(spec, w, h);
                break;
            case Radial:
                m = RenderRadial(spec, w, h);
                break;
            case Brush:
                m = RenderBrush(spec, w, h);
                break;
            case Luma:
                m = RenderLuma(spec, img, ctx.Space);
                break;
            default:
                if (spec.Kind.StartsWith(PluginPrefix, StringComparison.Ordinal))
                {
                    var prov = reg.FindMask(spec.Kind.Substring(PluginPrefix.Length));
                    if (prov is null) return Invertible(new float[w * h], spec);
                    if (prov.Owner is { Faulted: true }) return Invertible(new float[w * h], spec);
                    var fp = new FilterParams(prov.D.Params,
                        spec.Values ?? new Dictionary<string, double>(),
                        spec.Text ?? new Dictionary<string, string>());
                    var res = prov.D.CreateMask(img.Clone(), fp, ctx);
                    if (res is null || res.Length < w * h) throw new InvalidOperationException($"mask '{prov.FullId}' returned a wrong-sized plane");
                    m = new float[w * h];
                    for (int i = 0; i < m.Length; i++) m[i] = Math.Clamp(float.IsFinite(res[i]) ? res[i] : 0f, 0f, 1f);
                    break;
                }
                return null;
        }
        return Invertible(m, spec);
    }

    private static float[] Invertible(float[] m, MaskSpec spec)
    {
        if (spec.Invert)
            for (int i = 0; i < m.Length; i++) m[i] = 1f - m[i];
        return m;
    }

    /// <summary>Linear gradient: 1 at (x1,y1) side, 0 past (x2,y2). Full effect before the start line.</summary>
    private static float[] RenderLinear(MaskSpec s, int w, int h)
    {
        double x1 = P(s, 0, 0.5) * w, y1 = P(s, 1, 0.25) * h, x2 = P(s, 2, 0.5) * w, y2 = P(s, 3, 0.6) * h;
        double dx = x2 - x1, dy = y2 - y1;
        double len2 = dx * dx + dy * dy;
        var m = new float[w * h];
        if (len2 < 1e-6)
        {
            Array.Fill(m, 1f);
            return m;
        }
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                double t = ((x + 0.5 - x1) * dx + (y + 0.5 - y1) * dy) / len2;
                m[row + x] = 1f - Smooth(t);
            }
        });
        return m;
    }

    /// <summary>Ellipse (cx, cy, rx, ry as fractions of width/height) with feather 0..1 of the radius. 1 inside.</summary>
    private static float[] RenderRadial(MaskSpec s, int w, int h)
    {
        double cx = P(s, 0, 0.5) * w, cy = P(s, 1, 0.5) * h;
        double rx = Math.Max(1, P(s, 2, 0.3) * w), ry = Math.Max(1, P(s, 3, 0.3) * h);
        double feather = Math.Clamp(P(s, 4, 0.5), 0.0, 1.0);
        double inner = 1 - feather;
        var m = new float[w * h];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            double ny = (y + 0.5 - cy) / ry;
            for (int x = 0; x < w; x++)
            {
                double nx = (x + 0.5 - cx) / rx;
                double d = Math.Sqrt(nx * nx + ny * ny);
                double t = feather < 1e-6 ? (d <= 1 ? 0 : 1) : (d - inner) / feather;
                m[row + x] = 1f - Smooth(t);
            }
        });
        return m;
    }

    /// <summary>Strokes: [mode, radius (fraction of width), x1, y1, x2, y2, ...]. Paint = max, erase = min.</summary>
    private static float[] RenderBrush(MaskSpec s, int w, int h)
    {
        var m = new float[w * h];
        if (s.Strokes is null) return m;
        foreach (var st in s.Strokes)
        {
            if (st is null || st.Length < 4) continue;
            bool paint = st[0] >= 0.5;
            double r = Math.Max(0.5, st[1] * w);
            double prevX = st[2] * w, prevY = st[3] * h;
            Dab(m, w, h, prevX, prevY, r, paint);
            for (int i = 4; i + 1 < st.Length; i += 2)
            {
                double x = st[i] * w, y = st[i + 1] * h;
                double dx = x - prevX, dy = y - prevY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double spacing = Math.Max(0.5, r / 4);
                int n = (int)Math.Ceiling(dist / spacing);
                for (int k = 1; k <= n; k++)
                {
                    double t = k / (double)n;
                    Dab(m, w, h, prevX + dx * t, prevY + dy * t, r, paint);
                }
                prevX = x;
                prevY = y;
            }
        }
        return m;
    }

    private static void Dab(float[] m, int w, int h, double cx, double cy, double r, bool paint)
    {
        const double hardness = 0.5;
        int x0 = Math.Max(0, (int)Math.Floor(cx - r)), x1 = Math.Min(w - 1, (int)Math.Ceiling(cx + r));
        int y0 = Math.Max(0, (int)Math.Floor(cy - r)), y1 = Math.Min(h - 1, (int)Math.Ceiling(cy + r));
        double inner = r * hardness;
        for (int y = y0; y <= y1; y++)
        {
            int row = y * w;
            double dy = y + 0.5 - cy;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - cx;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d >= r) continue;
                float f = d <= inner ? 1f : 1f - Smooth((d - inner) / (r - inner));
                int i = row + x;
                if (paint) { if (f > m[i]) m[i] = f; }
                else m[i] = Math.Min(m[i], 1f - f);
            }
        }
    }

    /// <summary>Luminance range [lo, hi] with feather, measured on perceptual (sRGB encoded) luminance.</summary>
    private static float[] RenderLuma(MaskSpec s, PluginImage img, PluginColorSpace space)
    {
        double lo = P(s, 0, 0.6), hi = P(s, 1, 1.0), feather = Math.Max(1e-4, P(s, 2, 0.15));
        if (hi < lo) (lo, hi) = (hi, lo);
        int n = img.Length;
        var m = new float[n];
        bool linear = space == PluginColorSpace.LinearSrgb;
        Parallel.For(0, img.Height, y =>
        {
            int row = y * img.Width;
            for (int x = 0; x < img.Width; x++)
            {
                int i = row + x;
                float r = img.R[i], g = img.G[i], b = img.B[i];
                float l;
                if (linear)
                {
                    float yl = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    l = PluginImaging.LinearToSrgbF(Math.Clamp(yl, 0f, 1f));
                }
                else l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                double a = l < lo ? 1 - (lo - l) / feather : l > hi ? 1 - (l - hi) / feather : 1;
                m[i] = Smooth(a);
            }
        });
        return m;
    }

    /// <summary>dst = orig + m * (dst - orig).</summary>
    public static void Blend(PluginImage orig, PluginImage dst, float[] m)
    {
        int n = dst.Length;
        Parallel.For(0, dst.Height, y =>
        {
            int row = y * dst.Width;
            for (int x = 0; x < dst.Width; x++)
            {
                int i = row + x;
                float a = m[i];
                if (a >= 1f) continue;
                dst.R[i] = orig.R[i] + a * (dst.R[i] - orig.R[i]);
                dst.G[i] = orig.G[i] + a * (dst.G[i] - orig.G[i]);
                dst.B[i] = orig.B[i] + a * (dst.B[i] - orig.B[i]);
            }
        });
    }
}
