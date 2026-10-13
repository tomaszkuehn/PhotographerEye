using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Services;
using LibHeifSharp;

namespace ImgViewer.Imaging;

public static class HeifDecoder
{
    private static readonly string[] HeifExtensions = { ".heic", ".heif", ".hif", ".avif" };

    private const int MaxCacheEntries = 3;
    private static readonly object DecodeLock = new();
    private static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Lru = new();

    public static bool IsHeif(string path) =>
        HeifExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static ImageInfo GetInfo(string path)
    {
        using var ctx = new HeifContext(path);
        using var handle = ctx.GetPrimaryImageHandle();
        int w = Math.Max(1, handle.Width);
        int h = Math.Max(1, handle.Height);
        int orient = 1;
        bool transformed = ContainerHasTransforms(path);
        if (!transformed)
        {
            try
            {
                var block = handle.GetExifMetadata();
                (orient, _) = ParseExifBlock(block);
            }
            catch (Exception)
            {
            }
        }
        if (orient is 5 or 6 or 7 or 8)
        {
            (w, h) = (h, w);
        }
        return new ImageInfo(w, h, orient);
    }

    public static byte[]? ExifBytes(string path)
    {
        try
        {
            using var ctx = new HeifContext(path);
            using var handle = ctx.GetPrimaryImageHandle();
            return handle.GetExifMetadata();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static BitmapSource LoadScaled(string path, ImageInfo info, int maxWidth, int maxHeight)
    {
        var full = DecodeFull(path);
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / Math.Max(1, info.Width), (double)maxHeight / Math.Max(1, info.Height)));
        scale = Math.Clamp(scale, 0.02, 1.0);
        if (scale >= 1.0) return full;
        int dw = Math.Max(1, (int)Math.Round(full.PixelWidth * scale));
        int dh = Math.Max(1, (int)Math.Round(full.PixelHeight * scale));
        return ImageLoader.NormalizeDpi(ImageLoader.ScaleTo(full, dw, dh));
    }

    private static BitmapSource DecodeFull(string path)
    {
        lock (DecodeLock)
        {
            if (Cache.TryGetValue(path, out var hit))
            {
                Touch(path);
                return hit;
            }
            var bmp = DecodeImage(path);
            Cache[path] = bmp;
            Touch(path);
            Evict();
            return bmp;
        }
    }

    private static BitmapSource DecodeImage(string path)
    {
        bool transformed = ContainerHasTransforms(path);
        using var ctx = new HeifContext(path);
        using var handle = ctx.GetPrimaryImageHandle();
        int hw = handle.Width;
        int hh = handle.Height;
        var options = new HeifDecodingOptions();
        options.ConvertHdrToEightBit = true;
        using var img = handle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24, options);
        int pw = img.Width > 0 ? img.Width : hw;
        int ph = img.Height > 0 ? img.Height : hh;
        var plane = img.GetPlane(HeifChannel.Interleaved);
        int stride = plane.Stride > 0 ? plane.Stride : pw * 3;
        var bmp = CopyPlaneToBitmap(plane, pw, ph, stride);
        bmp.Freeze();

