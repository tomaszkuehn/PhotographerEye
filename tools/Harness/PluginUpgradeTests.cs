using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using ImgViewer;
using ImgViewer.Plugins;

internal static partial class Program
{
    private static float Enc(float v) { v = Math.Clamp(v, 0f, 1f); return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f; }

    /// <summary>Plugins using the command API: Auto → sliders, straighten, exposure clipping map and ETTR, vignette mask, event log.</summary>
    private static void TestPluginUpgrades()
    {
        // ---------------- Auto enhance: set sliders ----------------
        var autoPath = FindAutoEnhanceDll();
        if (autoPath is null) Check(false, "upgrade: auto dll present");
        else
        {
            var entry = PluginManager.LoadFromFile(autoPath);
            var reg = new PluginRegistry(entry.Plugins);
            Check(reg.Commands.Any(c => c.FullId == "photographereye.autoenhance:sliders") &&
                  reg.Commands.Any(c => c.FullId == "photographereye.autoenhance:straighten"),
                  "upgrade auto: set sliders and straighten commands");
            var t = Assembly.LoadFrom(autoPath).GetType("ImgViewer.Plugins.AutoEnhance.AutoEnhance")!;
            var suggest = t.GetMethod("SuggestSliders")!;
            var knotsFrom = t.GetMethod("KnotsFromLut")!;
            object Run(PluginImage? src, bool lin, PluginImage dev, double bal, double con, double wb, double col, bool str) =>
                suggest.Invoke(null, new object?[] { src, lin, dev, bal, con, wb, col, str, 8.0, CancellationToken.None })!;
            T F<T>(object o, string n) => (T)o.GetType().GetField(n)!.GetValue(o)!;

            // Warm cast on a grey scene with a low-contrast tonal range.
            int w = 400, h = 300, n = w * h;
            var sR = new float[n]; var sG = new float[n]; var sB = new float[n];
            var rnd = new Random(7);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float g = 0.08f + 0.17f * x / w + 0.02f * (float)rnd.NextDouble();
                    sR[i] = g * 1.35f; sG[i] = g; sB[i] = g * 0.7f;
                }
            var src = new PluginImage(w, h, sR, sG, sB);
            var dev = new PluginImage(w, h, sR.Select(Enc).ToArray(), sG.Select(Enc).ToArray(), sB.Select(Enc).ToArray());
            var sug = Run(src, true, dev, 60, 50, 80, 40, false);
            var neutral = F<double[]?>(sug, "Neutral");
            Check(neutral is not null && neutral[0] > neutral[1] && neutral[1] > neutral[2],
                  $"upgrade auto: warm cast measured as neutral colour ({(neutral is null ? "null" : string.Join("/", neutral.Select(v => v.ToString("0.000"))))})");
            var knots = F<double[][]?>(sug, "StretchEv");
            bool mono = knots is not null && knots.Length >= 2;
            for (int i = 1; mono && i < knots!.Length; i++)
                mono = knots[i][0] > knots[i - 1][0] && knots[i][1] >= knots[i - 1][1];
            Check(mono, $"upgrade auto: stretch knots, inputs increasing and outputs non-decreasing ({knots?.Length})");
            Check(knots is not null && knots[^1][1] - knots[0][1] > knots[^1][0] - knots[0][0],
                  "upgrade auto: low-contrast photo gets a wider tonal range");
            Check(knots is not null && knots.All(k => k[0] > -14 && k[0] < 10 && k[1] >= -14 && k[1] <= 10), "upgrade auto: knots inside the EV range");
            var none = Run(null, false, dev, 0, 0, 0, 0, false);
            Check(F<double[]?>(none, "Neutral") is null && F<double[][]?>(none, "StretchEv") is null && F<double?>(none, "Angle") is null &&
                  F<double>(none, "Saturation") == 0, "upgrade auto: zero strengths suggest nothing");
            var ident = new float[4096];
            for (int i = 0; i < ident.Length; i++) ident[i] = i / 4095f;
            Check(knotsFrom.Invoke(null, new object[] { ident, 0.0, 1.0 }) is null, "upgrade auto: identity tone LUT gives no knots");

            // Dull colours get more saturation.
            var cR = new float[n]; var cG = new float[n]; var cB = new float[n];
            for (int i = 0; i < n; i++) { float v = 0.3f + 0.4f * (i % w) / w; cR[i] = v * 1.08f; cG[i] = v; cB[i] = v * 0.94f; }
            var dull = Run(null, false, new PluginImage(w, h, cR, cG, cB), 0, 0, 0, 40, false);
            double sat = F<double>(dull, "Saturation");
            Check(sat > 0 && sat <= 100, $"upgrade auto: dull photo gets positive saturation ({sat})");

            // Tilted stripes: the host angle turns against the detected tilt, and both tilt directions differ in sign.
            double? Tilt(double deg)
            {
                int tw = 800, th = 600;
                var L = new float[tw * th];
                double a = deg * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
                for (int y = 0; y < th; y++)
                    for (int x = 0; x < tw; x++)
                    {
                        double v = (y - th / 2) * ca - (x - tw / 2) * sa;
                        L[y * tw + x] = (Math.Floor(v / 40) % 2 == 0) ? 0.8f : 0.2f;
                    }
                var r = Run(null, false, new PluginImage(tw, th, L, (float[])L.Clone(), (float[])L.Clone()), 0, 0, 0, 0, true);
                var res = F<object>(r, "Result");
                double det = (double)res.GetType().GetField("Angle")!.GetValue(res)!;
                var ang = F<double?>(r, "Angle");
                Check(ang is null || Math.Abs(ang.Value + Math.Round(det, 2)) < 1e-9, $"upgrade auto: host angle = -detected ({det:0.00} → {ang})");
                return ang;
            }
            var tp = Tilt(3); var tn = Tilt(-3);
            Check(tp is not null && tn is not null && Math.Abs(Math.Abs(tp.Value) - 3) < 0.6 && Math.Sign(tp.Value) == -Math.Sign(tn.Value),
                  $"upgrade auto: ±3° tilt found with opposite signs ({tp}, {tn})");
        }

