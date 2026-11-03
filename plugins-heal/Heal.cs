using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.Heal;

/// <summary>
/// Spot healing: each spot copies a round patch from a source point of the photo over a defect. "Heal" keeps the
/// texture of the source but matches its tone and colour to the defect's surroundings (the log ratio measured on the
/// patch border is interpolated harmonically over the disk), "Clone" copies the source as is. Runs in scene-linear
/// light before the develop pipeline, so later adjustments act on the repaired photo.
/// </summary>
public sealed class HealPlugin : IEyePlugin
{
    public const int SpotCount = 8;

    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "photographereye.heal",
        Name = "Heal",
        Version = "1.0",
        Author = "PhotographerEye",
        Description = "Removes dust, spots and small defects by copying another part of the photo over them (heal matches the tone of the surroundings, clone copies as is)."
    };

    public void Initialize(IPluginHost host)
    {
        for (int i = 1; i <= SpotCount; i++)
        {
            host.AddFilter(new FilterDescriptor
            {
                Id = "spot" + i,
                Label = "Spot " + i,
                Description = "Pick the defect, then the source area to copy from",
                Group = "Retouch",
                Stage = FilterStage.SceneLinear,
                Order = i,
                SupportsMask = false,
                RepeatGroup = "Spot",
                Params = HealSpot.Params(),
                IsIdentity = HealSpot.IsIdentity,
                Render = (img, p, ctx) => HealSpot.Render(img, p, ctx)
            });
        }
    }
}

public static class HealSpot
{
    /// <summary>Fourier terms of the border correction (higher = follows the border more closely, but also its noise).</summary>
    public const int Terms = 10;
    /// <summary>Border samples.</summary>
    public const int Samples = 96;

    public static IReadOnlyList<ParamDef> Params() => new[]
    {
        Tip(Point("target", "Defect", ""), "Click Pick on photo, then click the centre of the defect"),
        Tip(Point("source", "Source", "target"), "Click Pick on photo, then click a clean area with similar texture"),
        Tip(ParamDef.Slider("size", "Size", 0.2, 15, 1.5, 0.1, "0.0"), "Spot radius in % of the photo's long side"),
        Tip(ParamDef.Slider("feather", "Feather", 0, 100, 40), "Soft edge, % of the radius"),
        Tip(ParamDef.Choice("mode", "Mode", "heal", new ParamChoice("heal", "Heal (match surroundings)"), new ParamChoice("clone", "Clone (copy as is)")),
            "Heal keeps the source texture but takes tone and colour from around the defect"),
        Tip(ParamDef.Slider("opacity", "Opacity", 0, 100, 100), "Strength of the repair")
    };

    /// <summary>Point parameter without a default position: the spot does nothing until both points are picked.</summary>
    private static ParamDef Point(string id, string label, string link) => new ParamDef
    {
        Id = id, Label = label, Kind = ParamKind.Point, DefaultText = "", MarkerRadius = "size", MarkerLink = link
    };

    private static ParamDef Tip(ParamDef d, string tip) { d.Tooltip = tip; return d; }

    public static bool IsIdentity(FilterParams p)
    {
        if (!p.GetPoint("target", out var tx, out var ty) || !p.GetPoint("source", out var sx, out var sy)) return true;
        if (p.Get("opacity") < 0.5) return true;
        return Math.Abs(tx - sx) < 1e-6 && Math.Abs(ty - sy) < 1e-6;
    }

    public static void Render(PluginImage img, FilterParams p, RenderContext ctx)
    {
        if (IsIdentity(p)) return;
        p.GetPoint("target", out var tx, out var ty);
        p.GetPoint("source", out var sx, out var sy);
        bool heal = !string.Equals(p.GetText("mode"), "clone", StringComparison.Ordinal);
        Apply(img, tx, ty, sx, sy, p.Get("size"), p.Get("feather"), heal, p.Get("opacity") / 100.0);
    }

