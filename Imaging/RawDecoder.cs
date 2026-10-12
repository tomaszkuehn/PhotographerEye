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

    private const int MaxCacheEntries = 3;

    private static readonly object DecodeLock = new();
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsRaw(string path) =>
        RawExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static BitmapSource Decode(string path, bool halfSize)
    {
        lock (DecodeLock)
        {
            var entry = Fill(path, halfSize);
            return halfSize ? entry.Half! : entry.Full!;
        }
    }

    public static Raw16Image Decode16(string path, bool halfSize)
    {
        lock (DecodeLock)
        {
            var entry = Fill(path, halfSize);
            return halfSize ? entry.Half16! : entry.Full16!;
        }
    }

    public static Raw16Image? GetCached16(string path, bool full)
    {
        lock (DecodeLock)
        {
            return Cache.TryGetValue(path, out var e) ? (full ? e.Full16 : e.Half16) : null;
        }
    }

    private static CacheEntry Fill(string path, bool halfSize)
    {
        var entry = Cache.GetOrAdd(path, _ => new CacheEntry());
        entry.Touch();

        bool ready = halfSize
            ? entry.Half16 is not null && entry.Half is not null
            : entry.Full16 is not null && entry.Full is not null;
        if (ready) return entry;

        var (raw16, display) = DecodePair(path, halfSize);
        if (halfSize)
        {
            entry.Half16 = raw16;
            entry.Half = display;
        }
        else
        {
            entry.Full16 = raw16;
            entry.Full = display;
        }
        Evict(path);
        return entry;
    }

    private static (Raw16Image Raw, BitmapSource Display) DecodePair(string path, bool halfSize)
    {
        using var r = RawContext.OpenFile(path);
        r.Unpack();
        r.DcrawProcess(p =>
        {
            p.HalfSize = halfSize;
            p.UseCameraWb = true;
            p.OutputBps = 16;
            p.OutputTiff = false;
        });
        using var img = r.MakeDcrawMemoryImage();

        if (img.ImageType != ProcessedImageType.Bitmap || img.Bits != 16 || img.Channels != 3)
            throw new InvalidOperationException($"LibRaw: nieoczekiwany format wyjściowy ({img.ImageType}, {img.Bits} bit, {img.Channels} kanal.)");

        int w = img.Width;
        int h = img.Height;
        var raw16 = Raw16Image.FromInterleaved(img.AsSpan<ushort>().ToArray(), w, h);
        var display = raw16.ToDisplay(dither: true);
        Debug.Log($"RAW DECODE16: {Path.GetFileName(path)} half={halfSize} {w}x{h} 16bpp");
        return (raw16, display);
    }

    public static ImageInfo GetInfo(string path)
    {
        var full = Decode16(path, halfSize: false);
        return new ImageInfo(full.W, full.H, 1);
    }

    public static BitmapSource LoadScaled(string path, ImageInfo info, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / info.Width, (double)maxHeight / info.Height));
        if (scale < 0.02) scale = 0.02;

        int dw = Math.Min(info.Width, Math.Max(1, (int)Math.Round(info.Width * scale)));
        int dh = Math.Min(info.Height, Math.Max(1, (int)Math.Round(info.Height * scale)));

        var source = Decode(path, halfSize: false);
        if (source.PixelWidth == dw && source.PixelHeight == dh) return source;
        return Resize(source, dw, dh);
    }

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
        if (entries.Count <= MaxCacheEntries) return;
        foreach (var e in entries.OrderBy(p => p.Value.LastUse).Take(entries.Count - MaxCacheEntries))
        {
            if (!string.Equals(e.Key, keepPath, StringComparison.OrdinalIgnoreCase))
                Cache.TryRemove(e.Key, out _);
        }
    }

    private sealed class CacheEntry
    {
        public Raw16Image? Half16;
        public Raw16Image? Full16;
        public BitmapSource? Half;
        public BitmapSource? Full;
        public long LastUse;
        public void Touch() => LastUse = Environment.TickCount64;
    }
}