        // ---------------- Exposure: clipping map, ETTR ----------------
        var expPath = FindPluginDll("exposure", "ImgViewer.Plugins.ExposureInfo.dll");
        if (expPath is null) Check(false, "upgrade: exposure dll present");
        else
        {
            var entry = PluginManager.LoadFromFile(expPath);
            var reg = new PluginRegistry(entry.Plugins);
            Check(entry.Error is null && reg.Commands.Any(c => c.FullId.EndsWith(":ettr")) && reg.Overlays.Any(o => o.FullId.EndsWith(":clipping")),
                  "upgrade exposure: ETTR command and clipping overlay");
            var t = Assembly.LoadFrom(expPath).GetType("ImgViewer.Plugins.ExposureInfo.ExposureTools")!;
            var map = (OverlayBitmap)t.GetMethod("ClippingMap")!.Invoke(null, new object[] {
                new PluginImage(3, 1, new[] { 1f, 0f, 0.5f }, new[] { 0.5f, 0f, 0.5f }, new[] { 0.5f, 0.002f, 0.5f }) })!;
            Check(map.Width == 3 && map.Rgba.Length == 12 && map.Rgba[0] == 255 && map.Rgba[3] > 0 && map.Rgba[6] == 255 && map.Rgba[4] == 0 && map.Rgba[7] > 0 && map.Rgba[11] == 0,
                  "upgrade exposure: clipping map red / blue / transparent");
            var ettr = t.GetMethod("EttrDelta")!;
            double Delta(int bin, int clipped)
            {
                var hb = new int[256];
                hb[bin] = 10000 - clipped; hb[255] += clipped;
                var hist = new PhotoHistogram { R = hb, G = (int[])hb.Clone(), B = (int[])hb.Clone(), Luma = (int[])hb.Clone() };
                var args = new object?[] { hist, null };
                return (double)ettr.Invoke(null, args)!;
            }
            double d128 = Delta(128, 0);
            double expect = Math.Log((Math.Pow((250 / 255.0 + 0.055) / 1.055, 2.4)) / Math.Pow((128 / 255.0 + 0.055) / 1.055, 2.4), 2);
            Check(Math.Abs(d128 - Math.Round(expect, 2)) < 1e-9, $"upgrade exposure: ETTR mid-grey photo +{d128} EV (expected {expect:0.00})");
            Check(Math.Abs(Delta(128, 100) + 1.0 / 3.0) < 1e-9, "upgrade exposure: ETTR with 1 % clipped → -1/3 EV");
            Check(Delta(250, 0) == 0, "upgrade exposure: ETTR already at target → no change");
            Check(Delta(5, 0) == 3, "upgrade exposure: ETTR limited to +3 EV");
        }

        // ---------------- Vignette mask, event log ----------------
        var vigPath = FindPluginDll("vignette", "ImgViewer.Plugins.Vignette.dll");
        if (vigPath is null) Check(false, "upgrade: vignette dll present");
        else
        {
            var reg = new PluginRegistry(PluginManager.LoadFromFile(vigPath).Plugins);
            Check(reg.Filters.Any() && reg.Filters.All(f => f.D.SupportsMask), "upgrade vignette: supports masks");
        }
        var logPath = FindPluginDll("eventlog", "ImgViewer.Plugins.EventLog.dll");
        if (logPath is null) Check(false, "upgrade: event log dll present");
        else
        {
            var e = PluginManager.LoadFromFile(logPath);
            Check(e.Error is null && e.Plugins.Count == 1 && e.Plugins[0].Error is null, "upgrade event log: loads with the new events");
        }
    }
}