    /// <summary>
    /// Repairs one spot in place. Points are fractions of the image (0..1), <paramref name="sizePct"/> the radius in % of
    /// the long side, <paramref name="featherPct"/> the soft edge in % of the radius.
    /// </summary>
    public static void Apply(PluginImage img, double tx, double ty, double sx, double sy, double sizePct, double featherPct, bool heal, double opacity)
    {
        int w = img.Width, h = img.Height;
        double R = Math.Max(1.0, sizePct / 100.0 * Math.Max(w, h));
        double fe = Clamp(featherPct / 100.0, 0, 1);
        double rIn = R * (1 - fe);
        float op = (float)Clamp(opacity, 0, 1);
        double cx = tx * w, cy = ty * h;
        double dx = sx * w - cx, dy = sy * h - cy;
        if (op <= 0f || dx * dx + dy * dy < 1e-6) return;

        // Border correction per channel: Fourier series of ln(target / source) on the circle of radius R.
        var planes = new[] { img.R, img.G, img.B };
        var coef = new double[3][];
        if (heal)
        {
            for (int c = 0; c < 3; c++) coef[c] = BorderSeries(planes[c], w, h, cx, cy, dx, dy, R);
        }

        int x0 = Math.Max(0, (int)Math.Floor(cx - R)), x1 = Math.Min(w - 1, (int)Math.Ceiling(cx + R));
        int y0 = Math.Max(0, (int)Math.Floor(cy - R)), y1 = Math.Min(h - 1, (int)Math.Ceiling(cy + R));
        if (x0 > x1 || y0 > y1) return;
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;

        // All reads come from the untouched planes, the result is written afterwards (source and spot may overlap).
        var outR = new float[bw * bh];
        var outG = new float[bw * bh];
        var outB = new float[bw * bh];
        var alpha = new float[bw * bh];
        Parallel.For(0, bh, yy =>
        {
            int y = y0 + yy;
            double py = y + 0.5 - cy;
            var zr = new double[Terms + 1];
            var zi = new double[Terms + 1];
            for (int xx = 0; xx < bw; xx++)
            {
                int x = x0 + xx;
                double px = x + 0.5 - cx;
                double d = Math.Sqrt(px * px + py * py);
                if (d >= R) continue;
                double a = d <= rIn ? 1.0 : SmoothFall((d - rIn) / Math.Max(1e-9, R - rIn));
                if (a <= 0) continue;
                int o = yy * bw + xx;
                alpha[o] = (float)a * op;
                double qx = x + 0.5 + dx, qy = y + 0.5 + dy;
                float sr = Sample(img.R, w, h, qx, qy), sg = Sample(img.G, w, h, qx, qy), sb = Sample(img.B, w, h, qx, qy);
                if (heal)
                {
                    // z^n = (rho e^{i theta})^n: harmonic interpolation of the border series inside the disk.
                    double ux = px / R, uy = py / R;
                    zr[0] = 1; zi[0] = 0;
                    for (int n = 1; n <= Terms; n++)
                    {
                        zr[n] = zr[n - 1] * ux - zi[n - 1] * uy;
                        zi[n] = zr[n - 1] * uy + zi[n - 1] * ux;
                    }
                    sr = (float)(sr * Math.Exp(Eval(coef[0], zr, zi)));
                    sg = (float)(sg * Math.Exp(Eval(coef[1], zr, zi)));
                    sb = (float)(sb * Math.Exp(Eval(coef[2], zr, zi)));
                }
                outR[o] = sr; outG[o] = sg; outB[o] = sb;
            }
        });

        for (int yy = 0; yy < bh; yy++)
        {
            int row = (y0 + yy) * w + x0;
            for (int xx = 0; xx < bw; xx++)
            {
                int o = yy * bw + xx;
                float a = alpha[o];
                if (a <= 0f) continue;
                int i = row + xx;
                img.R[i] += (outR[o] - img.R[i]) * a;
                img.G[i] += (outG[o] - img.G[i]) * a;
                img.B[i] += (outB[o] - img.B[i]) * a;
            }
        }
    }

    /// <summary>[a0, a1, b1, a2, b2, ...] of ln(target / source) sampled on the spot border (averaged over a 3 px band).</summary>
    private static double[] BorderSeries(float[] plane, int w, int h, double cx, double cy, double dx, double dy, double R)
    {
        const double Eps = 1e-4;
        var v = new double[Samples];
        for (int k = 0; k < Samples; k++)
        {
            double th = 2 * Math.PI * k / Samples;
            double ux = Math.Cos(th), uy = Math.Sin(th);
            double t = 0, s = 0;
            for (int j = -1; j <= 1; j++)
            {
                double r = Math.Max(0.5, R + j);
                double px = cx + ux * r, py = cy + uy * r;
                t += Sample(plane, w, h, px, py);
                s += Sample(plane, w, h, px + dx, py + dy);
            }
            v[k] = Math.Log(Math.Max(t / 3, Eps)) - Math.Log(Math.Max(s / 3, Eps));
        }
        var c = new double[1 + 2 * Terms];
        for (int k = 0; k < Samples; k++) c[0] += v[k];
        c[0] /= Samples;
        for (int n = 1; n <= Terms; n++)
        {
            double a = 0, b = 0;
            for (int k = 0; k < Samples; k++)
            {
                double th = 2 * Math.PI * k * n / Samples;
                a += v[k] * Math.Cos(th);
                b += v[k] * Math.Sin(th);
            }
            // Lanczos sigma factor: damps the ringing of the truncated series.
            double sig = n == Terms + 1 ? 0 : Math.Sin(Math.PI * n / (Terms + 1)) / (Math.PI * n / (Terms + 1));
            c[2 * n - 1] = 2.0 / Samples * a * sig;
            c[2 * n] = 2.0 / Samples * b * sig;
        }
        return c;
    }

    private static double Eval(double[] c, double[] zr, double[] zi)
    {
        double s = c[0];
        for (int n = 1; n <= Terms; n++) s += c[2 * n - 1] * zr[n] + c[2 * n] * zi[n];
        return s;
    }

    /// <summary>Bilinear sample at continuous coordinates (pixel centres at +0.5), clamped to the image.</summary>
    private static float Sample(float[] p, int w, int h, double x, double y)
    {
        x = Clamp(x - 0.5, 0, w - 1);
        y = Clamp(y - 0.5, 0, h - 1);
        int ix = (int)x, iy = (int)y;
        int ix1 = Math.Min(ix + 1, w - 1), iy1 = Math.Min(iy + 1, h - 1);
        float fx = (float)(x - ix), fy = (float)(y - iy);
        float a = p[iy * w + ix] + (p[iy * w + ix1] - p[iy * w + ix]) * fx;
        float b = p[iy1 * w + ix] + (p[iy1 * w + ix1] - p[iy1 * w + ix]) * fx;
        return a + (b - a) * fy;
    }

    private static double SmoothFall(double t)
    {
        t = Clamp(t, 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    public static string FormatPoint(double x, double y) =>
        x.ToString("0.#####", CultureInfo.InvariantCulture) + "," + y.ToString("0.#####", CultureInfo.InvariantCulture);
}
