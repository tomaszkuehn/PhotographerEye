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
    public string? IsoText;
    public string? ShutterText;

    public string Summary =>
        string.Join(" \u2022 ", new[] { Camera, DateText, FocalText, ApertureText, IsoText, ShutterText }
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
            return RawDecoder.IsRaw(path) ? ReadRaw(path) : HeifDecoder.IsHeif(path) ? ReadHeif(path) : ReadExif(path);
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
            if (o.IsoSpeed > 0.5 && o.IsoSpeed < 1_000_000) m.IsoText = "ISO " + ((int)Math.Round(o.IsoSpeed)).ToString();
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
            if (TryInt(md.GetQuery("/app1/ifd/exif/{ushort=34855}"), out int iso) && iso > 0 && iso < 1_000_000) m.IsoText = "ISO " + iso.ToString();

            return Has(m) ? m : null;
        }
        catch (Exception ex)
        {
            Debug.Log($"META EXIF: {Path.GetFileName(path)} [{ex.GetType().Name}] {ex.Message}");
            return null;
        }
    }

    private static PhotoMeta? ReadHeif(string path)
    {
        try
        {
            var block = ImgViewer.Imaging.HeifDecoder.ExifBytes(path);
            var (_, m) = ParseTiff(block);
            return Has(m) ? m : null;
        }
        catch (Exception ex)
        {
            Debug.Log($"META HEIF: {Path.GetFileName(path)} [{ex.GetType().Name}] {ex.Message}");
            return null;
        }
    }

    internal static (int Orient, PhotoMeta? Meta) ParseTiff(byte[]? block)
    {
        if (block is null || block.Length < 8) return (1, null);
        byte[] data = block;
        int start = -1;
        for (int i = 0; i < Math.Min(16, data.Length - 3); i++)
        {
            if ((data[i] == (byte)'I' && data[i + 1] == (byte)'I' && (data[i + 2] == 42 || data[i + 3] == 0 && data[i + 2] == (byte)'U')) ||
                (data[i] == (byte)'M' && data[i + 1] == (byte)'M' && data[i + 2] == 0))
            {
                start = i;
                break;
            }
        }
        if (start < 0) return (1, null);
        bool le = data[start] == (byte)'I';

        ushort U16(int o) => le
            ? (ushort)(data[o] | (data[o + 1] << 8))
            : (ushort)(data[o] << 8 | data[o + 1]);
        uint U32(int o) => le
            ? (uint)(data[o] | data[o + 1] << 8 | data[o + 2] << 16 | data[o + 3] << 24)
            : (uint)(data[o] << 24 | data[o + 1] << 16 | data[o + 2] << 8 | data[o + 3]);

        var m = new PhotoMeta();
        int orient = 1;
        bool inExifIfd = false;
        int exifIfdOff = -1;

        string? Ascii(int off, int size)
        {
            int len = size;
            int z = Array.IndexOf(data, (byte)0, off, len);
            if (z >= 0) len = z - off;
            if (len <= 0) return null;
            return Encoding.ASCII.GetString(data, off, len);
        }
        int first = (int)U32(start + 4);
        for (int pass = 0; pass < 2 && first > 0; pass++)
        {
            int off = start + first;
            if (off + 2 > data.Length) break;
            int n = U16(off);
            for (int i = 0; i < n; i++)
            {
                int e = off + 2 + i * 12;
                if (e + 12 > data.Length) break;
                ushort tag = U16(e);
                ushort type = U16(e + 2);
                uint cnt = U32(e + 4);
                int unit = type switch { 1 => 1, 2 => 1, 3 => 2, 4 => 4, 5 => 8, 6 => 1, 7 => 1, 8 => 2, 9 => 4, 10 => 8, 11 => 4, 12 => 8, _ => 0 };
                if (unit == 0) continue;
                int size = unit * (int)cnt;
                if (size <= 0) continue;
                int val = size <= 4 ? e + 8 : (int)U32(e + 8);
                if (val + size > data.Length) continue;

                switch (tag)
                {
                    case 271:
                        m.Camera = Clean(Ascii(val, size)) ?? m.Camera;
                        break;
                    case 272:
                    {
                        string? model = Clean(Ascii(val, size));
                        if (!string.IsNullOrEmpty(model))
                            m.Camera = !string.IsNullOrEmpty(m.Camera) && model.IndexOf(m.Camera, StringComparison.OrdinalIgnoreCase) < 0
                                ? m.Camera + " " + model
                                : model;
                        break;
                    }
                    case 274:
                        if (type == 3) orient = U16(val);
                        else if (type == 4) orient = (int)U32(val);
                        break;
                    case 34665:
                        if (!inExifIfd) exifIfdOff = (int)U32(val) > 0 ? (int)U32(val) : -1;
                        break;
                    case 306:
                    {
                        string? s = Clean(Ascii(val, size));
                        if (s is not null && DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var dt))
                            m.DateText = dt.ToString("yyyy-MM-dd HH:mm");
                        break;
                    }
                    case 36867:
                    {
                        string? s = Clean(Ascii(val, size));
                        if (s is not null && DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var dt2))
                            m.DateText = dt2.ToString("yyyy-MM-dd HH:mm");
                        break;
                    }
                    case 33434 when type == 5:
                        m.ShutterText = FormatShutter(U32(val) / (double)U32(val + 4));
                        break;
                    case 33437 when type == 5:
                    {
                        double fn = U32(val) / (double)U32(val + 4);
                        if (fn > 0.05 && fn < 350) m.ApertureText = "f/" + FormatNumber(fn);
                        break;
                    }
                    case 37386 when type == 5:
                    {
                        double fl = U32(val) / (double)U32(val + 4);
                        if (fl > 0.05 && fl < 6500) m.FocalText = FormatNumber(fl) + " mm";
                        break;
                    }
                    case 34855:
                    {
                        int iso = type == 3 ? U16(val) : (int)U32(val);
                        if (iso > 0 && iso < 1_000_000) m.IsoText = "ISO " + iso.ToString();
                        break;
                    }
                }
            }
            if (exifIfdOff > 0)
            {
                first = exifIfdOff;
                exifIfdOff = -1;
                inExifIfd = true;
            }
            else break;
        }
        return (orient, Has(m) ? m : null);
    }

    private static bool Has(PhotoMeta? m) =>
        m?.Camera is not null || m?.DateText is not null || m?.FocalText is not null ||
        m?.ApertureText is not null || m?.IsoText is not null || m?.ShutterText is not null;

    private static bool TryInt(object? v, out int val)
    {
        val = 0;
        try
        {
            switch (v)
            {
                case ushort u:
                    val = u;
                    return true;
                case short s:
                    val = s;
                    return true;
                case uint ui:
                    val = (int)ui;
                    return true;
                case int i:
                    val = i;
                    return true;
                case byte by:
                    val = by;
                    return true;
                case double d:
                    val = (int)d;
                    return true;
                case float f:
                    val = (int)f;
                    return true;
                case byte[] b when b.Length >= 2:
                    val = BitConverter.ToUInt16(b, 0);
                    if (b.Length >= 4 && val == 0) val = (int)BitConverter.ToUInt32(b, 0);
                    return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    private static string? Clean(string? s)
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