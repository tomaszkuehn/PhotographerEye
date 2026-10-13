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

    public static double GetBaseEv(string path)
    {
        lock (DecodeLock)
        {
            return Cache.TryGetValue(path, out var e) ? e.BaseEv : 0.0;
        }
    }

    private const double BaseTarget = 0.93;
    private const double BasePercent = 0.005;

    public static double ComputeBaseEv(Raw16Image img)
    {
        const int bins = 4096;
        var hist = new long[bins];
        long n = 0;
        int len = img.R.Length;
        int stride = Math.Max(1, len / 2_000_000);
        var sR = img.R;
        var sG = img.G;
        var sB = img.B;
        for (int i = 0; i < len; i += stride)
        {
            int lum = (int)((0.2126f * sR[i] + 0.7152f * sG[i] + 0.0722f * sB[i]) + 0.5f);
            hist[Math.Min(lum >> 4, bins - 1)]++;
            n++;
        }
        if (n == 0) return 0.0;
        long clip = Math.Max(1, (long)(n * BasePercent));
        long acc = 0;
        int bin = bins - 1;
        for (bin = bins - 1; bin >= 0; bin--)
        {
            acc += hist[bin];
            if (acc >= clip) break;
        }
        double p995 = (bin + 0.5) * 65535.0 / bins;
        if (p995 <= 16) return 0.0;
        double ev = Math.Log2(BaseTarget * 65535.0 / p995);
        return Math.Clamp(ev, -3.5, 5.5);
    }

    private static CacheEntry Fill(string path, bool halfSize)
    {
        var entry = Cache.GetOrAdd(path, _ => new CacheEntry());
        entry.Touch();

        bool ready = halfSize
            ? entry.Half16 is not null && entry.Half is not null
            : entry.Full16 is not null && entry.Full is not null;
        if (ready) return entry;

        var (raw16, display, ev) = DecodePair(path, halfSize);
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
        if (!entry.BaseEvDone)
        {
            entry.BaseEv = ev;
            entry.BaseEvDone = true;
            Debug.Log($"RAW BASEEV: {Path.GetFileName(path)} baseEv={ev:0.00}");
        }
        Evict(path);
        return entry;
    }

    private static (Raw16Image Raw, BitmapSource Display, double Ev) DecodePair(string path, bool halfSize)
    {
        if (IsJpegXsRaw(path))
        {
            var he = DecodeViaLibRaw22(path, halfSize, heTool: true);
            if (he is not null) return he.Value;
            Debug.Log("RAW HE: dekodowanie TicoRAW nie powiodło się (brak fallbacku lub HE*)");
            throw HeNotSupported();
        }
        try
        {
            var (raw, disp, ev) = DecodePairSdcb(path, halfSize);
            if (IsLikelyGarbageDecode(raw))
            {
                Debug.Log("RAW SDCB: wynik wygląda na śmieci (derror); próba fallbacku libraw22");
                var fb = DecodeViaLibRaw22(path, halfSize, heTool: false);
                if (fb is not null) return fb.Value;
            }
            return (raw, disp, ev);
        }
        catch (Exception ex)
        {
            Debug.Log($"RAW SDCB FAIL: [{ex.GetType().Name}] {ex.Message}; próba fallbacku libraw22");
            var fb = DecodeViaLibRaw22(path, halfSize, heTool: false);
            if (fb is not null) return fb.Value;
            throw;
        }
    }

    private static InvalidOperationException HeNotSupported()
    {
        return new InvalidOperationException(
            "Nikon NEF High Efficiency (HE/HE*): failed to decode. The HE variant is usually supported (TicoRAW decoder from the LibRaw fork); " +
            "the HE* variant (higher quality) is not supported. Set your camera's NEF compression to Lossless compressed " +
            "or convert files to DNG (Adobe DNG Converter).");
    }

    private static bool IsLikelyGarbageDecode(Raw16Image img)
    {
        double sR = 0, sG = 0, sB = 0, sRR = 0, sGG = 0, sBB = 0, sRG = 0, sGB = 0;
        long n = 0;
        for (int i = 0; i < img.R.Length; i += 197)
        {
            double r = img.R[i], g = img.G[i], b = img.B[i];
            sR += r; sG += g; sB += b;
            sRR += r * r; sGG += g * g; sBB += b * b;
            sRG += r * g; sGB += g * b;
            n++;
        }
        if (n < 100) return false;
        double n2 = n;
        double cRG = (sRG - sR * sG / n2) / Math.Sqrt(Math.Max(1.0, (sRR - sR * sR / n2)) * (sGG - sG * sG / n2));
        double cGB = (sGB - sG * sB / n2) / Math.Sqrt(Math.Max(1.0, (sGG - sG * sG / n2)) * (sBB - sB * sB / n2));
        return Math.Abs(cRG) < 0.3 || Math.Abs(cGB) < 0.3;
    }

    private static (Raw16Image Raw, BitmapSource Display, double Ev) DecodePairSdcb(string path, bool halfSize)
    {
        using var r = RawContext.OpenFile(path);
        r.Unpack();
        r.DcrawProcess(p =>
        {
            p.HalfSize = halfSize;
            p.UseCameraWb = true;
            p.OutputBps = 16;
            p.OutputTiff = false;
            p.HighlightMode = 2;
            p.NoAutoBright = true;
            p.Gamma = new double[] { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
        });
        using var img = r.MakeDcrawMemoryImage();

        if (img.ImageType != ProcessedImageType.Bitmap || img.Bits != 16 || img.Channels != 3)
            throw new InvalidOperationException($"LibRaw: unexpected output format ({img.ImageType}, {img.Bits} bit, {img.Channels} channels)");

        int w = img.Width;
        int h = img.Height;
        var raw16 = Raw16Image.FromInterleaved(img.AsSpan<ushort>().ToArray(), w, h);
        double ev = ComputeBaseEv(raw16);
        var gained = LinearRender.ApplyGain(raw16, ev, 0.0);
        var display = Adjust16.RenderNeutral(gained).ToDisplay(dither: true);
        Debug.Log($"RAW DECODE16: {Path.GetFileName(path)} half={halfSize} {w}x{h} 16bpp linear hl=2 baseEv={ev:0.00}");
        return (raw16, display, ev);
    }

    public static (Raw16Image Raw, BitmapSource Display, double Ev)? DecodeViaLibRaw22(string path, bool halfSize, bool heTool)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "thirdparty", heTool ? "librawhe" : "libraw22", "dcraw_emu.exe");
        if (!File.Exists(exe)) return null;

        string work = Path.Combine(Path.GetTempPath(), heTool ? "photographereye-lrhe" : "photographereye-lr22");
        Directory.CreateDirectory(work);
        string tin = Path.Combine(work, $"dec-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        string tout = tin + ".ppm";
        try
        {
            File.Copy(path, tin, true);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"-w -H 2 -W -6 -g 1 1{(halfSize ? " -h" : "")} \"{tin}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string err = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(180_000)) { try { proc.Kill(); } catch (Exception) { } }
            if (proc.ExitCode != 0) return null;
            if (err.Contains("Cannot unpack", StringComparison.Ordinal) ||
                err.Contains("Cannot open", StringComparison.Ordinal) ||
                err.Contains("data corrupted", StringComparison.Ordinal)) return null;
            if (!File.Exists(tout)) return null;

            var packed = ParseP6Rgb16(tout, out int w, out int h);
            if (packed is null) return null;
            var raw16 = Raw16Image.FromInterleaved(packed, w, h);
            double ev = ComputeBaseEv(raw16);
            var gained = LinearRender.ApplyGain(raw16, ev, 0.0);
            var display = Adjust16.RenderNeutral(gained).ToDisplay(dither: true);
            Debug.Log($"RAW DECODE{(heTool ? "HE" : "22")}: {Path.GetFileName(path)} half={halfSize} {w}x{h} 16bpp linear hl=2 baseEv={ev:0.00}");
            return (raw16, display, ev);
        }
        catch (Exception ex)
        {
            Debug.Log($"RAW22 FAIL: [{ex.GetType().Name}] {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(tin); } catch (Exception) { }
            try { File.Delete(tout); } catch (Exception) { }
        }
    }

    private static ushort[]? ParseP6Rgb16(string path, out int w, out int h)
    {
        w = h = 0;
        try
        {
            using var fs = File.OpenRead(path);
            var header = new byte[256];
            int hdrLen = fs.Read(header, 0, header.Length);
            string head = System.Text.Encoding.ASCII.GetString(header, 0, hdrLen);
            if (!head.StartsWith("P6", StringComparison.Ordinal)) return null;
            int p = 2;
            int field = 0;
            int wv = 0, hv = 0, maxv = 0;
            while (field < 3)
            {
                while (p < head.Length && (head[p] == ' ' || head[p] == '\r' || head[p] == '\n' || head[p] == '\t'))
                    p++;
                if (p < head.Length && head[p] == '#')
                {
                    while (p < head.Length && head[p] != '\n') p++;
                    continue;
                }
                int start = p;
                while (p < head.Length && head[p] >= '0' && head[p] <= '9')
                    p++;
                if (p == start) return null;
                if (head[p] != ' ' && head[p] != '\r' && head[p] != '\n' && head[p] != '\t') return null;
                int v = int.Parse(head.Substring(start, p - start), System.Globalization.CultureInfo.InvariantCulture);
                if (field == 0) wv = v;
                else if (field == 1) hv = v;
                else maxv = v;
                field++;
            }
            if (maxv != 65535 || wv <= 0 || hv <= 0 || (long)wv * hv > int.MaxValue / 6) return null;
            w = wv;
            h = hv;
            long dataStart = fs.Position - (hdrLen - p) + 1;
            fs.Seek(dataStart, SeekOrigin.Begin);

            const int readChunk = 1024 * 1024;
            var raw = new byte[(long)w * h * 6];
            int total = 0;
            var buf = new byte[readChunk];
            while (total < raw.Length)
            {
                int n = fs.Read(buf, 0, Math.Min(readChunk, raw.Length - total));
                if (n <= 0) return null;
                System.Buffer.BlockCopy(buf, 0, raw, total, n);
                total += n;
            }
            var packed = new ushort[w * h * 3];
            for (int i = 0; i < packed.Length; i++)
            {
                int o = i * 2;
                packed[i] = (ushort)((raw[o] << 8) | raw[o + 1]);
            }
            return packed;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsJpegXsRaw(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[4096];
            long total = fs.Length;
            long probe = 0;
            while (probe < total && probe < 64L * 1024 * 1024)
            {
                int n = fs.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                for (int i = 0; i + 3 < n; i++)
                    if (buf[i] == 0xFF && buf[i + 1] == 0x10 && buf[i + 2] == 0xFF && buf[i + 3] == 0x50)
                        return true;
                if (n < buf.Length && probe + n >= total) break;
                probe += n - 3;
                fs.Seek(probe, SeekOrigin.Begin);
            }
        }
        catch (Exception) { }
        return false;
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
        public double BaseEv;
        public bool BaseEvDone;
        public long LastUse;
        public void Touch() => LastUse = Environment.TickCount64;
    }
}