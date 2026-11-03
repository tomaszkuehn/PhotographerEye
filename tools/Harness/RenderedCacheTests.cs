using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Services;

internal static partial class Program
{
    private static void TestRenderedCache()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pe_prevcache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string img = Path.Combine(dir, "photo.jpg");
            File.WriteAllBytes(img, new byte[] { 1, 2, 3, 4 });
            string cacheDir = Path.Combine(dir, "cache");

            static BitmapSource Bmp(int w, int h, byte v)
            {
                var px = new byte[w * h * 4];
                for (int i = 0; i < px.Length; i += 4) { px[i] = v; px[i + 1] = (byte)(v / 2); px[i + 2] = (byte)(255 - v); px[i + 3] = 255; }
                var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
                b.Freeze();
                return b;
            }

            // Signature: stable across a JSON round trip and plugin map order, ignores history, sees edits and context.
            var st = new EyeState
            {
                BaseEv = 0.7, Contrast = 12, ToneCurve = new[] { new[] { 0.25, 0.3 }, new[] { 1.0, 1.0 } },
                PluginValues = new Dictionary<string, double> { ["b:x.amt"] = 2, ["a:y.amt"] = 1 },
                PluginActive = new List<string> { "b:x", "a:y" }
            };
            var st2 = JsonSerializer.Deserialize<EyeState>(JsonSerializer.Serialize(st))! with
            {
                PluginValues = new Dictionary<string, double> { ["a:y.amt"] = 1, ["b:x.amt"] = 2 },
                PluginActive = new List<string> { "a:y", "b:x" },
                History = new List<EyeState> { new() }, HistoryPos = 0
            };
            string sig = RenderedPreviewCache.Signature(st, "ctx");
            Check(sig == RenderedPreviewCache.Signature(st2, "ctx"), "preview cache: signature stable across JSON, map order and history");
            Check(sig != RenderedPreviewCache.Signature(st with { Contrast = 13 }, "ctx") && sig != RenderedPreviewCache.Signature(st, "ctx2"),
                  "preview cache: signature changes with an edit or the context");

            // File defaults: before decoding, base exposure / curve come from the entry.
            var meta = RenderedPreviewCache.MakeMeta(img, st, "ctx", 0.7, st.ToneCurve, new Size(4000, 3000))!;
            var early = st with { BaseEv = 0, ToneCurve = null };
            Check(RenderedPreviewCache.Signature(RenderedPreviewCache.StateFor(early, meta, false, false), "ctx") == sig &&
                  RenderedPreviewCache.Signature(RenderedPreviewCache.StateFor(early, meta, true, true), "ctx") != sig,
                  "preview cache: file base exposure and curve filled in before the decode");

            // Memory hit, then disk hit from a new instance (downscaled JPEG, box kept).
            var c1 = new RenderedPreviewCache(cacheDir, maxLong: 800);
            var big = Bmp(1600, 1200, 200);
            c1.Put(meta, big);
            bool m1 = c1.TryGet(img, _ => sig, out var b1, out var box1);
            Check(m1 && ReferenceEquals(b1, big) && box1 == new Size(4000, 3000) && c1.MemoryHits == 1, "preview cache: memory hit returns the render and its box");
            c1.Persist(meta, big);
            Check(File.Exists(c1.FileFor(img)), "preview cache: entry written to disk");
            var c2 = new RenderedPreviewCache(cacheDir, maxLong: 800);
            bool d1 = c2.TryGet(img, _ => sig, out var b2, out var box2);
            Check(d1 && b2 is not null && b2.PixelWidth == 800 && b2.PixelHeight == 600 && b2.IsFrozen && box2 == new Size(4000, 3000) && c2.DiskHits == 1,
                  $"preview cache: disk hit, downscaled to the limit ({b2?.PixelWidth}x{b2?.PixelHeight})");
            if (b2 is not null)
            {
                var px = new byte[4 * b2.PixelWidth];
                new FormatConvertedBitmap(b2, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 300, b2.PixelWidth, 1), px, px.Length, 0);
                Check(Math.Abs(px[400 * 4] - 200) <= 3 && Math.Abs(px[400 * 4 + 2] - 55) <= 3, "preview cache: JPEG round trip keeps the colours");
            }
            Check(c2.TryGet(img, _ => sig, out _, out _) && c2.MemoryHits == 1, "preview cache: disk entry kept in memory afterwards");

            // Misses: other edits, changed file, disabled.
            Check(!c2.TryGet(img, _ => sig + "x", out _, out _), "preview cache: other edits miss");
            File.WriteAllBytes(img, new byte[] { 1, 2, 3, 4, 5 });
            Check(!c2.TryGet(img, _ => sig, out _, out _) && !new RenderedPreviewCache(cacheDir).TryGet(img, _ => sig, out _, out _),
                  "preview cache: a changed image file misses");
            var c3 = new RenderedPreviewCache(cacheDir) { Enabled = false };
            var meta3 = RenderedPreviewCache.MakeMeta(img, st, "ctx", 0.7, null, new Size(10, 10))!;
            c3.Put(meta3, Bmp(10, 10, 9));
            Check(!c3.TryGet(img, _ => sig, out _, out _) && c3.MemoryCount == 0, "preview cache: disabled stores and finds nothing");

            // Memory budget: oldest entries dropped (the newest always stays).
            var c4 = new RenderedPreviewCache(null, memBudget: 3L * 100 * 100 * 4);
            for (int i = 0; i < 6; i++)
            {
                string p = Path.Combine(dir, $"m{i}.jpg");
                File.WriteAllBytes(p, new byte[] { (byte)i });
                c4.Put(RenderedPreviewCache.MakeMeta(p, st, "ctx", 0, null, new Size(100, 100))!, Bmp(100, 100, 50));
            }
            Check(c4.MemoryCount == 3 && c4.TryGet(Path.Combine(dir, "m5.jpg"), _ => sig, out _, out _) && !c4.TryGet(Path.Combine(dir, "m0.jpg"), _ => sig, out _, out _),
                  $"preview cache: memory budget keeps the newest entries ({c4.MemoryCount})");

            // Clear removes memory and disk entries.
            c2.Clear(disk: true);
            Check(c2.MemoryCount == 0 && Directory.GetFiles(cacheDir, "*.pv").Length == 0, "preview cache: clear empties memory and disk");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }
}
