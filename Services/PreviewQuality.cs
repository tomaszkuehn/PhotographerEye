using System;

namespace ImgViewer.Services;

/// <summary>
/// Resolution of the live RAW preview. Edits are rendered on a copy as large as the screen shows (times a factor);
/// export and 100 % zoom still use the full image.
/// </summary>
public static class PreviewQuality
{
    public const string Window = "window";
    public const string Sharp = "sharp";
    public const string Full = "full";

    public static string Normalize(string? q) => q is Sharp or Full ? q : Window;

    /// <summary>Screen pixels per image pixel the preview keeps: 1 for Window, 2 for Sharp (0 = full image).</summary>
    public static double Factor(string? q) => Normalize(q) switch
    {
        Sharp => 2.0,
        Full => 0.0,
        _ => 1.0
    };

    /// <summary>Granularity of preview sizes, so small zoom steps reuse the same copy.</summary>
    public const int Step = 256;

    /// <summary>
    /// Longer side the preview buffer needs, or 0 for the full image. <paramref name="boxLong"/> is the longer side of
    /// the photo in viewer (native) pixels, <paramref name="zoom"/> screen pixels per native pixel, and
    /// <paramref name="srcLong"/> the longer side of the decoded buffer.
    /// </summary>
    public static int TargetLong(string? q, double boxLong, double zoom, int srcLong)
    {
        double f = Factor(q);
        if (f <= 0 || boxLong <= 0 || srcLong <= 0 || double.IsNaN(zoom) || zoom <= 0) return 0;
        // One buffer pixel per screen pixel the whole photo spans (times the factor).
        double needed = boxLong * Math.Min(zoom, 16.0) * f;
        int t = (int)Math.Ceiling(Math.Max(needed, 1024) / Step) * Step;
        // Close to the full size the copy saves little and costs a resample.
        return t >= srcLong * 0.8 ? 0 : t;
    }
}
