using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Services;
using Sdcb.LibRaw;

namespace ImgViewer.Imaging;

public static class RawDecoder
{
    private static readonly string[] RawExtensions =
    {
        ".rw2", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".cr2", ".cr3", ".crw",
        ".orf", ".raf", ".pef", ".ptx", ".dng", ".srw", ".x3f", ".3fr", ".fff", ".iiq", ".mos", ".erf", ".kdc", ".dcr", ".mef", ".rwl"
    };

    private static readonly object DecodeLock = new();
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsRaw(string path) =>
        RawExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static BitmapSource Decode(string path, bool halfSize)
    {
        lock (DecodeLock)
        {
            var entry = Cache.GetOrAdd(path, _ => new CacheEntry());
            entry.Touch();

            var cached = halfSize ? entry.Half : entry.Full;
            if (cached is not null) return cached;

            using var r = RawContext.OpenFile(path);
            r.Unpack();
            r.DcrawProcess(p =>
            {
                p.HalfSize = halfSize;
                p.UseCameraWb = true;
                p.OutputBps = 8;
                p.OutputTiff = false;
            });
            using var img = r.MakeDcrawMemoryImage();

            if (img.ImageType != ProcessedImageType.Bitmap || img.Bits != 8 || img.Channels != 3)
                throw new InvalidOperationException($"LibRaw: nieoczekiwany format wyjściowy ({img.ImageType}, {img.Bits} bit, {img.Channels} kanal.)");

            int w = img.Width;
            int h = img.Height;
            var src = img.AsSpan<byte>();
            var bgra = new byte[(long)w * h * 4];
            int si = 0, di = 0;
            while (di < bgra.Length)
            {
                bgra[di] = src[si + 2];
                bgra[di + 1] = src[si + 1];
                bgra[di + 2] = src[si];
                bgra[di + 3] = 255;
                si += 3;
                di += 4;
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            bmp.Freeze();
            if (halfSize) entry.Half = bmp; else entry.Full = bmp;
            Evict(path);
            Debug.Log($"RAW DECODE: {Path.GetFileName(path)} half={halfSize} {w}x{h}");
            return bmp;
        }
    }

    public static ImageInfo GetInfo(string path)
    {
        var half = Decode(path, halfSize: true);
        return new ImageInfo(half.PixelWidth, half.PixelHeight, 1);
    }

    public static BitmapSource LoadScaled(string path, ImageInfo info, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / info.Width, (double)maxHeight / info.Height));
        if (scale < 0.02) scale = 0.02;

        int dw = Math.Min(info.Width, Math.Max(1, (int)Math.Round(info.Width * scale)));
        int dh = Math.Min(info.Height, Math.Max(1, (int)Math.Round(info.Height * scale)));

        bool halfSize = !HasFull(path) && dw < info.Width * 0.55;
        var source = Decode(path, halfSize);
        if (source.PixelWidth == dw && source.PixelHeight == dh) return source;
        return Resize(source, dw, dh);
    }

    private static bool HasFull(string path) => Cache.TryGetValue(path, out var e) && e.Full is not null;

    private static BitmapSource Resize(BitmapSource source, int dw, int dh)
    {
        if (dw >= source.PixelWidth && dh >= source.PixelHeight) return source;

        var t = new System.Windows.Media.ScaleTransform(dw / (double)source.PixelWidth, dh / (double)source.PixelHeight);
        t.Freeze();
        var tb = new TransformedBitmap();
        tb.BeginInit();
        tb.Source = source;
        tb.Transform = t;
        tb.EndInit();
        tb.Freeze();
        return tb;
    }

    private static void Evict(string keepPath)
    {
        var entries = Cache.ToList();
        if (entries.Count <= 6) return;
        foreach (var e in entries.OrderBy(p => p.Value.LastUse).Take(entries.Count - 6))
        {
            if (!string.Equals(e.Key, keepPath, StringComparison.OrdinalIgnoreCase))
                Cache.TryRemove(e.Key, out _);
        }
    }

    private sealed class CacheEntry
    {
        public BitmapSource? Half;
        public BitmapSource? Full;
        public long LastUse;
        public void Touch() => LastUse = Environment.TickCount64;
    }
}