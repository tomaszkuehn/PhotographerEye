using System;
using System.Collections.Generic;
using System.Globalization;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.ExposureInfo;

// ---------------------------------------------------------------------
//  Metadata provider: EV100, megapixels, 35 mm hint
// ---------------------------------------------------------------------

public sealed class ExposureInfoPlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.exposure",
        Name = "Exposure info",
        Version = "2.0",
        Author = "PhotographerEye",
        Description = "Adds EV100 and megapixels to the photo info tooltip, a clipping overlay and an expose-to-the-right command."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddOverlay(new OverlayDescriptor
        {
            Id = "clipping",
            Label = "Clipped highlights and shadows",
            Draw = req =>
            {
                var px = req.Rendered?.Invoke();
                return px?.Image == null ? null : new OverlayContent { Bitmap = ExposureTools.ClippingMap(px.Image) };
            }
        });
        host.AddCommand(new CommandDescriptor
        {
            Id = "ettr",
            Label = "Expose to the right",
            Placement = CommandPlacement.Menu,
            Execute = c =>
            {
                if (string.IsNullOrEmpty(c.CurrentPath)) { c.SetStatus("No photo open"); return; }
                var hist = c.GetHistogram();
                if (hist == null) { c.SetStatus("No histogram yet, try again after the preview is shown"); return; }
                double delta = ExposureTools.EttrDelta(hist, out string why);
                if (delta == 0) { c.SetStatus(why); return; }
                var range = c.GetCoreRange("Exposure");
                double ev = Math.Min(range.Max, Math.Max(range.Min, Math.Round((c.GetCore("Exposure") + delta) * 100) / 100));
                c.SetCore("Exposure", ev);
                c.SetStatus(string.Format(CultureInfo.InvariantCulture, "Expose to the right: {0:+0.00;-0.00} EV ({1}), exposure {2:+0.00;-0.00;0}", delta, why, ev));
            }
        });
        host.AddMetadataProvider(new MetadataProviderDescriptor
        {
            Id = "ev",
            Label = "Exposure",
            Provide = m =>
            {
                var list = new List<KeyValuePair<string, string>>();
                if (m.Aperture > 0 && m.ShutterSeconds > 0)
                {
                    double ev = Math.Log(m.Aperture.Value * m.Aperture.Value / m.ShutterSeconds.Value, 2);
                    if (m.Iso > 0) ev -= Math.Log(m.Iso.Value / 100.0, 2);
                    list.Add(new KeyValuePair<string, string>("EV100", ev.ToString("0.0", CultureInfo.InvariantCulture)));
                }
                if (m.Width > 0 && m.Height > 0)
                    list.Add(new KeyValuePair<string, string>("Megapixels", (m.Width * (double)m.Height / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MP"));
                return list;
            }
        });
    }
}

// ---------------------------------------------------------------------
//  Clipping map and expose-to-the-right
// ---------------------------------------------------------------------

public static class ExposureTools
{
    /// <summary>A channel at or above this (sRGB 0..1) counts as clipped highlight.</summary>
    public const float HighClip = 0.996f;
    /// <summary>All channels at or below this count as clipped shadow.</summary>
    public const float LowClip = 0.004f;
    /// <summary>Brightest target for expose to the right (sRGB code value).</summary>
    public const double Target = 250;

    /// <summary>RGBA map of an sRGB image: red where a channel clips in the highlights, blue where the shadows clip, transparent elsewhere.</summary>
    public static OverlayBitmap ClippingMap(PluginImage img)
    {
        int n = img.Length;
        var px = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            float r = img.R[i], g = img.G[i], b = img.B[i];
            int o = i * 4;
            if (r >= HighClip || g >= HighClip || b >= HighClip) { px[o] = 255; px[o + 1] = 0; px[o + 2] = 0; px[o + 3] = 200; }
            else if (r <= LowClip && g <= LowClip && b <= LowClip) { px[o] = 0; px[o + 1] = 90; px[o + 2] = 255; px[o + 3] = 200; }
        }
        return new OverlayBitmap { Width = img.Width, Height = img.Height, Rgba = px };
    }

    /// <summary>
    /// Exposure change (EV, -3..3) that brings the 99.5th percentile of the brightest channel to <see cref="Target"/>;
    /// -1/3 EV when more than 0.5 % of a channel is already at 255. Repeating it refines the result (tone curves are
    /// not linear). Returns 0 with the reason when nothing should change.
    /// </summary>
    public static double EttrDelta(PhotoHistogram h, out string why)
    {
        var chans = new[] { h.R, h.G, h.B };
        double total = 0;
        foreach (var v in chans[0] ?? h.Luma ?? new int[0]) total += v;
        if (total <= 0) { why = "Empty histogram"; return 0; }
        double clipped = 0;
        int top = 0;
        foreach (var c in chans)
        {
            if (c == null || c.Length < 256) continue;
            clipped = Math.Max(clipped, c[255] / total);
            double acc = 0;
            for (int k = 0; k < 256; k++)
            {
                acc += c[k];
                if (acc >= 0.995 * total) { top = Math.Max(top, k); break; }
            }
        }
        if (clipped > 0.005)
        {
            why = string.Format(CultureInfo.InvariantCulture, "{0:0.0}% clipped", clipped * 100);
            return -1.0 / 3.0;
        }
        if (top <= 0) { why = "Photo is black"; return 0; }
        double delta = Math.Log(SrgbDecode(Target / 255.0) / SrgbDecode(top / 255.0), 2);
        delta = Math.Min(3, Math.Max(-3, delta));
        if (Math.Abs(delta) < 0.02) { why = "Already exposed to the right"; return 0; }
        why = string.Format(CultureInfo.InvariantCulture, "brightest at {0}", top);
        return Math.Round(delta, 2);
    }

    public static double SrgbDecode(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
}
