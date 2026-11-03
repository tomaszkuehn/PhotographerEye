using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.AutoEnhance;

/// <summary>
/// "Auto" toolbar toggle: automatic noise reduction (strength from the noise measured in the photo),
/// histogram balance (levels + clipped partial equalization), contrast towards a target spread and
/// horizon straightening (Hough on long near-horizontal / near-vertical edges, rotation zoomed to fill the frame).
/// Strengths are adjustable in the Adjustments panel. Setting "mode" (Settings > Plugin settings):
/// <list type="bullet">
/// <item>"once" (default): runs after develop, but measures the photo only once (first render with Auto on, kept
/// on disk), so later manual corrections are not undone by a new measurement;</item>
/// <item>"base": runs first, on the photo before the manual exposure / white balance (FilterStage.Base); the manual
/// corrections act on its result.</item>
/// </list>
/// </summary>
public sealed class AutoEnhancePlugin : IEyePlugin
{
    public const string FilterId = "auto";
    public const string ModeOnce = "once";
    public const string ModeBase = "base";
    private IPluginHost _host;
    private FilterDescriptor _filter;

    public static FilterStage StageFor(string mode) => mode == ModeBase ? FilterStage.Base : FilterStage.Display;

    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "photographereye.autoenhance",
        Name = "Auto enhance",
        Version = "1.0",
        Author = "PhotographerEye",
        Description = "One-click automatic processing: noise reduction, white balance, histogram balance, contrast, color and straightening."
    };

    public void Initialize(IPluginHost host)
    {
        _host = host;
        try { AutoEnhance.BaselineDir = System.IO.Path.Combine(host.DataDirectory, "baseline"); }
        catch (Exception ex) { host.Log("baseline folder unavailable: " + ex.Message); }
        var mode = host.AddSetting(new SettingDescriptor
        {
            Id = "mode",
            Label = "Auto enhance mode",
            Description = "Measure once: Auto runs after the manual corrections but measures the photo only once, so your edits are not undone.\n" +
                          "Base: Auto runs first, on the photo before the manual exposure and white balance; the manual corrections act on its result.",
            Choices = new[]
            {
                new ParamChoice(ModeOnce, "Measure once, after the manual corrections"),
                new ParamChoice(ModeBase, "Base, before the manual corrections")
            },
            DefaultId = ModeOnce,
            Changed = v => { if (_filter != null) _filter.Stage = StageFor(v); }
        });
        host.AddFilter(_filter = new FilterDescriptor
        {
            Id = FilterId,
            Label = "Auto",
            Description = "Automatic noise reduction, white balance, histogram balance, contrast, color and horizon straightening (Ctrl+Shift+A)",
            Group = "Auto",
            Stage = StageFor(mode.Value),
            Placement = FilterPlacement.Toolbar,
            Order = -100,
            SupportsMask = false,
            Params = AutoEnhance.Params,
            IsIdentity = AutoEnhance.IsIdentity,
            Render = (img, p, ctx) =>
            {
                var r = AutoEnhance.Render(img, p, ctx);
                if (!ctx.IsPreview) _host?.Log("auto enhance: " + r);
            }
        });
        host.AddCommand(new CommandDescriptor
        {
            Id = "toggle",
            Label = "Auto enhance",
            Shortcut = "Ctrl+Shift+A",
            Placement = CommandPlacement.Menu,
            Execute = c =>
            {
                bool on = !c.IsActive(FilterId);
                c.SetActive(FilterId, on);
                c.SetStatus(on ? "Auto enhance on" : "Auto enhance off");
            }
        });
        host.AddCommand(new CommandDescriptor
        {
            Id = "remeasure",
            Label = "Auto enhance: measure photo again",
            Placement = CommandPlacement.Menu,
            Execute = c =>
            {
                if (string.IsNullOrEmpty(c.CurrentPath)) { c.SetStatus("No photo open"); return; }
                AutoEnhance.ForgetBaseline(c.CurrentPath);
                // Marks the photo for a re-render; the state itself does not change.
                c.SetActive(FilterId, c.IsActive(FilterId));
                c.SetStatus(_filter?.Stage == FilterStage.Base
                    ? "Auto enhance (base mode) measures the photo before the manual corrections on every render"
                    : "Auto enhance will measure the photo as it looks now");
            }
        });
        host.AddPreset(new PresetDescriptor
        {
            Id = "gentle",
            Name = "Auto enhance (gentle)",
            Group = "Auto",
            Values = new Dictionary<string, double> { ["auto/balance"] = 30, ["auto/contrast"] = 25, ["auto/denoise"] = 40, ["auto/wb"] = 60, ["auto/color"] = 25 },
            Active = new List<string> { FilterId }
        });
        host.AddPreset(new PresetDescriptor
        {
            Id = "strong",
            Name = "Auto enhance (strong)",
            Group = "Auto",
            Values = new Dictionary<string, double> { ["auto/balance"] = 90, ["auto/contrast"] = 80, ["auto/denoise"] = 80, ["auto/wb"] = 100, ["auto/color"] = 70 },
            Active = new List<string> { FilterId }
        });
    }
}

/// <summary>Result of one render (for logs and tests).</summary>
public sealed class AutoResult
{
    public double NoiseSigma;
    public double ChromaSigma;
    public double Black, White, Gamma;
    /// <summary>Detected tilt in degrees (0 = none or not confident).</summary>
    public double Angle;
    public double AngleConfidence;
    /// <summary>Applied white balance gains (luma-normalized) and the share of near-neutral pixels found.</summary>
    public double GainR = 1, GainG = 1, GainB = 1, WbConfidence;
    /// <summary>Mean saturation measured and the vibrance strength applied (negative = desaturate).</summary>
    public double Saturation, Vibrance;

    public override string ToString() =>
        FormattableString.Invariant($"noise {NoiseSigma:0.0000} chroma {ChromaSigma:0.0000} levels {Black:0.000}..{White:0.000} contrast g {Gamma:0.00} wb {GainR:0.000}/{GainG:0.000}/{GainB:0.000} (gray {WbConfidence:0.00}) sat {Saturation:0.00} vib {Vibrance:+0.00;-0.00} tilt {Angle:0.00}° (conf {AngleConfidence:0.0})");
}

public static class AutoEnhance
{
    public static readonly IReadOnlyList<ParamDef> Params = new[]
    {
        Tip(ParamDef.Slider("balance", "Histogram balance", 0, 100, 60),
            "Sets black and white points from the histogram and partially equalizes it (clipped, no posterization)"),
        Tip(ParamDef.Slider("contrast", "Contrast", 0, 100, 50),
            "Moves global contrast towards a natural tonal spread: flat photos get more, harsh photos less"),
        Tip(ParamDef.Slider("denoise", "Noise reduction", 0, 100, 60),
            "Strength relative to the noise measured in the photo (per brightness level); clean photos stay untouched"),
        Tip(ParamDef.Slider("wb", "White balance", 0, 100, 80),
            "Removes the color cast estimated from near-neutral areas; very strong casts (sunset, stage light) are only reduced"),
        Tip(ParamDef.Slider("color", "Color correction", 0, 100, 40),
            "Vibrance towards a natural saturation: dull colors are boosted, skin tones protected, oversaturated photos toned down"),
        Tip(ParamDef.Toggle("straighten", "Straighten", true),
            "Levels the horizon / verticals found in the photo and zooms slightly so no empty corners appear"),
        Tip(ParamDef.Slider("maxangle", "Max straighten angle", 1, 15, 8, 0.5, "0.0°"),
            "Larger tilts are treated as intentional and left alone")
    };

