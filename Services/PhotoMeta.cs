using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using ImgViewer.Imaging;
using Sdcb.LibRaw;

namespace ImgViewer.Services;

public sealed class PhotoMeta
{
    public string? Camera;
    public string? DateText;
    public string? FocalText;
    public string? ApertureText;
    public string? ShutterText;

    public string Summary =>
        string.Join("  •  ", new[] { Camera, DateText, FocalText, ApertureText, ShutterText }
            .Where(s => s is not null).Select(s => s!));
}

public static class PhotoMetaReader
{
    private static readonly ConcurrentDictionary<string, PhotoMeta?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static PhotoMeta? Get(string path)
    {
        try
        {
            return Cache.GetOrAdd(path, Read);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PhotoMeta? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return RawDecoder.IsRaw(path) ? ReadRaw(path) : ReadExif(path);
        }
        catch (Exception ex)
        {
            Debug.Log($"META FAIL: {Path.GetFileName(path)} [{ex.GetType().Name}] {ex.Message}");
            return null;
        }
    }

    private static PhotoMeta? ReadRaw(string path)
    {
        try
        {
            using var r = RawContext.OpenFile(path);
            var m = new PhotoMeta();
            var p = r.ImageParams;
            string? make = Clean(p.NormalizedMake) ?? Clean(p.Make);
            string? model = Clean(p.NormalizedModel) ?? Clean(p.Model);
            if (!string.IsNullOrEmpty(model))
            {
                string cam = !string.IsNullOrEmpty(make) && model.IndexOf(make, StringComparison.OrdinalIgnoreCase) < 0
                    ? make + " " + model
                    : model;
                m.Camera = cam;
            }

            var o = r.ImageOtherParams;
            if (o.Timestamp > 0) m.DateText = DateTimeOffset.FromUnixTimeSeconds(o.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            if (o.FocalLength > 0.05 && o.FocalLength < 6500) m.FocalText = FormatNumber(o.FocalLength) + " mm";
            if (o.Aperture > 0.05 && o.Aperture < 350) m.ApertureText = "f/" + FormatNumber(o.Aperture);
            m.ShutterText = FormatShutter(o.Shutter);
            return Has(m) ? m : null;
        }
        catch (Exception ex)
        {
            Debug.Log($"META RAW: {Path.GetFileName(path)} [{ex.GetType().Name}] {ex.Message}");
            return null;
        }
    }

    private static PhotoMeta? ReadExif(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            var md = dec.Frames[0].Metadata as BitmapMetadata;
            if (md is null) return null;

            var m = new PhotoMeta();
            string? make = AsText(md.GetQuery("/app1/ifd/{ushort=271}"));
            string? model = AsText(md.GetQuery("/app1/ifd/{ushort=272}"));
            if (!string.IsNullOrEmpty(model))
            {
                m.Camera = !string.IsNullOrEmpty(make) && model.IndexOf(make, StringComparison.OrdinalIgnoreCase) < 0
                    ? make + " " + model
                    : model;
            }

            string? taken = AsText(md.GetQuery("/app1/ifd/exif/{ushort=36867}")) ?? AsText(md.GetQuery("/app1/ifd/{ushort=306}"));
            if (taken is not null && DateTime.TryParseExact(taken, "yyyy:MM:dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var dt))
                m.DateText = dt.ToString("yyyy-MM-dd HH:mm");

            if (TryRational(md.GetQuery("/app1/ifd/exif/{ushort=33434}"), out double shutter)) m.ShutterText = FormatShutter(shutter);
            if (TryRational(md.GetQuery("/app1/ifd/exif/{ushort=33437}"), out double fn) && fn > 0.05 && fn < 350) m.ApertureText = "f/" + FormatNumber(fn);
            if (TryRational(md.GetQuery("/app1/ifd/exif/{ushort=37386}"), out double fl) && fl > 0.05 && fl < 6500) m.FocalText = FormatNumber(fl) + " mm";

            return Has(m) ? m : null;
        }
        catch (Exception ex)
        {
            Debug.Log($"META EXIF: {Path.GetFileName(path)} [{ex.GetType().Name}] {ex.Message}");
            return null;
        }
    }

    private static bool Has(PhotoMeta m) =>
        m.Camera is not null || m.DateText is not null || m.FocalText is not null ||
        m.ApertureText is not null || m.ShutterText is not null;

    private static string? Clean(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim('\0').Trim();
        if (s.Length == 0) return null;
        s = s.Replace(" Corporation", "").Replace(" Inc.", "").Replace(" COMPANY, LTD.", "");
        return s.Trim();
    }

    private static string? AsText(object? v)
    {
        switch (v)
        {
            case string s:
                return Clean(s);
            case byte[] b:
                int len = Array.IndexOf(b, (byte)0);
                if (len < 0) len = b.Length;
                if (len == 0) return null;
                return Clean(Encoding.ASCII.GetString(b, 0, len));
            default:
                return null;
        }
    }

    private static bool TryRational(object? v, out double val)
    {
        val = 0;
        try
        {
            switch (v)
            {
                case byte[] b when b.Length >= 8:
                {
                    uint num = BitConverter.ToUInt32(b, 0);
                    uint den = BitConverter.ToUInt32(b, 4);
                    if (den == 0 || num == 0) return false;
                    val = (double)num / den;
                    return true;
                }
                case ulong u:
                {
                    uint num = (uint)(u & 0xFFFFFFFF);
                    uint den = (uint)(u >> 32);
                    if (den == 0 || num == 0) return false;
                    val = (double)num / den;
                    return true;
                }
                case double d:
                    val = d;
                    return true;
                case float f:
                    val = f;
                    return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    private static string FormatNumber(double v)
    {
        if (v >= 10) return v.ToString("0.#");
        if (v >= 1) return v.ToString("0.#");
        return v.ToString("0.##");
    }

    private static string? FormatShutter(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return null;
        if (seconds >= 1) return FormatNumber(seconds) + " s";
        double x = 1.0 / seconds;
        if (x >= 1)
        {
            long r = (long)Math.Round(x);
            if (r >= 1 && Math.Abs(x - r) < Math.Max(0.05, x * 0.02))
                return "1/" + r + " s";
            return seconds.ToString("0.###") + " s";
        }
        return seconds.ToString("0.###") + " s";
    }
}