        if (!transformed && pw == hw && ph == hh)
        {
            int orient = 1;
            try
            {
                var block = handle.GetExifMetadata();
                (orient, _) = ParseExifBlock(block);
            }
            catch (Exception)
            {
            }
            double deg = orient switch
            {
                3 or 4 => 180.0,
                6 => 90.0,
                5 or 8 => 270.0,
                _ => 0.0
            };
            if (deg > 0) bmp = ImageLoader.ApplyRotation(bmp, deg);
        }
        return ImageLoader.NormalizeDpi(bmp);
    }

    private static bool ContainerHasTransforms(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 16) return false;

            int meta = FindTopBox(data, "meta", 0);
            if (meta < 0) return false;
            int metaStart = meta + 12;
            int metaEnd = meta + (int)BoxSize(data, meta);

            int pitm = ChildBox(data, metaStart, metaEnd, "pitm");
            if (pitm < 0) return false;
            int itemId = data[pitm + 8] == 0 ? ReadBe16(data, pitm + 12) : (int)ReadBe32(data, pitm + 12);

            int iprp = ChildBox(data, metaStart, metaEnd, "iprp");
            if (iprp < 0) return false;
            int ipco = ChildBox(data, iprp + 8, iprp + (int)BoxSize(data, iprp), "ipco");
            int ipma = ChildBox(data, iprp + 8, iprp + (int)BoxSize(data, iprp), "ipma");
            if (ipco < 0 || ipma < 0) return false;

            var props = new List<(string Type, int Off)>();
            int p = ipco + 8;
            int ipcoEnd = ipco + (int)BoxSize(data, ipco);
            while (p + 8 <= ipcoEnd && p < ipcoEnd)
            {
                int sz = (int)ReadBe32(data, p);
                string ty = BoxType(data, p);
                if (sz < 8 || p + sz > ipcoEnd) break;
                props.Add((ty, p + 8));
                p += sz;
            }

            int mp = ipma + 12;
            int count = (int)ReadBe32(data, mp);
            mp += 4;
            bool bigIds = data[ipma + 8] != 0;
            bool bigCounts = (data[ipma + 9] & 1) != 0;
            for (int i = 0; i < count; i++)
            {
                if (mp + 2 > data.Length) break;
                int id = bigIds ? (int)ReadBe32(data, mp) : ReadBe16(data, mp);
                mp += bigIds ? 4 : 2;
                if (mp >= data.Length) break;
                int n = bigCounts ? ReadBe16(data, mp) : data[mp];
                mp += bigCounts ? 2 : 1;
                bool matches = id == itemId;
                for (int k = 0; k < n; k++)
                {
                    if (mp + (bigCounts ? 2 : 1) > data.Length) { mp = data.Length + 1; break; }
                    int idx = bigCounts ? ReadBe16(data, mp) & 0x7FFF : data[mp] & 0x7F;
                    mp += bigCounts ? 2 : 1;
                    if (matches && idx >= 1 && idx <= props.Count && props[idx - 1].Type is "irot" or "imir" or "clap")
                        return true;
                }
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int FindTopBox(byte[] d, string type, int start)
    {
        int p = start;
        while (p + 8 <= d.Length)
        {
            int sz = (int)ReadBe32(d, p);
            string ty = BoxType(d, p);
            if (sz == 0) sz = d.Length - p;
            if (sz < 8 || p + sz > d.Length) return -1;
            if (ty == type) return p;
            p += sz;
        }
        return -1;
    }

    private static int ChildBox(byte[] d, int start, int end, string type)
    {
        int p = start;
        while (p + 8 <= end)
        {
            int sz = (int)ReadBe32(d, p);
            string ty = BoxType(d, p);
            if (sz < 8 || p + sz > end) return -1;
            if (ty == type) return p;
            p += sz;
        }
        return -1;
    }

    private static uint BoxSize(byte[] d, int header)
    {
        if (header + 8 > d.Length) return 0;
        uint sz = ReadBe32(d, header);
        if (sz == 1)
        {
            if (header + 16 > d.Length) return 0;
            return (uint)Math.Min((long)ReadBe32(d, header + 8) << 32 | ReadBe32(d, header + 12), int.MaxValue);
        }
        return sz;
    }

    private static string BoxType(byte[] d, int p)
    {
        if (p + 8 > d.Length) return "";
        return $"{(char)d[p + 4]}{(char)d[p + 5]}{(char)d[p + 6]}{(char)d[p + 7]}";
    }

    private static uint ReadBe32(byte[] d, int o) =>
        d.Length < o + 4 ? 0u : (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);

    private static int ReadBe16(byte[] d, int o) =>
        d.Length < o + 2 ? 0 : d[o] << 8 | d[o + 1];

    private static unsafe BitmapSource CopyPlaneToBitmap(LibHeifSharp.HeifPlaneData plane, int pw, int ph, int stride)
    {
        var buf = new byte[(long)stride * ph];
        byte* src = (byte*)plane.Scan0.ToPointer();
        fixed (byte* dst = buf)
        {
            byte* d = dst;
            if (stride == pw * 3)
            {
                Buffer.MemoryCopy(src, d, (long)stride * ph, (long)stride * ph);
            }
            else
            {
                for (int y = 0; y < ph; y++)
                {
                    long off = (long)y * stride;
                    Buffer.MemoryCopy(src + off, d + off, pw * 3, pw * 3);
                }
            }
        }
        return BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Rgb24, null, buf, stride);
    }

    private static (int Orient, PhotoMeta? Meta) ParseExifBlock(byte[]? block)
    {
        return block is null || block.Length < 8 ? (1, null) : PhotoMetaReader.ParseTiff(block);
    }

    private static void Touch(string key)
    {
        Lru.Remove(key);
        Lru.Add(key);
    }

    private static void Evict()
    {
        while (Lru.Count > MaxCacheEntries)
        {
            var key = Lru[0];
            Lru.RemoveAt(0);
            Cache.TryRemove(key, out _);
        }
    }
}