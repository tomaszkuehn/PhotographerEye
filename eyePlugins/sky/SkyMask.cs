using System;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.SkyMask;

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
//  Sky mask provider (hue + brightness range)
// ---------------------------------------------------------------------

public sealed class SkyMaskPlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.sky",
        Name = "Sky mask",
        Version = "2.0",
        Author = "PhotographerEye",
        Description = "Mask provider selecting bright blue areas (usable with any maskable filter)."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddMaskProvider(new MaskProviderDescriptor
        {
            Id = "sky",
            Label = "Sky (blue hue)",
            Params = new[]
            {
                ParamDef.Slider("hue", "Hue", 170, 260, 215),
                ParamDef.Slider("tolerance", "Tolerance", 10, 90, 35),
                ParamDef.Slider("brightness", "Min brightness", 0, 100, 30)
            },
            CreateMask = (img, p, ctx) =>
            {
                float hue = (float)p.Get("hue"), tol = (float)p.Get("tolerance"), minV = (float)(p.Get("brightness") / 100.0);
                bool lin = ctx.Space == PluginColorSpace.LinearSrgb;
                var m = new float[img.Length];
                Parallel.For(0, img.Height, y =>
                {
                    int row = y * img.Width;
                    for (int i = row; i < row + img.Width; i++)
                    {
                        float r = img.R[i], g = img.G[i], b = img.B[i];
                        if (lin) { r = (float)Math.Pow(U.Clamp01(r), 1 / 2.2); g = (float)Math.Pow(U.Clamp01(g), 1 / 2.2); b = (float)Math.Pow(U.Clamp01(b), 1 / 2.2); }
                        U.Rgb2Hsv(r, g, b, out var h, out var s, out var v);
                        float hw = 1f - U.Smooth(U.HueDistance(h, hue) / tol);
                        float vw = U.Smooth((v - minV) / 0.15f + 0.5f);
                        float sw = U.Smooth(s * 6f);
                        m[i] = hw * vw * sw;
                    }
                });
                return m;
            }
        });
    }
}