    private static ParamDef Tip(ParamDef d, string tip) { d.Tooltip = tip; return d; }

    public static bool IsIdentity(FilterParams p) =>
        p.Get("balance") <= 0 && p.Get("contrast") <= 0 && p.Get("denoise") <= 0 && p.Get("wb") <= 0 && p.Get("color") <= 0
        && !p.GetBool("straighten");

    private const int AnalysisSize = 1024;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double Angle, double Conf)> TiltCache = new();

    /// <summary>Forgets cached horizon angles (all images).</summary>
    public static void ClearTiltCache() => TiltCache.Clear();

    // ---------------------------------------------------------------
    //  Measure once: small copy of the photo as Auto first saw it
    // ---------------------------------------------------------------

    /// <summary>Long side of the stored measurement copy.</summary>
    public const int BaselineSize = 256;

    /// <summary>Folder for measurement copies (null: memory only, e.g. tests).</summary>
    public static string BaselineDir { get; set; }

    public sealed class Baseline
    {
        public string Key;
        public int W, H;
        public float[] R, G, B;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Baseline> Baselines = new();

    /// <summary>Forgets the measurement of one photo (memory and disk) or of all photos (null).</summary>
    public static void ForgetBaseline(string path)
    {
        if (path is null) { Baselines.Clear(); return; }
        string prefix = Norm(path) + "|";
        foreach (var k in Baselines.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal)) Baselines.TryRemove(k, out _);
        var f = BaselineFile(path);
        try { if (f != null && System.IO.File.Exists(f)) System.IO.File.Delete(f); } catch { }
    }

    /// <summary>Forgets the in-memory measurements only (stored files are read again on the next render).</summary>
    public static void ClearBaselineMemory() => Baselines.Clear();

    /// <summary>True when a measurement of <paramref name="path"/> is kept in memory.</summary>
    public static bool HasBaseline(string path)
    {
        string prefix = Norm(path) + "|";
        foreach (var k in Baselines.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string Norm(string path)
    {
        try { return System.IO.Path.GetFullPath(path).ToLowerInvariant(); } catch { return path.ToLowerInvariant(); }
    }

    private static string BaselineKey(RenderContext ctx)
    {
        string path = ctx?.Meta?.SourcePath;
        if (string.IsNullOrEmpty(path)) return null;
        long stamp = 0;
        try { stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        return FormattableString.Invariant($"{Norm(path)}|{stamp}|{ctx.FullWidth}x{ctx.FullHeight}");
    }

    private static string BaselineFile(string path)
    {
        var dir = BaselineDir;
        if (string.IsNullOrEmpty(dir)) return null;
        using (var sha = System.Security.Cryptography.SHA1.Create())
        {
            var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Norm(path)));
            return System.IO.Path.Combine(dir, BitConverter.ToString(h).Replace("-", "").ToLowerInvariant() + ".bin");
        }
    }

    /// <summary>The stored measurement copy for this photo, or a new one taken from <paramref name="img"/>.</summary>
    private static Baseline GetBaseline(string key, PluginImage img)
    {
        if (Baselines.TryGetValue(key, out var b)) return b;
        string path = key.Substring(0, key.IndexOf('|'));
        b = LoadBaseline(path, key);
        if (b == null)
        {
            b = new Baseline { Key = key };
            b.R = Downscale(img.R, img.Width, img.Height, BaselineSize, out b.W, out b.H);
            b.G = Downscale(img.G, img.Width, img.Height, BaselineSize, out _, out _);
            b.B = Downscale(img.B, img.Width, img.Height, BaselineSize, out _, out _);
            b = Baselines.GetOrAdd(key, b);
            SaveBaseline(path, b);
        }
        else b = Baselines.GetOrAdd(key, b);
        if (Baselines.Count > 64)
            foreach (var k in Baselines.Keys)
                if (k != key) { Baselines.TryRemove(k, out _); if (Baselines.Count <= 48) break; }
        return b;
    }

    private const int BaselineMagic = 0x31424145; // "EAB1"

    private static Baseline LoadBaseline(string path, string key)
    {
        var f = BaselineFile(path);
        if (f == null || !System.IO.File.Exists(f)) return null;
        try
        {
            using (var br = new System.IO.BinaryReader(System.IO.File.OpenRead(f)))
            {
                if (br.ReadInt32() != BaselineMagic || br.ReadString() != key) return null;
                var b = new Baseline { Key = key, W = br.ReadInt32(), H = br.ReadInt32() };
                int n = b.W * b.H;
                if (b.W <= 0 || b.H <= 0 || n > BaselineSize * BaselineSize) return null;
                float[] Plane()
                {
                    var p = new float[n];
                    for (int i = 0; i < n; i++) p[i] = br.ReadUInt16() / 65535f;
                    return p;
                }
                b.R = Plane(); b.G = Plane(); b.B = Plane();
                return b;
            }
        }
        catch
        {
            return null;
        }
    }

    private static void SaveBaseline(string path, Baseline b)
    {
        var f = BaselineFile(path);
        if (f == null) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
            var tmp = f + ".tmp";
            using (var bw = new System.IO.BinaryWriter(System.IO.File.Create(tmp)))
            {
                bw.Write(BaselineMagic);
                bw.Write(b.Key);
                bw.Write(b.W);
                bw.Write(b.H);
                foreach (var p in new[] { b.R, b.G, b.B })
                    foreach (var v in p) bw.Write((ushort)Math.Round(Clamp01(v) * 65535f));
            }
            if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
            System.IO.File.Move(tmp, f);
            // Keep the folder small: oldest copies go first.
            var files = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(f)).GetFiles("*.bin");
            if (files.Length > 400)
            {
                Array.Sort(files, (x, y) => x.LastWriteTimeUtc.CompareTo(y.LastWriteTimeUtc));
                for (int i = 0; i < files.Length - 300; i++) { try { files[i].Delete(); } catch { } }
            }
        }
        catch
        {
            // Memory copy still works for this session.
        }
    }

    private static string TiltKey(RenderContext ctx, double maxAngle)
    {
        string path = ctx?.Meta?.SourcePath;
        if (string.IsNullOrEmpty(path)) return null;
        long stamp = 0;
        try { stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        return FormattableString.Invariant($"{path}|{stamp}|{ctx.FullWidth}x{ctx.FullHeight}|{ctx.Stage}|{maxAngle:0.###}");
    }

    public static AutoResult Render(PluginImage img, FilterParams p, RenderContext ctx)
    {
        var res = new AutoResult();
        var ct = ctx?.Cancel ?? default;
        double scale = ctx != null && ctx.Scale > 0 ? Math.Min(1.0, ctx.Scale) : 1.0;
        bool linear = ctx != null && ctx.Space == PluginColorSpace.LinearSrgb;
        double balance = Clamp(p.Get("balance") / 100.0, 0, 1);
        double contrast = Clamp(p.Get("contrast") / 100.0, 0, 1);
        double denoise = Clamp(p.Get("denoise") / 100.0, 0, 1);
        double wb = Clamp(p.Get("wb") / 100.0, 0, 1);
        double color = Clamp(p.Get("color") / 100.0, 0, 1);
        bool straighten = p.GetBool("straighten");
        double maxAngle = Clamp(p.Get("maxangle"), 0.5, 20);

        if (linear) EncodeSrgb(img, true);
        int w = img.Width, h = img.Height, n = img.Length;
        var t0 = new float[n];
        var t1 = new float[n];

        // After develop the input already holds the manual corrections: measure the photo once and keep it, so later
        // corrections are not measured (and undone) again. The Base stage sees the photo before them: measure live.
        Baseline bl = null;
        if (ctx != null && ctx.Stage == FilterStage.Display && (balance > 0 || contrast > 0 || wb > 0 || color > 0))
        {
            string bkey = BaselineKey(ctx);
            if (bkey != null) bl = GetBaseline(bkey, img);
        }

        if (denoise > 0)
        {
            var t2 = new float[n];
            var t3 = new float[n];
            Denoise(img, denoise, scale, t0, t1, t2, t3, res, ct);
        }
        ct.ThrowIfCancellationRequested();

        // Analysis on a small copy so the preview and the full-size export decide the same.
        if (bl != null)
        {
            if (wb > 0 && EstimateWhiteBalance(bl.R, bl.G, bl.B, wb, res))
                ApplyGains(img, res.GainR, res.GainG, res.GainB);
            if (color > 0)
                res.Vibrance = VibranceStrength(bl.R, bl.G, bl.B, res, color);
        }
        else if (wb > 0 || color > 0)
        {
            var aR = Downscale(img.R, w, h, ColorAnalysisSize, out _, out _);
            var aG = Downscale(img.G, w, h, ColorAnalysisSize, out _, out _);
            var aB = Downscale(img.B, w, h, ColorAnalysisSize, out _, out _);
            if (wb > 0 && EstimateWhiteBalance(aR, aG, aB, wb, res))
                ApplyGains(img, res.GainR, res.GainG, res.GainB);
            if (color > 0)
                res.Vibrance = VibranceStrength(aR, aG, aB, res, color);
        }
        ct.ThrowIfCancellationRequested();

        Luma(img, t0);
        var aL = Downscale(t0, w, h, AnalysisSize, out int aw, out int ah);

        double angle = 0;
        if (straighten)
        {
            // The tilt is a property of the photo, not of the current tone/color settings: detect it once per
            // image and reuse it, otherwise every exposure / WB / curve change re-detects and the frame jumps.
            string key = TiltKey(ctx, maxAngle);
            double conf;
            if (key is null || !TiltCache.TryGetValue(key, out var cached))
            {
                angle = DetectTilt(aL, aw, ah, maxAngle, out conf);
                if (key is not null)
                {
                    if (TiltCache.Count > 256) TiltCache.Clear();
                    TiltCache[key] = (angle, conf);
                }
            }
            else (angle, conf) = cached;
            res.AngleConfidence = conf;
            res.Angle = angle;
        }
        ct.ThrowIfCancellationRequested();

        if (balance > 0 || contrast > 0)
        {
            var lut = BuildToneLut(bl != null ? BaselineLuma(bl, res) : aL, balance, contrast, res);
            ApplyLumaLut(img, t0, lut);
        }
        ct.ThrowIfCancellationRequested();

        if (Math.Abs(res.Vibrance) > 1e-3)
            ApplyVibrance(img, (float)res.Vibrance);
        ct.ThrowIfCancellationRequested();

        if (Math.Abs(angle) >= 0.1)
            Rotate(img, angle, t0, t1);

        if (linear) EncodeSrgb(img, false);
        return res;
    }

    // =================================================================
    //  Noise reduction
    // =================================================================

    private static void Denoise(PluginImage img, double amount, double scale, float[] t0, float[] t1, float[] t2, float[] t3, AutoResult res, System.Threading.CancellationToken ct)
    {
        int w = img.Width, h = img.Height, n = img.Length;
        if (w < 8 || h < 8) return;
        float[] Y = img.R, Cb = img.G, Cr = img.B;
        // In place: R <- Y, G <- B - Y, B <- R - Y.
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++)
            {
                float r = img.R[i], g = img.G[i], b = img.B[i];
                float l = 0.299f * r + 0.587f * g + 0.114f * b;
                Y[i] = l;
                Cb[i] = b - l;
                Cr[i] = r - l;
            }
        });

        // Noise profile: sigma per brightness level (noise in encoded values is larger in the shadows).
        const int Bins = 8;
        Copy(Y, t0);
        Box(t0, w, h, 2);
        var prof = NoiseProfile(Y, t0, w, h, Bins, out double global);
        double chroma = Math.Max(NoiseGlobal(Cb, w, h), NoiseGlobal(Cr, w, h));
        res.NoiseSigma = global;
        res.ChromaSigma = chroma;
        ct.ThrowIfCancellationRequested();

        // ---- luminance: self-guided filter with a per-pixel eps from the noise profile ----
        if (global > 0.0015)
        {
            float k = (float)(0.8 + 1.9 * amount);
            float blend = (float)Math.Min(1.0, 0.25 + amount);
            int r = 2;
            // t0 = mean(Y) (already r=2), t1 = mean(Y^2)
            Parallel.For(0, h, y => { for (int i = y * w, e = i + w; i < e; i++) t1[i] = Y[i] * Y[i]; });
            Box(t1, w, h, r);
            Parallel.For(0, h, y =>
            {
                for (int i = y * w, e = i + w; i < e; i++)
                {
                    float m = t0[i];
                    float v = Math.Max(0f, t1[i] - m * m);
                    float s = Sample(prof, m) * k;
                    float a = v / (v + s * s + 1e-8f);
                    t0[i] = m - a * m; // b
                    t1[i] = a;
                }
            });
            Box(t0, w, h, r);
            Box(t1, w, h, r);
            Parallel.For(0, h, y =>
            {
                for (int i = y * w, e = i + w; i < e; i++)
                {
                    float q = t1[i] * Y[i] + t0[i];
                    Y[i] += (q - Y[i]) * blend;
                }
            });
        }
        ct.ThrowIfCancellationRequested();

        // ---- chroma: guided by the (denoised) luminance, larger radius for color blotches ----
        float cBlend = (float)(Math.Min(1.0, 0.3 + amount) * SmoothStep(0.0008, 0.004, chroma));
        if (cBlend > 0.01f)
        {
            int rc = Math.Max(2, (int)Math.Round(8 * scale));
            float kc = (float)(1.5 + 2.5 * amount);
            Copy(Y, t0);
            Box(t0, w, h, rc); // mean I
            Parallel.For(0, h, y => { for (int i = y * w, e = i + w; i < e; i++) t1[i] = Y[i] * Y[i]; });
            Box(t1, w, h, rc); // mean I^2
            foreach (var P in new[] { Cb, Cr })
            {
                Copy(P, t2);
                Box(t2, w, h, rc); // mean p
                Parallel.For(0, h, y => { for (int i = y * w, e = i + w; i < e; i++) t3[i] = Y[i] * P[i]; });
                Box(t3, w, h, rc); // mean Ip
                var aP = t3;
                var bP = t2;
                Parallel.For(0, h, y =>
                {
                    for (int i = y * w, e = i + w; i < e; i++)
                    {
                        float mi = t0[i];
                        float varI = Math.Max(0f, t1[i] - mi * mi);
                        float cov = t3[i] - mi * t2[i];
                        float s = Math.Max(Sample(prof, mi), (float)chroma) * kc;
                        float a = cov / (varI + s * s + 1e-6f);
                        aP[i] = a;
                        bP[i] = t2[i] - a * mi;
                    }
                });
                Box(aP, w, h, rc);
                Box(bP, w, h, rc);
                Parallel.For(0, h, y =>
                {
                    for (int i = y * w, e = i + w; i < e; i++)
                    {
                        float q = aP[i] * Y[i] + bP[i];
                        P[i] += (q - P[i]) * cBlend;
                    }
                });
                ct.ThrowIfCancellationRequested();
            }
        }

        // Back to RGB.
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++)
            {
                float l = Y[i], b = Cb[i] + l, r = Cr[i] + l;
                float g = (l - 0.299f * r - 0.114f * b) / 0.587f;
                img.R[i] = Clamp01(r);
                img.G[i] = Clamp01(g);
                img.B[i] = Clamp01(b);
            }
        });
    }

    /// <summary>
    /// Noise sigma per brightness bin: median of |Laplacian-like 3x3 operator| / (0.6745 * 6) (Immerkaer),
    /// robust to edges because of the median. Bins with too few samples borrow from neighbours.
    /// </summary>
    private static float[] NoiseProfile(float[] Y, float[] mean, int w, int h, int bins, out double global)
    {
        const int HB = 1024;
        const float HMax = 0.25f;
        var hist = new long[bins + 1, HB];
        var gate = new object();
        Parallel.For(1, h - 1, () => new long[bins + 1, HB], (y, _, local) =>
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                float v = Y[i - w - 1] - 2 * Y[i - w] + Y[i - w + 1]
                          - 2 * Y[i - 1] + 4 * Y[i] - 2 * Y[i + 1]
                          + Y[i + w - 1] - 2 * Y[i + w] + Y[i + w + 1];
                v = Math.Abs(v);
                int hb = (int)(v / HMax * (HB - 1));
                if (hb >= HB) hb = HB - 1;
                int b = mean == null ? 0 : (int)(Clamp01(mean[i]) * bins);
                if (b >= bins) b = bins - 1;
                local[b, hb]++;
                local[bins, hb]++;
            }
            return local;
        }, local =>
        {
            lock (gate)
                for (int b = 0; b <= bins; b++)
                    for (int k = 0; k < HB; k++) hist[b, k] += local[b, k];
        });

        double Median(int b, out long count)
        {
            count = 0;
            for (int k = 0; k < HB; k++) count += hist[b, k];
            if (count == 0) return 0;
            long half = count / 2, acc = 0;
            for (int k = 0; k < HB; k++)
            {
                acc += hist[b, k];
                if (acc >= half) return (k + 0.5) / (HB - 1) * HMax;
            }
            return HMax;
        }

        const double Norm = 1.0 / (0.6745 * 6.0);
        global = Median(bins, out long total) * Norm;
        var prof = new float[bins];
        var ok = new bool[bins];
        for (int b = 0; b < bins; b++)
        {
            double m = Median(b, out long c);
            if (c >= Math.Max(500, total / 200)) { prof[b] = (float)(m * Norm); ok[b] = true; }
        }
        for (int b = 0; b < bins; b++)
        {
            if (ok[b]) continue;
            int lo = b - 1, hi = b + 1;
            while (lo >= 0 && !ok[lo]) lo--;
            while (hi < bins && !ok[hi]) hi++;
            prof[b] = lo >= 0 && hi < bins ? (prof[lo] + prof[hi]) / 2 : lo >= 0 ? prof[lo] : hi < bins ? prof[hi] : (float)global;
        }
        return prof;
    }

    private static double NoiseGlobal(float[] P, int w, int h)
    {
        NoiseProfile(P, null, w, h, 1, out double g);
        return g;
    }

    private static float Sample(float[] prof, float v)
    {
        float x = Clamp01(v) * prof.Length - 0.5f;
        if (x <= 0) return prof[0];
        int i = (int)x;
        if (i >= prof.Length - 1) return prof[prof.Length - 1];
        float t = x - i;
        return prof[i] + (prof[i + 1] - prof[i]) * t;
    }

    // =================================================================
    //  Tone: levels + clipped partial equalization + contrast
    // =================================================================

    private const int LutSize = 4096;

    private static float[] BuildToneLut(float[] L, double balance, double contrast, AutoResult res)
    {
        const int HB = 1024;
        var hist = new double[HB];
        foreach (var v in L) hist[Math.Min(HB - 1, (int)(Clamp01(v) * HB))]++;
        double total = L.Length;

        double Pct(double q)
        {
            double acc = 0, target = q * total;
            for (int k = 0; k < HB; k++)
            {
                acc += hist[k];
                if (acc >= target) return (k + 0.5) / HB;
            }
            return 1;
        }

        // Levels: black / white point from the histogram tails, limited so dark or bright scenes keep their key.
        double black = Math.Min(Pct(0.0005), 0.15);
        double white = Math.Max(Pct(0.9995), 0.70);
        if (white - black < 0.3) { double c = (white + black) / 2; black = c - 0.15; white = c + 0.15; }
        double bl = black * balance, wh = 1 - (1 - white) * balance;
        res.Black = bl;
        res.White = wh;
        Func<double, double> levels = x => Clamp((x - bl) / (wh - bl), 0, 1);

        // Histogram of the levelled image, CLAHE-like clip, CDF.
        const int EB = 256;
        var eh = new double[EB];
        for (int k = 0; k < HB; k++)
            eh[Math.Min(EB - 1, (int)(levels((k + 0.5) / HB) * EB))] += hist[k];
        double limit = 2.5 * total / EB, excess = 0;
        for (int k = 0; k < EB; k++)
            if (eh[k] > limit) { excess += eh[k] - limit; eh[k] = limit; }
        for (int k = 0; k < EB; k++) eh[k] += excess / EB;
        var cdf = new double[EB + 1];
        for (int k = 0; k < EB; k++) cdf[k + 1] = cdf[k] + eh[k];
        for (int k = 0; k <= EB; k++) cdf[k] /= cdf[EB];
        double eqMix = 0.45 * balance;
        Func<double, double> stage1 = x =>
        {
            double v = levels(x);
            double f = v * EB;
            int i = Math.Min(EB - 1, (int)f);
            double eq = cdf[i] + (cdf[i + 1] - cdf[i]) * (f - i);
            return v + (eq - v) * eqMix;
        };

        // Contrast: find the curve strength that brings the tonal spread to ~0.21, then scale by the slider.
        double g = 1;
        double pivot = 0.5;
        if (contrast > 0)
        {
            var mids = new double[HB];
            double med = 0, acc = 0;
            for (int k = 0; k < HB; k++)
            {
                mids[k] = stage1((k + 0.5) / HB);
                acc += hist[k];
                if (med == 0 && acc >= total / 2) med = mids[k];
            }
            pivot = Clamp(med, 0.3, 0.6);
            double Std(double gg)
            {
                double s = 0, s2 = 0;
                for (int k = 0; k < HB; k++)
                {
                    double v = ContrastCurve(mids[k], pivot, gg);
                    s += v * hist[k];
                    s2 += v * v * hist[k];
                }
                double m = s / total;
                return Math.Sqrt(Math.Max(0, s2 / total - m * m));
            }
            const double Target = 0.21;
            double lo = 0.75, hi = 2.0;
            if (Std(hi) <= Target) g = hi;
            else if (Std(lo) >= Target) g = lo;
            else
            {
                for (int it = 0; it < 30; it++)
                {
                    double mid = (lo + hi) / 2;
                    if (Std(mid) < Target) lo = mid; else hi = mid;
                }
                g = (lo + hi) / 2;
            }
            g = 1 + (g - 1) * contrast;
        }
        res.Gamma = g;

        var lut = new float[LutSize];
        for (int i = 0; i < LutSize; i++)
        {
            double x = i / (double)(LutSize - 1);
            lut[i] = (float)ContrastCurve(stage1(x), pivot, g);
        }
        for (int i = 1; i < LutSize; i++) if (lut[i] < lut[i - 1]) lut[i] = lut[i - 1];
        return lut;
    }

    /// <summary>Slope <paramref name="g"/> at the pivot, ends compressed but never clipped (slope 1/g at 0 and 1).</summary>
    private static double ContrastCurve(double x, double p, double g)
    {
        if (Math.Abs(g - 1) < 1e-6) return x;
        double F(double t) => g * t / (1 + (g - 1) * t);
        if (x >= p)
        {
            double t = (x - p) / (1 - p);
            return p + (1 - p) * F(Clamp(t, 0, 1));
        }
        else
        {
            double t = (p - x) / p;
            return p - p * F(Clamp(t, 0, 1));
        }
    }

    private static void ApplyLumaLut(PluginImage img, float[] L, float[] lut)
    {
        int w = img.Width, h = img.Height;
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++)
            {
                float l = L[i];
                float nl = lut[(int)(Clamp01(l) * (LutSize - 1) + 0.5f)];
                float r = img.R[i], g = img.G[i], b = img.B[i];
                // Ratio keeps hue/saturation; additive in the deep shadows avoids amplifying color noise.
                float wgt = SmoothStepF(0f, 0.05f, l);
                float k = l > 1e-5f ? Math.Min(4f, nl / l) : 1f;
                float d = nl - l;
                r = r + d + (r * k - r - d) * wgt;
                g = g + d + (g * k - g - d) * wgt;
                b = b + d + (b * k - b - d) * wgt;
                float m = Math.Max(r, Math.Max(g, b));
                if (m > 1f && m - nl > 1e-6f)
                {
                    float t = (1f - nl) / (m - nl);
                    r = nl + (r - nl) * t;
                    g = nl + (g - nl) * t;
                    b = nl + (b - nl) * t;
                }
                img.R[i] = Clamp01(r);
                img.G[i] = Clamp01(g);
                img.B[i] = Clamp01(b);
            }
        });
    }

    // =================================================================
    //  White balance and color
    // =================================================================

    private const int ColorAnalysisSize = 512;

    /// <summary>
    /// Illuminant from near-neutral pixels (linear light): starts from shades-of-gray (p = 6), then iteratively
    /// averages the pixels that are close to gray after the current correction. Clipped and very dark pixels are ignored.
    /// The gains are normalized so luminance is unchanged; strong casts are compressed (part of the mood is kept)
    /// and the correction fades out when few neutral pixels exist. Inputs are sRGB-encoded; returns false for no change.
    /// </summary>
    public static bool EstimateWhiteBalance(float[] sR, float[] sG, float[] sB, double amount, AutoResult res)
    {
        int n = sR.Length;
        var R = new float[n]; var G = new float[n]; var B = new float[n]; var ok = new bool[n];
        var sy = new double[n];
        int valid = 0;
        double totalY = 0;
        for (int i = 0; i < n; i++)
        {
            float r = ToLinear(sR[i]), g = ToLinear(sG[i]), b = ToLinear(sB[i]);
            R[i] = r; G[i] = g; B[i] = b;
            float mx = Math.Max(r, Math.Max(g, b));
            float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            ok[i] = mx < 0.95f && y > 0.008f && Math.Min(r, Math.Min(g, b)) > 1e-4f;
            if (!ok[i]) continue;
            valid++;
            sy[i] = Math.Sqrt(y);
            totalY += sy[i];
        }
        if (valid < n / 50 || valid < 200) return false;

        // Shades of gray.
        double er = 0, eg = 0, eb = 0;
        for (int i = 0; i < n; i++)
        {
            if (!ok[i]) continue;
            er += Math.Pow(R[i], 6); eg += Math.Pow(G[i], 6); eb += Math.Pow(B[i], 6);
        }
        er = Math.Pow(er / valid, 1 / 6.0); eg = Math.Pow(eg / valid, 1 / 6.0); eb = Math.Pow(eb / valid, 1 / 6.0);

        // Gray pixel refinement.
        double frac = 0;
        const double Sigma = 0.12;
        for (int it = 0; it < 6; it++)
        {
            double lr0 = Math.Log(er / eg), lb0 = Math.Log(eb / eg);
            double wr = 0, wg = 0, wbb = 0, wsum = 0;
            for (int i = 0; i < n; i++)
            {
                if (!ok[i]) continue;
                double u = Math.Log(R[i] / G[i]) - lr0, v = Math.Log(B[i] / G[i]) - lb0;
                double d2 = u * u + v * v;
                if (d2 > 9 * Sigma * Sigma) continue;
                double y = sy[i] * sy[i];
                double wgt = Math.Exp(-d2 / (2 * Sigma * Sigma)) * sy[i];
                // Each pixel contributes its chromaticity (divided by luminance) so bright areas do not dominate.
                wr += wgt * R[i] / y; wg += wgt * G[i] / y; wbb += wgt * B[i] / y;
                wsum += wgt;
            }
            if (wsum <= 1e-9) break;
            double nr = wr / wsum, ng = wg / wsum, nb = wbb / wsum;
            double ar = Math.Log(nr / ng) - lr0, ab = Math.Log(nb / ng) - lb0;
            er = nr; eg = ng; eb = nb;
            frac = wsum / totalY;
            if (ar * ar + ab * ab < 1e-6) break;
        }

        // Cast in log-chromaticity; compress strong ones (sunsets, stage lights, candles stay warm but less so).
        double cr = Math.Log(er / eg), cb = Math.Log(eb / eg);
        // Real light sources lie near the blackbody (warm/cool) axis; a strong green/magenta "illuminant" is far more
        // likely the scene itself (foliage, a pink wall), so the tint component is limited.
        double temp = (cr - cb) / Math.Sqrt(2), tint = (cr + cb) / Math.Sqrt(2);
        tint = Clamp(tint, -0.08, 0.08);
        cr = (temp + tint) / Math.Sqrt(2); cb = (tint - temp) / Math.Sqrt(2);
        double mag = Math.Sqrt(cr * cr + cb * cb);
        res.WbConfidence = frac;
        if (mag < 0.01) return false;
        double target = mag <= 0.25 ? mag : 0.25 + (mag - 0.25) * 0.5;
        target = Math.Min(target, 0.8);
        double k = amount * (target / mag) * SmoothStep(0.03, 0.15, frac);
        if (k * mag < 0.005) return false;

        double gr = Math.Exp(-cr * k), gb = Math.Exp(-cb * k), gg = 1;
        double ly = 0.2126 * gr + 0.7152 * gg + 0.0722 * gb;
        res.GainR = gr / ly; res.GainG = gg / ly; res.GainB = gb / ly;
        return true;
    }

    /// <summary>Applies linear-light gains to an sRGB-encoded image through per-channel LUTs; near-clipped pixels keep their color.</summary>
    private static void ApplyGains(PluginImage img, double gr, double gg, double gb)
    {
        const int N = 4096;
        var lr = new float[N + 1]; var lg = new float[N + 1]; var lb = new float[N + 1];
        for (int i = 0; i <= N; i++)
        {
            float lin = ToLinear(i / (float)N);
            lr[i] = ToSrgb((float)(lin * gr)); lg[i] = ToSrgb((float)(lin * gg)); lb[i] = ToSrgb((float)(lin * gb));
        }
        int w = img.Width;
        Parallel.For(0, img.Height, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++)
            {
                float r = img.R[i], g = img.G[i], b = img.B[i];
                float nr = Lut(lr, r), ng = Lut(lg, g), nb = Lut(lb, b);
                // Clipped highlights carry no color information: blend back to the original near white.
                float t = SmoothStepF(0.92f, 0.99f, Math.Max(r, Math.Max(g, b)));
                img.R[i] = nr + (r - nr) * t;
                img.G[i] = ng + (g - ng) * t;
                img.B[i] = nb + (b - nb) * t;
            }
        });
    }

    private static float Lut(float[] lut, float v)
    {
        float x = Clamp01(v) * (lut.Length - 1);
        int i = (int)x;
        if (i >= lut.Length - 1) return lut[lut.Length - 1];
        float f = x - i;
        return lut[i] + (lut[i + 1] - lut[i]) * f;
    }

    /// <summary>
    /// Vibrance strength from the mean saturation (after white balance): dull photos get a boost, very saturated ones
    /// a slight reduction. Inputs are sRGB-encoded analysis copies.
    /// </summary>
    public static double VibranceStrength(float[] sR, float[] sG, float[] sB, AutoResult res, double amount)
    {
        double gr = res.GainR, gg = res.GainG, gb = res.GainB;
        bool gains = Math.Abs(gr - 1) > 1e-4 || Math.Abs(gb - 1) > 1e-4;
        double sum = 0; int cnt = 0;
        for (int i = 0; i < sR.Length; i++)
        {
            float r = sR[i], g = sG[i], b = sB[i];
            if (gains) { r = ToSrgb((float)(ToLinear(r) * gr)); g = ToSrgb((float)(ToLinear(g) * gg)); b = ToSrgb((float)(ToLinear(b) * gb)); }
            float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            if (mx < 0.08f) continue;
            sum += (mx - mn) / mx; cnt++;
        }
        if (cnt < 100) return 0;
        double s = sum / cnt;
        res.Saturation = s;
        const double Target = 0.32;
        double v = s < Target ? (Target - s) / Target * 0.9 : -(s - Target) / (1 - Target) * 0.5;
        return Clamp(v, -0.12, 0.6) * amount;
    }

    /// <summary>Luma-preserving vibrance: low-saturation pixels change most, skin hues are protected when boosting.</summary>
    private static void ApplyVibrance(PluginImage img, float v)
    {
        int w = img.Width;
        Parallel.For(0, img.Height, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++)
            {
                float r = img.R[i], g = img.G[i], b = img.B[i];
                float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (mx <= 1e-4f) continue;
                float s = (mx - mn) / mx;
                float f;
                if (v > 0)
                {
                    // Skin: orange hues (R >= G >= B, G in the lower-middle of the R..B range), not strongly saturated.
                    float skin = 0f;
                    if (r >= g && g >= b && r - b > 1e-4f)
                    {
                        float hq = (g - b) / (r - b);
                        skin = SmoothStepF(0.1f, 0.3f, hq) * (1f - SmoothStepF(0.6f, 0.8f, hq)) * (1f - SmoothStepF(0.55f, 0.7f, s));
                    }
                    f = 1f + v * (1f - s) * (1f - s) * (1f - 0.7f * skin) * SmoothStepF(0.02f, 0.1f, mx);
                }
                else f = 1f + v * s * s;
                float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                r = l + (r - l) * f; g = l + (g - l) * f; b = l + (b - l) * f;
                // Keep in gamut along the line to gray.
                float m2 = Math.Max(r, Math.Max(g, b)), n2 = Math.Min(r, Math.Min(g, b));
                float t = 1f;
                if (m2 > 1f && m2 - l > 1e-6f) t = Math.Min(t, (1f - l) / (m2 - l));
                if (n2 < 0f && l - n2 > 1e-6f) t = Math.Min(t, l / (l - n2));
                if (t < 1f) { r = l + (r - l) * t; g = l + (g - l) * t; b = l + (b - l) * t; }
                img.R[i] = Clamp01(r); img.G[i] = Clamp01(g); img.B[i] = Clamp01(b);
            }
        });
    }

    // =================================================================
    //  Straightening
    // =================================================================

    /// <summary>
    /// Tilt (degrees) of the dominant near-horizontal / near-vertical straight lines. Positive = lines descend to the right
    /// (image y points down). Returns 0 when no long straight line is found or the result is ambiguous.
    /// </summary>
    public static string LastInfo = "";

    public static double DetectTilt(float[] L, int w, int h, double maxDeg, out double confidence)
    {
        confidence = 0;
        if (w < 32 || h < 32) return 0;
        var s = (float[])L.Clone();
        Box(s, w, h, 1);

        int n = w * h;
        var gx = new float[n];
        var gy = new float[n];
        var mag = new float[n];
        Parallel.For(1, h - 1, y =>
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                float a = s[i - w - 1], b = s[i - w], c = s[i - w + 1];
                float d = s[i - 1], f = s[i + 1];
                float g = s[i + w - 1], hh = s[i + w], k = s[i + w + 1];
                float sx = (c + 2 * f + k) - (a + 2 * d + g);
                float sy = (g + 2 * hh + k) - (a + 2 * b + c);
                gx[i] = sx;
                gy[i] = sy;
                mag[i] = (float)Math.Sqrt(sx * sx + sy * sy);
            }
        });

        // Threshold: strong edges only (top ~8%, absolute floor against noise).
        const int HB = 2048;
        var mh = new int[HB];
        float mMax = 0;
        for (int i = 0; i < n; i++) if (mag[i] > mMax) mMax = mag[i];
        if (mMax <= 0) return 0;
        for (int i = 0; i < n; i++) mh[Math.Min(HB - 1, (int)(mag[i] / mMax * (HB - 1)))]++;
        int accN = 0, kk = HB - 1;
        for (; kk > 0; kk--) { accN += mh[kk]; if (accN >= n * 0.08) break; }
        float thr = Math.Max(0.25f, kk / (float)(HB - 1) * mMax);

        double gateDeg = 4.0;
        double maxW = maxDeg + gateDeg;
        const double Step = 0.1;
        int nA = (int)Math.Round(2 * maxDeg / Step) + 1;
        double diag = Math.Sqrt((double)w * w + (double)h * h);
        int nR = (int)Math.Ceiling(2 * diag) + 3;
        double rOff = diag + 1;
        var accH = new int[nA * nR];
        var accV = new int[nA * nR];
        var sinT = new double[nA];
        var cosT = new double[nA];
        for (int a = 0; a < nA; a++)
        {
            double rad = (-maxDeg + a * Step) * Math.PI / 180;
            sinT[a] = Math.Sin(rad);
            cosT[a] = Math.Cos(rad);
        }
        double cx = w / 2.0, cy = h / 2.0;
        for (int y = 2; y < h - 2; y++)
        {
            for (int x = 2; x < w - 2; x++)
            {
                int i = y * w + x;
                float m = mag[i];
                if (m < thr) continue;
                // Non-maximum suppression along the gradient (4 directions).
                float ax = Math.Abs(gx[i]), ay = Math.Abs(gy[i]);
                int off;
                if (ay > 2.414f * ax) off = w;
                else if (ax > 2.414f * ay) off = 1;
                else off = (gx[i] > 0) == (gy[i] > 0) ? w + 1 : w - 1;
                if (m < mag[i - off] || m < mag[i + off]) continue;

                double phi = Math.Atan2(gy[i], gx[i]) * 180 / Math.PI;
                double la = phi - 90;
                while (la <= -90) la += 180;
                while (la > 90) la -= 180;
                double dx = x - cx, dy = y - cy;
                if (Math.Abs(la) <= maxW)
                {
                    int a0 = Math.Max(0, (int)Math.Floor((la - gateDeg + maxDeg) / Step));
                    int a1 = Math.Min(nA - 1, (int)Math.Ceiling((la + gateDeg + maxDeg) / Step));
                    for (int a = a0; a <= a1; a++)
                    {
                        int r = (int)Math.Round(-dx * sinT[a] + dy * cosT[a] + rOff);
                        accH[a * nR + r]++;
                    }
                }
                double dv = la > 0 ? la - 90 : la + 90;
                if (Math.Abs(dv) <= maxW)
                {
                    int a0 = Math.Max(0, (int)Math.Floor((dv - gateDeg + maxDeg) / Step));
                    int a1 = Math.Min(nA - 1, (int)Math.Ceiling((dv + gateDeg + maxDeg) / Step));
                    for (int a = a0; a <= a1; a++)
                    {
                        int r = (int)Math.Round(dx * cosT[a] + dy * sinT[a] + rOff);
                        accV[a * nR + r]++;
                    }
                }
            }
        }

        // Score per angle: squared votes of long lines (>= 12% of the side), verticals weighted lower (perspective).
        double minH = 0.12 * w, minV = 0.12 * h;
        var score = new double[nA];
        var longest = new double[nA];
        var lines = new int[nA];
        for (int a = 0; a < nA; a++)
        {
            // A tilted edge lands on whole pixels after NMS, so its votes spread over adjacent rho bins:
            // a line's strength is the 3-bin band sum, counted once at the band's local maximum.
            double sc = 0, best = 0;
            int o = a * nR;
            // Independent lines only: edges closer than 10% of the side (two sides of one object) count once.
            int lastH = int.MinValue / 2, lastV = int.MinValue / 2;
            int sepH = (int)(0.1 * h), sepV = (int)(0.1 * w);
            for (int r = 2; r < nR - 2; r++)
            {
                int i = o + r;
                double bh = accH[i - 1] + accH[i] + accH[i + 1];
                if (bh >= minH && bh >= accH[i - 2] + accH[i - 1] + accH[i] && bh > accH[i] + accH[i + 1] + accH[i + 2])
                {
                    sc += bh * bh;
                    best = Math.Max(best, bh / w);
                    if (r - lastH >= sepH) { lines[a]++; lastH = r; }
                }
                double bv = accV[i - 1] + accV[i] + accV[i + 1];
                if (bv >= minV && bv >= accV[i - 2] + accV[i - 1] + accV[i] && bv > accV[i] + accV[i + 1] + accV[i + 2])
                {
                    sc += 0.6 * bv * bv;
                    best = Math.Max(best, 0.8 * bv / h);
                    if (r - lastV >= sepV) { lines[a]++; lastV = r; }
                }
            }
            score[a] = sc;
            longest[a] = best;
        }

        var sm = new double[nA];
        for (int a = 0; a < nA; a++)
        {
            double sum = 0, wsum = 0;
            for (int d = -2; d <= 2; d++)
            {
                int j = a + d;
                if (j < 0 || j >= nA) continue;
                double wt = 3 - Math.Abs(d);
                sum += score[j] * wt;
                wsum += wt;
            }
            sm[a] = sum / wsum;
        }
        int bi = 0;
        for (int a = 1; a < nA; a++) if (sm[a] > sm[bi]) bi = a;
        {
            int mh2 = 0, mv2 = 0, edges = 0;
            for (int k = 0; k < accH.Length; k++) { if (accH[k] > mh2) mh2 = accH[k]; if (accV[k] > mv2) mv2 = accV[k]; }
            for (int k = 0; k < n; k++) if (mag[k] >= thr) edges++;
            LastInfo = FormattableString.Invariant($"thr {thr:0.000} edges {edges} maxH {mh2 / (double)w:0.000} maxV {mv2 / (double)h:0.000} best {-maxDeg + bi * Step:0.0} longest {longest[bi]:0.000} lines {lines[bi]}");
        }
        if (sm[bi] <= 0) return 0;

        // Confidence: a long line must exist, and the peak must stand out from the other angles.
        double mean = 0;
        for (int a = 0; a < nA; a++) mean += sm[a];
        mean /= nA;
        double ratio = sm[bi] / Math.Max(1e-9, mean);
        double len = 0;
        for (int d = -2; d <= 2; d++) { int j = bi + d; if (j >= 0 && j < nA) len = Math.Max(len, longest[j]); }
        int support = 0;
        for (int d = -2; d <= 2; d++) { int j = bi + d; if (j >= 0 && j < nA) support = Math.Max(support, lines[j]); }
        confidence = ratio * len;
        // Several independent straight lines must agree, unless one spans most of the frame (a horizon):
        // a single shorter edge (an arm, a shoulder, a shadow) is not evidence of a tilted camera.
        bool enough = (support >= 2 && len >= 0.2) || len >= 0.35;
        if (!enough || ratio < 2.0) return 0;

        double off2 = 0;
        if (bi > 0 && bi < nA - 1)
        {
            double y0 = sm[bi - 1], y1 = sm[bi], y2 = sm[bi + 1];
            double den = y0 - 2 * y1 + y2;
            if (Math.Abs(den) > 1e-12) off2 = Clamp(0.5 * (y0 - y2) / den, -0.5, 0.5);
        }
        double angle = -maxDeg + (bi + off2) * Step;
        // Peaks at the window edge are likely intentional tilts.
        if (Math.Abs(angle) > maxDeg - 0.15) return 0;
        return Math.Abs(angle) < 0.1 ? 0 : angle;
    }

    /// <summary>Rotates by <paramref name="deg"/> about the center and zooms just enough to leave no empty corners.</summary>
    private static void Rotate(PluginImage img, double deg, float[] t0, float[] t1)
    {
        int w = img.Width, h = img.Height;
        double rad = deg * Math.PI / 180;
        double c = Math.Cos(rad), s = Math.Sin(rad), as_ = Math.Abs(s);
        double zoom = Math.Max(c + as_ * h / w, c + as_ * w / h);
        double cx = w / 2.0, cy = h / 2.0;
        var t2 = (float[])img.B.Clone();
        Copy(img.R, t0);
        Copy(img.G, t1);
        float[] sr = t0, sg = t1, sb = t2;
        Parallel.For(0, h, y =>
        {
            double dy = y + 0.5 - cy;
            for (int x = 0; x < w; x++)
            {
                double dx = x + 0.5 - cx;
                double fx = (dx * c - dy * s) / zoom + cx - 0.5;
                double fy = (dx * s + dy * c) / zoom + cy - 0.5;
                if (fx < 0) fx = 0; else if (fx > w - 1) fx = w - 1;
                if (fy < 0) fy = 0; else if (fy > h - 1) fy = h - 1;
                int x0 = (int)fx, y0 = (int)fy;
                int x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
                float tx = (float)(fx - x0), ty = (float)(fy - y0);
                int i00 = y0 * w + x0, i01 = y0 * w + x1, i10 = y1 * w + x0, i11 = y1 * w + x1;
                int o = y * w + x;
                img.R[o] = Bil(sr, i00, i01, i10, i11, tx, ty);
                img.G[o] = Bil(sg, i00, i01, i10, i11, tx, ty);
                img.B[o] = Bil(sb, i00, i01, i10, i11, tx, ty);
            }
        });
    }

    private static float Bil(float[] p, int i00, int i01, int i10, int i11, float tx, float ty)
    {
        float a = p[i00] + (p[i01] - p[i00]) * tx;
        float b = p[i10] + (p[i11] - p[i10]) * tx;
        return a + (b - a) * ty;
    }

    // =================================================================
    //  Helpers
    // =================================================================

    /// <summary>Luma of the measurement copy after the white balance gains of <paramref name="res"/>.</summary>
    private static float[] BaselineLuma(Baseline b, AutoResult res)
    {
        var im = new PluginImage(b.W, b.H, (float[])b.R.Clone(), (float[])b.G.Clone(), (float[])b.B.Clone());
        if (Math.Abs(res.GainR - 1) > 1e-6 || Math.Abs(res.GainG - 1) > 1e-6 || Math.Abs(res.GainB - 1) > 1e-6)
            ApplyGains(im, res.GainR, res.GainG, res.GainB);
        var l = new float[im.Length];
        Luma(im, l);
        return l;
    }

    private static void Luma(PluginImage img, float[] dst)
    {
        int w = img.Width;
        Parallel.For(0, img.Height, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++)
                dst[i] = 0.2126f * img.R[i] + 0.7152f * img.G[i] + 0.0722f * img.B[i];
        });
    }

    /// <summary>Area-average downscale so the long side is at most <paramref name="max"/>.</summary>
    public static float[] Downscale(float[] src, int w, int h, int max, out int ow, out int oh)
    {
        int f = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / (double)max));
        ow = Math.Max(1, w / f);
        oh = Math.Max(1, h / f);
        if (f == 1) { ow = w; oh = h; return (float[])src.Clone(); }
        int oW = ow, oH = oh;
        var dst = new float[oW * oH];
        float inv = 1f / (f * f);
        Parallel.For(0, oH, y =>
        {
            for (int x = 0; x < oW; x++)
            {
                float sum = 0;
                for (int j = 0; j < f; j++)
                {
                    int row = (y * f + j) * w + x * f;
                    for (int k = 0; k < f; k++) sum += src[row + k];
                }
                dst[y * oW + x] = sum * inv;
            }
        });
        return dst;
    }

    private static void Copy(float[] src, float[] dst) => Array.Copy(src, dst, Math.Min(src.Length, dst.Length));

    /// <summary>In-place box mean with radius <paramref name="r"/> (window clipped at the borders).</summary>
    public static void Box(float[] p, int w, int h, int r)
    {
        if (r <= 0) return;
        // Horizontal.
        Parallel.For(0, h, () => new float[w], (y, _, tmp) =>
        {
            int row = y * w;
            double sum = 0;
            int cnt = 0;
            for (int x = 0; x <= Math.Min(r, w - 1); x++) { sum += p[row + x]; cnt++; }
            for (int x = 0; x < w; x++)
            {
                tmp[x] = (float)(sum / cnt);
                int add = x + r + 1, rem = x - r;
                if (add < w) { sum += p[row + add]; cnt++; }
                if (rem >= 0) { sum -= p[row + rem]; cnt--; }
            }
            Array.Copy(tmp, 0, p, row, w);
            return tmp;
        }, _ => { });
        // Vertical, in column blocks (the block is copied so the pass can write in place).
        const int B = 64;
        int blocks = (w + B - 1) / B;
        Parallel.For(0, blocks, () => (new float[h * B], new double[B], new int[1]), (bi, _, st) =>
        {
            var (buf, sums, _) = st;
            int x0 = bi * B, bw = Math.Min(B, w - x0);
            for (int y = 0; y < h; y++) Array.Copy(p, y * w + x0, buf, y * B, bw);
            Array.Clear(sums, 0, B);
            int cnt = 0;
            for (int y = 0; y <= Math.Min(r, h - 1); y++) { for (int k = 0; k < bw; k++) sums[k] += buf[y * B + k]; cnt++; }
            for (int y = 0; y < h; y++)
            {
                int o = y * w + x0;
                double inv = 1.0 / cnt;
                for (int k = 0; k < bw; k++) p[o + k] = (float)(sums[k] * inv);
                int add = y + r + 1, rem = y - r;
                if (add < h) { for (int k = 0; k < bw; k++) sums[k] += buf[add * B + k]; cnt++; }
                if (rem >= 0) { for (int k = 0; k < bw; k++) sums[k] -= buf[rem * B + k]; cnt--; }
            }
            return st;
        }, _ => { });
    }

    private static void EncodeSrgb(PluginImage img, bool toSrgb)
    {
        Parallel.For(0, img.Height, y =>
        {
            for (int i = y * img.Width, e = i + img.Width; i < e; i++)
            {
                img.R[i] = toSrgb ? ToSrgb(img.R[i]) : ToLinear(img.R[i]);
                img.G[i] = toSrgb ? ToSrgb(img.G[i]) : ToLinear(img.G[i]);
                img.B[i] = toSrgb ? ToSrgb(img.B[i]) : ToLinear(img.B[i]);
            }
        });
    }

    private static float ToSrgb(float v) { v = Clamp01(v); return v <= 0.0031308f ? v * 12.92f : 1.055f * (float)Math.Pow(v, 1 / 2.4) - 0.055f; }
    private static float ToLinear(float v) { v = Clamp01(v); return v <= 0.04045f ? v / 12.92f : (float)Math.Pow((v + 0.055f) / 1.055f, 2.4); }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    private static double SmoothStep(double a, double b, double x) { double t = Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    private static float SmoothStepF(float a, float b, float x) { float t = Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }
}
