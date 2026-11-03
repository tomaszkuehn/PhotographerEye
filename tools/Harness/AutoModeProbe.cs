using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using ImgViewer;
using ImgViewer.Imaging;
using ImgViewer.Plugins;

internal static partial class Program
{
    /// <summary>
    /// --automodes &lt;raw dir&gt; [out dir]: Auto enhance (default strengths) after develop (Display) and before it (Base) on
    /// the RAW files of a folder; prints mean / clipped share and writes neutral | display | base side by side.
    /// </summary>
    private static void AutoModeProbe(string dir, string? outDir)
    {
        var dll = FindAutoEnhanceDll();
        if (dll is null) { Console.WriteLine("auto enhance dll not found"); return; }
        var entry = PluginManager.LoadFromFile(dll);
        var reg = new PluginRegistry(entry.Plugins);
        var f = reg.FindFilter("photographereye.autoenhance:auto")!;
        var oldStage = f.D.Stage;
        if (outDir is not null) Directory.CreateDirectory(outDir);
        static (double Mean, double Hi, double Lo) Stats(Raw16Image im)
        {
            double s = 0; int hi = 0, lo = 0;
            for (int i = 0; i < im.R.Length; i++)
            {
                double l = (0.2126 * im.R[i] + 0.7152 * im.G[i] + 0.0722 * im.B[i]) / 65535.0;
                s += l;
                if (Math.Max(im.R[i], Math.Max(im.G[i], im.B[i])) > 65000) hi++;
                if (l < 0.01) lo++;
            }
            return (s / im.R.Length, 100.0 * hi / im.R.Length, 100.0 * lo / im.R.Length);
        }
        try
        {
            foreach (var file in Directory.GetFiles(dir).Where(RawDecoder.IsRaw).OrderBy(p => p))
            {
                var raw = RawDecoder.Decode16(file, halfSize: true);
                double ev = RawDecoder.GetBaseEv(file);
                var curve = RawDecoder.GetCameraTone(file);
                var st = new PluginEditState();
                st.Active.Add(f.FullId);
                foreach (var p in f.D.Params) st.SetValue(f, p, p.Default);
                PluginRenderJob Job() => new() { Registry = reg, State = st, Meta = new ImageMeta { Width = raw.W, Height = raw.H, SourcePath = "" }, Cancel = CancellationToken.None };
                var lin = LinearRender.ApplyGain(raw, ev, 0.0);
                var neutral = Adjust16.RenderNeutral(lin, curve);
                f.D.Stage = FilterStage.Display;
                var disp = PluginPipeline.ApplyRaw16(neutral, FilterStage.Display, Job(), raw.W, raw.H) ?? neutral;
                f.D.Stage = FilterStage.Base;
                var b = PluginPipeline.ApplyRaw16(lin, FilterStage.Base, Job(), raw.W, raw.H) ?? lin;
                var based = Adjust16.RenderNeutral(b, curve);
                var (n0, n1, n2) = Stats(neutral);
                var (d0, d1, d2) = Stats(disp);
                var (b0, b1, b2) = Stats(based);
                Console.WriteLine($"{Path.GetFileName(file),-24} neutral {n0:0.000} clip {n1,5:0.0}% black {n2,5:0.0}% | display {d0:0.000} clip {d1,5:0.0}% black {d2,5:0.0}% | base {b0:0.000} clip {b1,5:0.0}% black {b2,5:0.0}%");
                if (outDir is null) continue;
                int step = Math.Max(1, (int)Math.Ceiling(Math.Max(raw.W, raw.H) / 800.0));
                int w2 = raw.W / step, h2 = raw.H / step;
                var px = new byte[w2 * 3 * h2 * 3];
                var ims = new[] { neutral, disp, based };
                for (int y = 0; y < h2; y++)
                    for (int x = 0; x < w2; x++)
                    {
                        int si = y * step * raw.W + x * step;
                        for (int k = 0; k < 3; k++)
                        {
                            int a = (y * w2 * 3 + k * w2 + x) * 3;
                            px[a] = (byte)(ims[k].R[si] >> 8); px[a + 1] = (byte)(ims[k].G[si] >> 8); px[a + 2] = (byte)(ims[k].B[si] >> 8);
                        }
                    }
                var bs = BitmapSource.Create(w2 * 3, h2, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, px, w2 * 3 * 3);
                var enc = new JpegBitmapEncoder { QualityLevel = 88 };
                enc.Frames.Add(BitmapFrame.Create(bs));
                using var fs = File.Create(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "_modes.jpg"));
                enc.Save(fs);
            }
        }
        finally { f.D.Stage = oldStage; }
    }
}
