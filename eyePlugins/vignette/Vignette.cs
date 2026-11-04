using System;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.Vignette;

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
//  Vignette: Output stage (post-crop), point parameter
// ---------------------------------------------------------------------

public sealed class VignettePlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.vignette",
        Name = "Post-crop vignette",
        Version = "2.0",
        Author = "PhotographerEye",
        Description = "Darkens or lightens the frame edges after crop. The center can be picked on the photo."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddFilter(new FilterDescriptor
        {
            Id = "vignette",
            Label = "Vignette",
            Group = "Effects",
            Stage = FilterStage.Output,
            SupportsMask = true,
            Params = new[]
            {
                ParamDef.Slider("amount", "Amount", -100, 100, 0),
                ParamDef.Slider("size", "Midpoint", 0, 100, 50),
                ParamDef.Slider("feather", "Feather", 0, 100, 50),
                ParamDef.Point("center", "Center")
            },
            IsIdentity = p => Math.Abs(p.Get("amount")) < 0.5,
            Render = (img, p, ctx) =>
            {
                float amount = (float)(p.Get("amount") / 100.0);
                double mid = 0.25 + 0.6 * p.Get("size") / 100.0;
                double feather = 0.05 + 0.9 * p.Get("feather") / 100.0;
                p.GetPoint("center", out var cx, out var cy);
                int w = img.Width, h = img.Height;
                double aspect = (double)w / h;
                Parallel.For(0, h, y =>
                {
                    int row = y * w;
                    double dy = ((y + 0.5) / h - cy) * 2;
                    for (int x = 0; x < w; x++)
                    {
                        double dx = ((x + 0.5) / w - cx) * 2 * Math.Sqrt(aspect);
                        double d = Math.Sqrt(dx * dx + dy * dy / Math.Sqrt(aspect) * Math.Sqrt(aspect) / aspect) / Math.Sqrt(2);
                        float t = U.Smooth((float)((d - mid + feather / 2) / feather));
                        int i = row + x;
                        if (amount < 0)
                        {
                            float k = 1f + amount * t;
                            img.R[i] *= k;
                            img.G[i] *= k;
                            img.B[i] *= k;
                        }
                        else
                        {
                            float k = amount * t;
                            img.R[i] += (1f - img.R[i]) * k;
                            img.G[i] += (1f - img.G[i]) * k;
                            img.B[i] += (1f - img.B[i]) * k;
                        }
                    }
                });
            }
        });
    }
}
