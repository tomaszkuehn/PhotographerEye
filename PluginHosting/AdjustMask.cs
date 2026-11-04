using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>
/// Mask for the main Adjustments panel (STANDARD / DETAIL sliders). The panel has two sets of values: the whole-photo
/// ones (everywhere) and the mask's own (<see cref="MaskSpec.Adjust"/>, added to the whole-photo ones inside the mask).
/// Stored in <see cref="PluginEditState.Masks"/> under <see cref="Key"/>, so undo, history and .eye files keep it.
/// The photo is rendered with the whole-photo values and with the combined values, and the two are blended by the
/// mask, which is computed on the whole-photo render (Display stage, sRGB). R/G/B curves stay whole-photo.
/// </summary>
public static class AdjustMask
{
    public const string Key = BuiltInFilters.CoreId + ":adjustments";
    public const string Label = "Adjustments";

    public static MaskSpec? For(PluginEditState state) =>
        state.Masks.TryGetValue(Key, out var m) ? state.Resolve(m) : null;

    /// <summary>The mask's own slider values (stored on the Adjustments mask itself, also when it is a link), or null.</summary>
    public static Dictionary<string, double>? Values(PluginEditState state) =>
        state.Masks.TryGetValue(Key, out var m) && m.Adjust is { Count: > 0 } a ? a : null;

    /// <summary>Whole-photo value plus the mask's value of one slider, clamped to the slider range.</summary>
    public static double Combine(CoreParam p, double global, Dictionary<string, double>? local) =>
        local is not null && local.TryGetValue(p.Key, out var l) ? Math.Clamp(global + l, p.Min, p.Max) : global;

    private static float[]? Render(MaskSpec spec, PluginImage img, PluginRenderJob job, int fullW, int fullH)
    {
        var ctx = new RenderContext
        {
            Stage = FilterStage.Display,
            Space = PluginColorSpace.Srgb,
            IsPreview = job.IsPreview,
            Scale = job.Scale,
            FullWidth = fullW > 0 ? fullW : img.Width,
            FullHeight = fullH > 0 ? fullH : img.Height,
            Meta = job.Meta,
            Cancel = job.Cancel
        };
        try
        {
            return MaskRender.Render(spec, img, ctx, job.Registry);
        }
        catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            job.Report.Errors.Add($"{Label}: mask failed ({ex.Message})");
            return null;
        }
    }

    /// <summary>neutral + mask * (full - neutral). Returns <paramref name="full"/> when the mask covers everything.</summary>
    public static Raw16Image Blend16(Raw16Image full, Raw16Image neutral, MaskSpec spec, PluginRenderJob job, int fullW, int fullH)
    {
        if (full.W != neutral.W || full.H != neutral.H) return full;
        var m = Render(spec, PluginPipeline.FromRaw16(neutral), job, fullW, fullH);
        if (m is null) return full;
        int w = full.W;
        var dst = new Raw16Image(w, full.H);
        Parallel.For(0, full.H, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++)
            {
                float a = Math.Clamp(m[i], 0f, 1f);
                dst.R[i] = Mix(neutral.R[i], full.R[i], a);
                dst.G[i] = Mix(neutral.G[i], full.G[i], a);
                dst.B[i] = Mix(neutral.B[i], full.B[i], a);
            }
        });
        return dst;
    }

    /// <summary>8-bit variant of <see cref="Blend16"/> (BGRA, alpha from <paramref name="full"/>).</summary>
    public static BitmapSource Blend8(BitmapSource full, BitmapSource neutral, MaskSpec spec, PluginRenderJob job, int fullW, int fullH)
    {
        if (full.PixelWidth != neutral.PixelWidth || full.PixelHeight != neutral.PixelHeight) return full;
        var img = PluginPipeline.FromBitmap(neutral, out var nb, out int ns);
        var m = Render(spec, img, job, fullW, fullH);
        if (m is null) return full;
        int w = full.PixelWidth, h = full.PixelHeight, stride = w * 4;
        var fb = Bgra(full);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float a = Math.Clamp(m[y * w + x], 0f, 1f);
                int o = y * stride + x * 4, on = y * ns + x * 4;
                for (int c = 0; c < 3; c++)
                    fb[o + c] = (byte)Math.Clamp((int)(nb[on + c] + (fb[o + c] - nb[on + c]) * a + 0.5f), 0, 255);
            }
        });
        var bmp = BitmapSource.Create(w, h, full.DpiX, full.DpiY, PixelFormats.Bgra32, null, fb, stride);
        bmp.Freeze();
        return bmp;
    }

    private static ushort Mix(ushort n, ushort f, float a) => (ushort)Math.Clamp((int)(n + (f - n) * a + 0.5f), 0, 65535);

    private static byte[] Bgra(BitmapSource src)
    {
        BitmapSource s = src;
        if (s.Format != PixelFormats.Bgra32)
        {
            var c = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            c.Freeze();
            s = c;
        }
        var buf = new byte[s.PixelWidth * 4 * s.PixelHeight];
        s.CopyPixels(buf, s.PixelWidth * 4, 0);
        return buf;
    }
}
