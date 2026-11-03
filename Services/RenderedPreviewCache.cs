using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Services;

/// <summary>
/// Last rendered preview of each photo, shown at once when the photo is opened again while the real render runs in
/// the background. An entry is valid while the image file (length, write time) and the edit signature (the photo's
/// <see cref="EyeState"/> plus a context string: app build, plugins) are unchanged. Kept in memory (LRU, byte budget)
/// and on disk as JPEG (one file per photo, oldest files dropped over the disk budget).
/// </summary>
public sealed class RenderedPreviewCache
{
    public sealed record Meta
    {
        public string Path { get; init; } = "";
        public long Length { get; init; }
        public long WriteTicks { get; init; }
        public string Sig { get; init; } = "";
        /// <summary>Base exposure / camera tone curve of the file itself, for photos without a stored base (see <see cref="StateFor"/>).</summary>
        public double FileBaseEv { get; init; }
        public double[][]? FileCurve { get; init; }
        public double BoxW { get; init; }
        public double BoxH { get; init; }
    }

    private sealed class Entry
    {
        public Meta Meta = null!;
        public BitmapSource? Bitmap;
        public byte[]? Jpeg;
        public long Bytes => Jpeg?.LongLength ?? (Bitmap is null ? 0 : (long)Bitmap.PixelWidth * Bitmap.PixelHeight * 4);
    }

    private readonly object _lock = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, (Entry E, LinkedListNode<string> Node)> _mem = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _dir;
    private readonly long _memBudget;
    private readonly long _diskBudget;
    private readonly int _maxLong;
    private int _writes;

    public bool Enabled { get; set; } = true;
    public int MemoryHits { get; private set; }
    public int DiskHits { get; private set; }

    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("PHOTOGRAPHEREYE_PREVIEWS") is { Length: > 0 } d
            ? d
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotographerEye", "previews");

    public RenderedPreviewCache(string? dir, long memBudget = 192L << 20, long diskBudget = 500L << 20, int maxLong = 2560)
    {
        _dir = dir;
        _memBudget = memBudget;
        _diskBudget = diskBudget;
        _maxLong = maxLong;
    }

