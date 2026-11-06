using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImgViewer.Imaging;
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
    /// <summary>A mask that uses the mask of another tool or filter (<see cref="MaskSpec.Ref"/>), so both follow one shape.</summary>
    public const string Link = "link";

    /// <summary>Diameter of a new radial mask (its outer, fully feathered edge) as a fraction of the image height.</summary>
    public const double RadialDefaultDiameter = 0.15;

    /// <summary>
    /// A new mask of <paramref name="kind"/>. <paramref name="aspect"/> = image width / height: a new radial mask is a
    /// circle in the middle with a diameter of <see cref="RadialDefaultDiameter"/> of the image height.
    /// </summary>
    public static MaskSpec DefaultFor(string kind, double aspect = 1.0) => kind switch
    {
        Linear => new MaskSpec { Kind = Linear, P = new[] { 0.5, 0.25, 0.5, 0.6 } },
        Radial => new MaskSpec
        {
            Kind = Radial,
            P = new[] { 0.5, 0.5, RadialDefaultDiameter / 2 / (aspect > 0 && double.IsFinite(aspect) ? aspect : 1.0), RadialDefaultDiameter / 2, 0.5 }
        },
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

    /// <summary>Rotation angle of a radial mask (P[5]) folded to -90..90 degrees (an ellipse turned by 180 degrees is the same).</summary>
    public static double RadialAngle(MaskSpec? s)
    {
        double a = s?.P is { Length: > 5 } p && double.IsFinite(p[5]) ? p[5] : 0;
        a = ((a + 90) % 180 + 180) % 180 - 90;
        return a == -90 ? 90 : a;
    }

    /// <summary>
    /// The user feather of a radial mask (0..1): 0 = no feather (the outer outline sits on the inner ellipse), 1 = the
    /// outer outline is twice the inner ellipse (its double diameter). The stored band (P[4]) is a fraction of the
    /// outer radius, so band = F / (1 + F); a stored band above 0.5 (not reachable through the UI) reads as 1.
    /// </summary>
    public static double RadialUserFeather(double band)
    {
        double f = Math.Clamp(band, 0.0, 0.5);
        return f / (1 - f);
    }

    /// <summary>Inverse of <see cref="RadialUserFeather"/>: the stored band of a user feather 0..1.</summary>
    public static double RadialBandFraction(double userFeather)
    {
        double f = Math.Clamp(userFeather, 0.0, 1.0);
        return f / (1 + f);
    }

    /// <summary>
    /// Feather drag by the outer outline of a radial mask: the pointer at <paramref name="innerRatio"/> × the inner
    /// ellipse (1 = on the inner ellipse, 2 = twice it) with the inner ellipse kept fixed; returns the new stored band
    /// and the scale of the outer radii (P[2], P[3]). A ratio of 1 + the start band (the pointer on the grabbed
    /// outline) gives back the start values, so the outline does not jump when it is grabbed; a stored band above
    /// 0.5 (legacy, not reachable through the UI) snaps into the range on the first drag.
    /// </summary>
    public static (double Band, double Scale) RadialFeatherDrag(double band0, double innerRatio)
    {
        double f0 = Math.Clamp(band0, 0.0, 0.5);
        double f = Math.Clamp(innerRatio - 1, 0.0, 1.0);
        return (f / (1 + f), (1 + f) * (1 - f0));
    }

    public static bool IsGeometric(string kind) => kind is Linear or Radial or Brush;

    /// <summary>
    /// Where the on-photo marker of a mask goes (source fractions): the centre of a radial, the middle of a linear
    /// gradient, the painted point nearest to the middle of the brush paint strokes (so it sits on the paint).
    /// Null for masks without a place on the photo (luminance, plugin masks, a brush with no paint).
    /// </summary>
    public static (double X, double Y)? PinPoint(MaskSpec? s)
    {
        if (s is null) return null;
        static double At(double[]? p, int i, double def) => p is not null && p.Length > i && double.IsFinite(p[i]) ? p[i] : def;
        switch (s.Kind)
        {
            case Radial:
                return (At(s.P, 0, 0.5), At(s.P, 1, 0.5));
            case Linear:
                return ((At(s.P, 0, 0.5) + At(s.P, 2, 0.5)) / 2, (At(s.P, 1, 0.25) + At(s.P, 3, 0.6)) / 2);
            case Brush:
            {
                double sx = 0, sy = 0;
                int n = 0;
                foreach (var st in s.Strokes ?? new List<double[]>())
                {
                    if (st is null || st.Length < 4 || st[0] < 0.5) continue;
                    for (int i = 2; i + 1 < st.Length; i += 2) { sx += st[i]; sy += st[i + 1]; n++; }
                }
                if (n == 0) return null;
                double cx = sx / n, cy = sy / n, best = double.MaxValue, bx = cx, by = cy;
                foreach (var st in s.Strokes!)
                {
                    if (st is null || st.Length < 4 || st[0] < 0.5) continue;
                    for (int i = 2; i + 1 < st.Length; i += 2)
                    {
                        double d = (st[i] - cx) * (st[i] - cx) + (st[i + 1] - cy) * (st[i + 1] - cy);
                        if (d < best) { best = d; bx = st[i]; by = st[i + 1]; }
                    }
                }
                return (bx, by);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Geometric mask (source fractions) for an image after crop and rotation (<paramref name="w"/> x <paramref name="h"/>):
    /// rendered on a grid shaped like the source at the output's pixel density and sampled through the inverse geometry.
    /// </summary>
    public static float[] RenderMapped(MaskSpec spec, int w, int h, GeoState g, double srcW, double srcH)
    {
        srcW = Math.Max(1, srcW);
        srcH = Math.Max(1, srcH);
        double outW = Math.Max(1e-6, PhotoGeo.OutputWidth(g, srcW, srcH));
        double k = w / outW;
        double gw0 = srcW * k, gh0 = srcH * k;
        double cap = Math.Min(1.0, 6000.0 / Math.Max(gw0, gh0));
        int gw = Math.Max(1, (int)Math.Round(gw0 * cap)), gh = Math.Max(1, (int)Math.Round(gh0 * cap));
        var grid = spec.Kind switch
        {
            Linear => RenderLinear(spec, gw, gh),
            Radial => RenderRadial(spec, gw, gh),
            _ => RenderBrush(spec, gw, gh)
        };
        var m = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                var p = PhotoGeo.MapFromOutput(g, srcW, srcH, new System.Windows.Point((x + 0.5) / w, (y + 0.5) / h));
                double fx = p.X * gw - 0.5, fy = p.Y * gh - 0.5;
                int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
                double ax = fx - x0, ay = fy - y0;
                float S(int xx, int yy) => grid[Math.Clamp(yy, 0, gh - 1) * gw + Math.Clamp(xx, 0, gw - 1)];
                m[y * w + x] = (float)((S(x0, y0) * (1 - ax) + S(x0 + 1, y0) * ax) * (1 - ay) + (S(x0, y0 + 1) * (1 - ax) + S(x0 + 1, y0 + 1) * ax) * ay);
            }
        });
        return Invertible(m, spec);
    }

    private static float[] Invertible(float[] m, MaskSpec spec)
    {
        if (spec.Invert)
            for (int i = 0; i < m.Length; i++) m[i] = 1f - m[i];
        return m;
    }

    /// <summary>Linear gradient: 1 at (x1,y1) side, 0 past (x2,y2). Full effect before the start line; both handles on one point give no effect.</summary>
    private static float[] RenderLinear(MaskSpec s, int w, int h)
    {
        double x1 = P(s, 0, 0.5) * w, y1 = P(s, 1, 0.25) * h, x2 = P(s, 2, 0.5) * w, y2 = P(s, 3, 0.6) * h;
        double dx = x2 - x1, dy = y2 - y1;
        double len2 = dx * dx + dy * dy;
        var m = new float[w * h];
        if (len2 < 1e-6) return m; // no direction: empty, not the whole photo
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

    /// <summary>
    /// Ellipse (cx, cy, rx, ry as fractions of width/height) with feather 0..1 of the radius and an optional rotation
    /// (P[5], degrees clockwise, in pixel space so the shape keeps its proportions). 1 inside.
    /// </summary>
    private static float[] RenderRadial(MaskSpec s, int w, int h)
    {
        double cx = P(s, 0, 0.5) * w, cy = P(s, 1, 0.5) * h;
        double rx = Math.Max(1, P(s, 2, 0.3) * w), ry = Math.Max(1, P(s, 3, 0.3) * h);
        double feather = Math.Clamp(P(s, 4, 0.5), 0.0, 1.0);
        double inner = 1 - feather;
        double th = P(s, 5, 0) * Math.PI / 180, cos = Math.Cos(th), sin = Math.Sin(th);
        var m = new float[w * h];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            double dy = y + 0.5 - cy;
            for (int x = 0; x < w; x++)
            {
                double dx = x + 0.5 - cx;
                double nx = (dx * cos + dy * sin) / rx, ny = (dy * cos - dx * sin) / ry;
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