    /// <summary>Edit signature: the state without undo history, maps in key order, plus <paramref name="context"/>.</summary>
    public static string Signature(EyeState st, string context)
    {
        static Dictionary<string, T>? Sorted<T>(Dictionary<string, T>? d) =>
            d is null ? null : d.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        var canon = st with
        {
            History = null,
            HistoryPos = null,
            PluginValues = Sorted(st.PluginValues),
            PluginText = Sorted(st.PluginText),
            PluginMasks = Sorted(st.PluginMasks),
            PluginActive = st.PluginActive?.OrderBy(s => s, StringComparer.Ordinal).ToList()
        };
        var json = JsonSerializer.Serialize(canon) + "\n" + context;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// The state to compare with an entry: <paramref name="current"/> with the base exposure / tone curve taken from
    /// the entry when the photo has none of its own yet (they come from the decoder, which has not run at that point).
    /// </summary>
    public static EyeState StateFor(EyeState current, Meta m, bool baseEvKnown, bool curveKnown) => current with
    {
        BaseEv = baseEvKnown ? current.BaseEv : m.FileBaseEv,
        ToneCurve = curveKnown ? current.ToneCurve : m.FileCurve
    };

    public static Meta? MakeMeta(string path, EyeState st, string context, double fileBaseEv, double[][]? fileCurve, Size box)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            return new Meta
            {
                Path = path,
                Length = fi.Length,
                WriteTicks = fi.LastWriteTimeUtc.Ticks,
                Sig = Signature(st, context),
                FileBaseEv = fileBaseEv,
                FileCurve = fileCurve,
                BoxW = box.Width,
                BoxH = box.Height
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Remembers <paramref name="bmp"/> (frozen) in memory; call <see cref="Persist"/> off the UI thread afterwards.</summary>
    public void Put(Meta m, BitmapSource bmp)
    {
        if (!Enabled || !bmp.IsFrozen) return;
        lock (_lock)
        {
            Remove(m.Path);
            var node = _order.AddFirst(m.Path);
            _mem[m.Path] = (new Entry { Meta = m, Bitmap = bmp }, node);
            TrimMemory();
        }
    }

    /// <summary>Downscales and JPEG-encodes the entry (memory then holds the small copy) and writes it to disk.</summary>
    public void Persist(Meta m, BitmapSource bmp)
    {
        if (!Enabled) return;
        byte[] jpeg;
        try
        {
            jpeg = Encode(bmp, _maxLong);
        }
        catch (Exception)
        {
            return;
        }
        lock (_lock)
        {
            if (_mem.TryGetValue(m.Path, out var e) && ReferenceEquals(e.E.Meta, m))
            {
                e.E.Jpeg = jpeg;
                e.E.Bitmap = null;
            }
        }
        if (_dir is null) return;
        try
        {
            Directory.CreateDirectory(_dir);
            var file = FileFor(m.Path);
            var tmp = file + "." + Environment.CurrentManagedThreadId + ".tmp";
            var meta = JsonSerializer.SerializeToUtf8Bytes(m);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(BitConverter.GetBytes(meta.Length));
                fs.Write(meta);
                fs.Write(jpeg);
            }
            File.Move(tmp, file, overwrite: true);
            if (System.Threading.Interlocked.Increment(ref _writes) % 16 == 1) TrimDisk();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Cached preview of <paramref name="path"/> when the file is unchanged and <paramref name="sigFor"/> (the current
    /// signature, given the entry for its file defaults) equals the stored one.
    /// </summary>
    public bool TryGet(string path, Func<Meta, string> sigFor, out BitmapSource? bmp, out Size box)
    {
        bmp = null;
        box = default;
        if (!Enabled) return false;
        long len, ticks;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            len = fi.Length;
            ticks = fi.LastWriteTimeUtc.Ticks;
        }
        catch (Exception)
        {
            return false;
        }

        Meta? m = null;
        BitmapSource? b = null;
        byte[]? jpeg = null;
        bool fromDisk = false;
        lock (_lock)
        {
            if (_mem.TryGetValue(path, out var e))
            {
                m = e.E.Meta;
                b = e.E.Bitmap;
                jpeg = e.E.Jpeg;
                _order.Remove(e.Node);
                _order.AddFirst(e.Node);
            }
        }
        if (m is null && _dir is not null)
        {
            if (!ReadFile(FileFor(path), out m, out jpeg)) return false;
            fromDisk = true;
        }
        if (m is null || !string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)) return false;
        if (m.Length != len || m.WriteTicks != ticks) return false;
        if (!string.Equals(sigFor(m), m.Sig, StringComparison.Ordinal)) return false;
        try
        {
            b ??= jpeg is null ? null : Decode(jpeg);
        }
        catch (Exception)
        {
            return false;
        }
        if (b is null) return false;
        if (fromDisk)
        {
            DiskHits++;
            lock (_lock)
            {
                if (!_mem.ContainsKey(path))
                {
                    var node = _order.AddFirst(path);
                    _mem[path] = (new Entry { Meta = m, Jpeg = jpeg }, node);
                    TrimMemory();
                }
            }
            try { File.SetLastWriteTimeUtc(FileFor(path), DateTime.UtcNow); } catch (Exception) { }
        }
        else MemoryHits++;
        bmp = b;
        box = new Size(m.BoxW, m.BoxH);
        return true;
    }

    public void Clear(bool disk)
    {
        lock (_lock)
        {
            _mem.Clear();
            _order.Clear();
        }
        if (!disk || _dir is null) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*.pv")) { try { File.Delete(f); } catch (Exception) { } }
        }
        catch (Exception)
        {
        }
    }

    public int MemoryCount { get { lock (_lock) return _mem.Count; } }

    public string FileFor(string path)
    {
        var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        return System.IO.Path.Combine(_dir ?? "", h + ".pv");
    }

    private void Remove(string path)
    {
        if (_mem.Remove(path, out var e)) _order.Remove(e.Node);
    }

    private void TrimMemory()
    {
        long total = _mem.Values.Sum(v => v.E.Bytes);
        while (total > _memBudget && _order.Count > 1)
        {
            var last = _order.Last!.Value;
            total -= _mem[last].E.Bytes;
            Remove(last);
        }
    }

    private void TrimDisk()
    {
        if (_dir is null) return;
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.pv").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > _diskBudget) { try { f.Delete(); } catch (Exception) { } }
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool ReadFile(string file, out Meta? m, out byte[]? jpeg)
    {
        m = null;
        jpeg = null;
        try
        {
            if (!File.Exists(file)) return false;
            var all = File.ReadAllBytes(file);
            if (all.Length < 4) return false;
            int n = BitConverter.ToInt32(all, 0);
            if (n <= 0 || 4 + n >= all.Length) return false;
            m = JsonSerializer.Deserialize<Meta>(all.AsSpan(4, n));
            jpeg = all.AsSpan(4 + n).ToArray();
            return m is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] Encode(BitmapSource bmp, int maxLong)
    {
        BitmapSource src = bmp;
        int l = Math.Max(bmp.PixelWidth, bmp.PixelHeight);
        if (l > maxLong)
        {
            double k = maxLong / (double)l;
            var tb = new TransformedBitmap(bmp, new ScaleTransform(k, k));
            tb.Freeze();
            src = tb;
        }
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
        conv.Freeze();
        var enc = new JpegBitmapEncoder { QualityLevel = 92 };
        enc.Frames.Add(BitmapFrame.Create(conv));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource Decode(byte[] jpeg)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.StreamSource = new MemoryStream(jpeg);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
