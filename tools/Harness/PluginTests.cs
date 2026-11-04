using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using ImgViewer;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;

internal static partial class Program
{
    /// <summary>Harness-defined v1 plugin (old SDK) to exercise the adapter and .eye v4 key migration.</summary>
    private sealed class LegacyInvert : IPlugin
    {
        public string Name => "Legacy Invert";
        public string Description => "v1 test plugin";
        public IReadOnlyList<PluginFeature> Features { get; } = new[]
        {
            new PluginFeature("inv", "Invert", PluginFeatureKind.ToolbarToggle, Array.Empty<PluginParamDef>(), (buf, v) =>
            {
                for (int i = 0; i < buf.R.Length; i++)
                {
                    buf.R[i] = (ushort)(65535 - buf.R[i]);
                    buf.G[i] = (ushort)(65535 - buf.G[i]);
                    buf.B[i] = (ushort)(65535 - buf.B[i]);
                }
            }),
            new PluginFeature("gain", "Gain", PluginFeatureKind.PanelSlider, new[] { new PluginParamDef("Amount", 0, 100, 0) }, (buf, v) =>
            {
                double k = 1 + v[0] / 100.0;
                for (int i = 0; i < buf.R.Length; i++)
                    buf.R[i] = (ushort)Math.Min(65535, buf.R[i] * k);
            })
        };
    }

    private static LoadedPlugin TestPlugin(string id, params FilterDescriptor[] filters)
    {
        var lp = new LoadedPlugin
        {
            Id = id,
            Name = id,
            Api = 2,
            Enabled = true,
            Entry = new PluginFileEntry(id + ".dll", id + ".dll", "loaded")
        };
        int i = 0;
        foreach (var d in filters)
            lp.Filters.Add(new Registered<FilterDescriptor>
            {
                PluginId = id,
                PluginName = id,
                FullId = id + ":" + d.Id,
                LocalId = d.Id,
                D = d,
                Owner = lp,
                Index = i++
            });
        return lp;
    }

    private static Raw16Image MakeGradient16(int w, int h)
    {
        var img = new Raw16Image(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                img.R[i] = (ushort)(x * 65535 / Math.Max(1, w - 1));
                img.G[i] = (ushort)(y * 65535 / Math.Max(1, h - 1));
                img.B[i] = (ushort)(((x + y) % 7) * 9000);
            }
        return img;
    }

    private static bool AllInverted(Raw16Image a, Raw16Image b, int tol = 1)
    {
        for (int i = 0; i < a.R.Length; i++)
            if (Math.Abs(b.R[i] - (65535 - a.R[i])) > tol || Math.Abs(b.G[i] - (65535 - a.G[i])) > tol || Math.Abs(b.B[i] - (65535 - a.B[i])) > tol)
                return false;
        return true;
    }

    /// <summary>Sample plugins, one project and package each: folder (eyePlugins\folder), assembly suffix, plugin id.</summary>
    private static readonly List<(string Folder, string Asm, string Id)> SamplePlugins = new()
    {
        ("negative", "Negative", "sample.negative"), ("bwred", "BwRed", "sample.bwred"), ("splittone", "SplitTone", "sample.grade"),
        ("vignette", "Vignette", "sample.vignette"), ("netpbm", "Netpbm", "sample.netpbm"), ("webexport", "WebExport", "sample.web"),
        ("exposure", "ExposureInfo", "sample.exposure"), ("sky", "SkyMask", "sample.sky"), ("eventlog", "EventLog", "sample.eventlog")
    };

    private static void TestPluginSamples()
    {
        var appData = Environment.GetEnvironmentVariable("PHOTOGRAPHEREYE_APPDATA") ?? "";
        Check(appData.Length > 0 && SettingsStore.DirectoryPath == appData, "plugin tests use isolated app data dir");
        try { if (Directory.Exists(appData)) Directory.Delete(appData, true); } catch (Exception) { }
        Directory.CreateDirectory(appData);

        var dlls = SamplePlugins.Select(s => FindPluginDll(s.Folder, "ImgViewer.Plugins." + s.Asm + ".dll")).ToList();
        if (dlls.Any(d => d is null))
        {
            Check(false, "sample plugin dlls present (build eyePlugins/negative, bwred, ... first)");
            return;
        }

        // ---------------- load + registry (one package per plugin) ----------------
        var entries = dlls.Select(d => PluginManager.LoadFromFile(d!)).ToList();
        Check(entries.All(e => e.Error is null && e.Plugins.Count == 1 && e.Plugins[0].Error is null && e.Plugins[0].Api == 2),
            $"v2 samples load, one plugin per dll ({string.Join(", ", entries.Select(e => e.Error ?? e.Plugins.Count.ToString()))})");
        var all = entries.SelectMany(e => e.Plugins).ToList();
        Check(all.Select(p => p.Id).SequenceEqual(SamplePlugins.Select(s => s.Id)), "sample plugin ids unchanged (saved edits keep working)");
        var reg = new PluginRegistry(all);
        var neg = reg.FindFilter("sample.negative:negative");
        var bw = reg.FindFilter("sample.bwred:bwred");
        var vig = reg.FindFilter("sample.vignette:vignette");
        var grade = reg.FindFilter("sample.grade:grade");
        var lut = reg.FindFilter(BuiltInFilters.LutId);
        Check(neg is not null && bw is not null && vig is not null && grade is not null && lut is not null && reg.FindFilter(BuiltInFilters.LocalId) is not null,
            "namespaced filter ids (sample.*:*, core:lut, core:local)");
        Check(neg?.D.Placement == FilterPlacement.Toolbar && vig?.D.Stage == FilterStage.Output && bw?.D.Group == "Black & white",
            "filter placement / stage / group");
        Check(reg.FindImporter("x.PPM") is not null && reg.ImporterExtensions.Contains(".pgm"), "importer registered (.ppm/.pgm)");
        Check(reg.Exporters.Any(e => e.FullId == "sample.web:web") && reg.MetaProviders.Any(m => m.FullId == "sample.exposure:ev"),
            "exporter + metadata provider registered");
        Check(reg.FindMask("sample.sky:sky") is not null, "mask provider registered");
        Check(reg.Commands.Any(c => c.FullId == "sample.negative:toggle" && c.D.Shortcut == "Ctrl+Shift+N"), "command registered with shortcut");
        if (neg is null || bw is null || vig is null || grade is null || lut is null) return;

        // Param kinds
        var kinds = bw.D.Params.Select(p => p.Kind).ToList();
        Check(kinds.SequenceEqual(new[] { ParamKind.Slider, ParamKind.Choice, ParamKind.Toggle }), "bwred param kinds slider/choice/toggle");
        Check(grade.D.Params.Any(p => p.Kind == ParamKind.Color) && grade.D.Params.Any(p => p.Kind == ParamKind.Curve) &&
              vig.D.Params.Any(p => p.Kind == ParamKind.Point), "color / curve / point params");

        const int W = 64, H = 48;
        var src = MakeGradient16(W, H);
        PluginRenderJob Job(PluginEditState st, bool preview = false, CancellationToken ct = default) =>
            new() { Registry = reg, State = st, Meta = new ImageMeta { Width = W, Height = H }, IsPreview = preview, Cancel = ct };

        // ---------------- negative (toolbar) on 16-bit and 8-bit ----------------
        var st = new PluginEditState();
        Check(PluginPipeline.ApplyRaw16(src, FilterStage.Display, Job(st), W, H) is null, "no active filter: ApplyRaw16 returns null");
        st.Active.Add(neg.FullId);
        var inv = PluginPipeline.ApplyRaw16(src, FilterStage.Display, Job(st), W, H);
        Check(inv is not null && AllInverted(src, inv), "negative inverts 16-bit (Display stage)");
        Check(src.R[5] == (ushort)(5 * 65535 / (W - 1)), "ApplyRaw16 does not mutate the source");
        Check(PluginPipeline.ApplyRaw16(src, FilterStage.Output, Job(st), W, H) is null, "Display filter does not run in Output stage");

        var bmp = MakeSource(W, H);
        var invB = PluginPipeline.ApplyBitmap(bmp, FilterStage.Display, Job(st), W, H);
        bool inv8 = invB is not null;
        if (inv8)
        {
            var b1 = new byte[W * H * 4];
            var b2 = new byte[W * H * 4];
            new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(b1, W * 4, 0);
            new FormatConvertedBitmap(invB!, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(b2, W * 4, 0);
            for (int i = 0; i < b1.Length && inv8; i += 4)
                inv8 = b2[i] == (byte)(255 - b1[i]) && b2[i + 1] == (byte)(255 - b1[i + 1]) && b2[i + 2] == (byte)(255 - b1[i + 2]);
        }
        Check(inv8, "negative inverts 8-bit bitmap exactly");

        // ---------------- bwred: identity + effect ----------------
        var st2 = new PluginEditState();
        var mix = bw.D.Params.First(p => p.Id == "mix");
        st2.SetValue(bw, mix, 0);
        Check(!st2.IsFilterActive(bw) && PluginPipeline.ApplyRaw16(src, FilterStage.Display, Job(st2), W, H) is null, "bwred mix 0 is identity");
        st2.SetValue(bw, mix, 80);
        var bwOut = PluginPipeline.ApplyRaw16(src, FilterStage.Display, Job(st2), W, H);
        bool gray = bwOut is not null && Enumerable.Range(0, W * H).All(i => bwOut.R[i] == bwOut.G[i] && bwOut.G[i] == bwOut.B[i]);
        Check(gray, "bwred mix 80 / keep none produces monochrome");

        // ---------------- vignette: Output stage ----------------
        var st3 = new PluginEditState();
        st3.SetValue(vig, vig.D.Params.First(p => p.Id == "amount"), -80);
        var flat = new Raw16Image(W, H);
        Array.Fill(flat.R, (ushort)40000); Array.Fill(flat.G, (ushort)40000); Array.Fill(flat.B, (ushort)40000);
        Check(PluginPipeline.ApplyRaw16(flat, FilterStage.Display, Job(st3), W, H) is null, "vignette skipped in Display stage");
        var vo = PluginPipeline.ApplyRaw16(flat, FilterStage.Output, Job(st3), W, H);
        int ci = H / 2 * W + W / 2;
        Check(vo is not null && vo.R[0] < vo.R[ci] - 5000 && Math.Abs(vo.R[ci] - 40000) < 3000, $"vignette darkens corners in Output stage (corner {vo?.R[0]}, center {vo?.R[ci]})");

        // ---------------- masks ----------------
        var pimg = PluginPipeline.FromRaw16(src);
        var ctx = new RenderContext { Stage = FilterStage.Display, Space = PluginColorSpace.Srgb, FullWidth = W, FullHeight = H };
        var lin = MaskRender.Render(MaskRender.DefaultFor(MaskRender.Linear), pimg, ctx, reg);
        var rad = MaskRender.Render(MaskRender.DefaultFor(MaskRender.Radial), pimg, ctx, reg);
        var luma = MaskRender.Render(new MaskSpec { Kind = MaskRender.Luma, P = new[] { 0.5, 1.0, 0.05 } }, pimg, ctx, reg);
        bool Range01(float[]? m) => m is not null && m.Length == W * H && m.All(v => v >= 0f && v <= 1f) && m.Min() < 0.05f && m.Max() > 0.95f;
        Check(Range01(lin) && Range01(rad) && Range01(luma), "linear / radial / luma masks span 0..1");
        Check(rad is not null && rad[ci] > 0.95f && rad[0] < 0.05f, "radial mask: center in, corner out");
        var radInv = MaskRender.Render(MaskRender.DefaultFor(MaskRender.Radial) with { Invert = true }, pimg, ctx, reg);
        Check(radInv is not null && rad is not null && Math.Abs(radInv[ci] + rad[ci] - 1f) < 1e-4f, "inverted mask = 1 - mask");
        {
            // Rotated radial (P[5], degrees clockwise, pixel space): a thin horizontal ellipse turned by 90 / 45 degrees.
            double mn = Math.Min(W, H);
            double[] Ell(double angle) => new[] { 0.5, 0.5, 0.4 * mn / W, 0.08 * mn / H, 0.0, angle };
            float At(float[]? m, double ox, double oy) => m is null ? -1 : m[(int)(H / 2 + oy) * W + (int)(W / 2 + ox)];
            var r0 = MaskRender.Render(new MaskSpec { Kind = MaskRender.Radial, P = Ell(0) }, pimg, ctx, reg);
            var r90 = MaskRender.Render(new MaskSpec { Kind = MaskRender.Radial, P = Ell(90) }, pimg, ctx, reg);
            var rm270 = MaskRender.Render(new MaskSpec { Kind = MaskRender.Radial, P = Ell(-270) }, pimg, ctx, reg);
            var r45 = MaskRender.Render(new MaskSpec { Kind = MaskRender.Radial, P = Ell(45) }, pimg, ctx, reg);
            double d = 0.3 * mn, q = d / Math.Sqrt(2);
            Check(At(r0, d, 0) > 0.95f && At(r0, 0, d) < 0.05f && At(r90, d, 0) < 0.05f && At(r90, 0, d) > 0.95f,
                  "radial rotation: 90 degrees turns the long axis from horizontal to vertical");
            Check(At(r45, q, q) > 0.95f && At(r45, -q, q) < 0.05f && At(r45, d, 0) < 0.05f, "radial rotation: 45 degrees clockwise");
            Check(r90 is not null && rm270 is not null && r90.Zip(rm270).All(t => Math.Abs(t.First - t.Second) < 1e-4f), "radial rotation: 90 = -270");
            var r5 = MaskRender.Render(new MaskSpec { Kind = MaskRender.Radial, P = Ell(0).Take(5).ToArray() }, pimg, ctx, reg);
            Check(r5 is not null && r0 is not null && r5.SequenceEqual(r0), "radial rotation: 5 values = angle 0 (old masks unchanged)");
            Check(MaskRender.RadialAngle(new MaskSpec { Kind = "radial", P = Ell(120) }) == -60 && MaskRender.RadialAngle(new MaskSpec { Kind = "radial", P = Ell(-90) }) == 90 &&
                  MaskRender.RadialAngle(new MaskSpec { Kind = "radial", P = Ell(0).Take(5).ToArray() }) == 0 && MaskRender.RadialAngle(null) == 0,
                  "radial rotation: RadialAngle folds to -90..90");
            var api = PluginEditOps.ToMaskSpec(new MaskInfo { Kind = "radial", P = Ell(30) });
            Check(api?.P is { Length: 6 } ap && ap[5] == 30 && PluginEditOps.ToMaskInfo(api)!.P[5] == 30, "radial rotation: plugin API keeps P[5]");
        }
        var sky = MaskRender.Render(new MaskSpec { Kind = "plugin:sample.sky:sky" }, pimg, ctx, reg);
        Check(sky is not null && sky.Length == W * H, "plugin mask provider renders");

        var st4 = new PluginEditState();
        st4.Active.Add(neg.FullId);
        st4.Masks[neg.FullId] = MaskRender.DefaultFor(MaskRender.Radial);
        var masked = PluginPipeline.ApplyRaw16(src, FilterStage.Display, Job(st4), W, H);
        Check(masked is not null && Math.Abs(masked.R[ci] - (65535 - src.R[ci])) < 300 && Math.Abs(masked.R[0] - src.R[0]) < 300,
            "masked negative: inverted in center, untouched in corner");

        // ---------------- LUT ----------------
        var idLut = CubeLut.Parse(new[] { "TITLE \"id\"", "LUT_3D_SIZE 2", "0 0 0", "1 0 0", "0 1 0", "1 1 0", "0 0 1", "1 0 1", "0 1 1", "1 1 1" });
        var li = PluginPipeline.FromRaw16(src);
        idLut.Apply(li, 1f, false);
        var orig = PluginPipeline.FromRaw16(src);
        float maxd = 0;
        for (int i = 0; i < li.Length; i++) maxd = Math.Max(maxd, Math.Abs(li.R[i] - orig.R[i]) + Math.Abs(li.G[i] - orig.G[i]) + Math.Abs(li.B[i] - orig.B[i]));
        Check(maxd < 1e-3f, $"identity .cube LUT is a no-op (max diff {maxd:0.00000})");
        var warm = CubeLut.Builtin("warm");
        bool warmOk = false;
        if (warm is not null)
        {
            var g = new PluginImage(1, 1, new[] { 0.5f }, new[] { 0.5f }, new[] { 0.5f });
            warm.Apply(g, 1f, false);
            warmOk = g.R[0] > g.B[0] + 0.01f;
        }
        Check(warmOk, "builtin 'warm' profile warms gray");

        // ---------------- presets ----------------
        var presets = PresetStore.FromRegistry(reg);
        var red = presets.FirstOrDefault(p => p.PluginId == "sample.bwred");
        Check(red is not null && red.Values.ContainsKey("sample.bwred:bwred/mix") && red.Values.ContainsKey("core.Contrast"),
            $"plugin presets resolved to full ids ({presets.Count} presets)");
        var presetDir = Path.Combine(appData, "PresetsTest");
        PresetStore.Save(new UserPreset { Name = "Mine", Values = { ["core.Exposure"] = 0.5, ["sample.bwred:bwred/mix"] = 40 } }, presetDir);
        var mine = PresetStore.LoadUser(presetDir);
        Check(mine.Count == 1 && mine[0].Name == "Mine" && mine[0].Values["sample.bwred:bwred/mix"] == 40, "user preset save / load");

        // ---------------- .eye v5 roundtrip + v1 migration ----------------
        var tmpImg = Path.Combine(appData, "eye-test.jpg");
        File.WriteAllBytes(tmpImg, new byte[] { 0 });
        var st5 = new PluginEditState();
        st5.Active.Add(neg.FullId);
        st5.SetValue(bw, mix, 33);
        st5.SetText(grade, grade.D.Params.First(p => p.Id == "shadows"), "#112233");
        st5.Order.AddRange(new[] { bw.FullId, neg.FullId });
        st5.Masks[neg.FullId] = MaskRender.DefaultFor(MaskRender.Linear);
        var sv = st5.Save();
        EyeFile.Save(tmpImg, new EyeState { PluginValues = sv.Values, PluginActive = sv.Active, PluginText = sv.Text, PluginOrder = sv.Order, PluginMasks = sv.Masks });
        bool eyeOk = EyeFile.TryLoad(tmpImg, out var eyeBack);
        var st5b = new PluginEditState();
        if (eyeOk) st5b.Load(eyeBack, reg);
        Check(eyeOk && eyeBack.Version == 5 && st5b.Active.Contains(neg.FullId) && st5b.Values[PluginEditState.Key(bw.FullId, "mix")] == 33 &&
              st5b.Text[PluginEditState.Key(grade.FullId, "shadows")] == "#112233" && st5b.Order.SequenceEqual(sv.Order!) &&
              st5b.Masks[neg.FullId].Kind == MaskRender.Linear, ".eye v5 plugin state roundtrip (values/text/active/order/masks)");
        var ordered = st5b.Ordered(reg, FilterStage.Display).Select(f => f.FullId).ToList();
        Check(ordered.IndexOf(bw.FullId) >= 0 && ordered.IndexOf(bw.FullId) < ordered.IndexOf(neg.FullId), "user filter order honored");

        var unknown = new PluginEditState();
        unknown.Load(new EyeState { PluginValues = new() { ["missing.plugin:f/p"] = 7 }, PluginActive = new() { "missing.plugin:t" } }, reg);
        var unknownSave = unknown.Save();
        Check(unknownSave.Values?["missing.plugin:f/p"] == 7 && unknownSave.Active?.Contains("missing.plugin:t") == true, "state of missing plugins preserved");

        var v1 = new LoadedPlugin
        {
            Id = PluginManager.V1PluginId("Legacy.dll", "Legacy Invert"),
            Name = "Legacy Invert",
            Api = 1,
            Enabled = true,
            Entry = new PluginFileEntry("Legacy.dll", "Legacy.dll", "loaded")
        };
        V1Adapter.Register(v1, new LegacyInvert());
        var regV1 = new PluginRegistry(new[] { v1 });
        var invF = regV1.FindFilter(v1.Id + ":inv");
        var gainF = regV1.FindFilter(v1.Id + ":gain");
        Check(v1.Id == "v1.legacy.legacy-invert" && invF?.D.Placement == FilterPlacement.Toolbar && gainF?.D.Params.Count == 1, $"v1 adapter ids/placement ({v1.Id})");
        var mig = new PluginEditState();
        mig.Load(new EyeState { Version = 4, PluginActive = new() { "inv" }, PluginValues = new() { ["gain"] = 50 } }, regV1);
        Check(mig.Active.Contains(v1.Id + ":inv") && mig.Values.TryGetValue(v1.Id + ":gain/p0", out var gv) && gv == 50, "legacy .eye v4 keys migrated");
        var v1Out = PluginPipeline.ApplyRaw16(src, FilterStage.Display,
            new PluginRenderJob { Registry = regV1, State = new PluginEditState { Active = { v1.Id + ":inv" } } }, W, H);
        Check(v1Out is not null && AllInverted(src, v1Out), "v1 filter runs through adapter");

        // ---------------- auto-disable after 3 failures ----------------
        int calls = 0;
        bool shouldThrow = true;
        var bad = TestPlugin("test.bad", new FilterDescriptor
        {
            Id = "boom",
            Placement = FilterPlacement.Toolbar,
            Render = (img, p, c) => { calls++; if (shouldThrow) throw new InvalidOperationException("boom"); }
        });
        var regBad = new PluginRegistry(new[] { bad });
        var stBad = new PluginEditState { Active = { "test.bad:boom" } };
        PluginRenderJob BadJob() => new() { Registry = regBad, State = stBad };
        var j1 = BadJob();
        PluginPipeline.ApplyRaw16(src, FilterStage.Display, j1, W, H);
        Check(j1.Report.Errors.Count == 1 && bad.ConsecutiveFailures == 1 && !bad.Faulted, "plugin exception reported, not fatal");
        shouldThrow = false;
        PluginPipeline.ApplyRaw16(src, FilterStage.Display, BadJob(), W, H);
        Check(bad.ConsecutiveFailures == 0, "success resets failure counter");
        shouldThrow = true;
        for (int k = 0; k < 3; k++) PluginPipeline.ApplyRaw16(src, FilterStage.Display, BadJob(), W, H);
        int before = calls;
        var after = PluginPipeline.ApplyRaw16(src, FilterStage.Display, BadJob(), W, H);
        Check(bad.Faulted && after is null && calls == before, "plugin auto-disabled after 3 consecutive failures");
        var settings = SettingsStore.Load();
        Check(settings.PluginFiles?.TryGetValue(PluginManager.EnabledKey("test.bad"), out var en) == true && !en &&
              settings.PluginDisabledReason?.ContainsKey("test.bad") == true, "auto-disable persisted with reason");

        // ---------------- superseded preview: job token cancelled and disposed mid-render ----------------
        // The viewer cancels and disposes the previous render's token source when a new preview starts (e.g. while
        // dragging a heal spot); that must count as a cancellation, not as a plugin failure.
        var slow = TestPlugin("test.slow", new FilterDescriptor
        {
            Id = "slow",
            Placement = FilterPlacement.Toolbar,
            Render = (img, p, c) => Thread.Sleep(300)
        });
        // The race that disabled heal: the job is cancelled and its source disposed after the stage loop checked
        // cancellation but before the filter render starts waiting (mask render, image copy). A plugin mask that
        // does exactly that makes it deterministic.
        CancellationTokenSource? racing = null;
        slow.Masks.Add(new Registered<MaskProviderDescriptor>
        {
            PluginId = "test.slow", PluginName = "test.slow", FullId = "test.slow:killer", LocalId = "killer", Owner = slow,
            D = new MaskProviderDescriptor
            {
                Id = "killer",
                CreateMask = (img, p, c) => { racing?.Cancel(); racing?.Dispose(); return Enumerable.Repeat(1f, img.Width * img.Height).ToArray(); }
            }
        });
        var regSlow = new PluginRegistry(new[] { slow });
        int cancelled = 0;
        for (int k = 0; k < 4; k++)
        {
            var scts = new CancellationTokenSource();
            var sst = new PluginEditState { Active = { "test.slow:slow" } };
            if (k % 2 == 0) { racing = scts; sst.Masks["test.slow:slow"] = new MaskSpec { Kind = MaskRender.PluginPrefix + "test.slow:killer" }; }
            else racing = null;
            var sj = new PluginRenderJob { Registry = regSlow, State = sst, IsPreview = true, Cancel = scts.Token };
            var killer = racing is null ? System.Threading.Tasks.Task.Run(() => { Thread.Sleep(60); scts.Cancel(); scts.Dispose(); }) : null;
            try { PluginPipeline.ApplyRaw16(src, FilterStage.Display, sj, W, H); }
            catch (OperationCanceledException) { cancelled++; }
            killer?.Wait();
        }
        racing = null;
        var gone = new CancellationTokenSource();
        var goneToken = gone.Token;
        gone.Cancel();
        gone.Dispose();
        try
        {
            PluginPipeline.ApplyRaw16(src, FilterStage.Display,
                new PluginRenderJob { Registry = regSlow, State = new PluginEditState { Active = { "test.slow:slow" } }, IsPreview = true, Cancel = goneToken }, W, H);
        }
        catch (OperationCanceledException) { cancelled++; }
        Check(cancelled == 5 && slow.ConsecutiveFailures == 0 && !slow.Faulted,
              $"preview superseded by a disposed token is a cancellation, not a plugin failure ({cancelled}/5 cancelled, {slow.ConsecutiveFailures} failures)");

        // ---------------- preview timeout ----------------
        var hang = TestPlugin("test.hang", new FilterDescriptor
        {
            Id = "hang",
            Placement = FilterPlacement.Toolbar,
            Render = (img, p, c) => c.Cancel.WaitHandle.WaitOne(30000)
        });
        var regHang = new PluginRegistry(new[] { hang });
        using (var cts = new CancellationTokenSource())
        {
            var sw = Stopwatch.StartNew();
            var hj = new PluginRenderJob { Registry = regHang, State = new PluginEditState { Active = { "test.hang:hang" } }, IsPreview = true, Cancel = cts.Token };
            var hr = PluginPipeline.ApplyRaw16(src, FilterStage.Display, hj, W, H);
            sw.Stop();
            cts.Cancel();
            Check(hang.Faulted && hj.Report.Errors.Count > 0 && sw.ElapsedMilliseconds >= PluginManager.PreviewTimeoutMs - 500 && sw.ElapsedMilliseconds < PluginManager.PreviewTimeoutMs + 4000,
                $"hung preview filter times out and is disabled ({sw.ElapsedMilliseconds} ms)");
        }

        // ---------------- package: create / inspect / install / import ----------------
        string MakePkg(string folder, string dll, string[] perms)
        {
            var dir = Path.Combine(appData, "pkgsrc-" + folder);
            Directory.CreateDirectory(dir);
            File.Copy(dll, Path.Combine(dir, Path.GetFileName(dll)), true);
            var pkgPath = Path.Combine(appData, folder + PluginPackage.Extension);
            PluginPackage.Create(dir, new PackageManifest { Id = "photographereye." + folder, Name = folder, Version = "2.0", Entry = Path.GetFileName(dll), Permissions = perms }, pkgPath);
            return pkgPath;
        }
        string negDll = dlls[0]!, pbmDll = dlls[SamplePlugins.FindIndex(s => s.Folder == "netpbm")]!;
        var pkg = MakePkg("negative", negDll, Array.Empty<string>());
        var pkgPbm = MakePkg("netpbm", pbmDll, new[] { "filesystem" });
        var insp = PluginPackage.Inspect(pkg);
        Check(insp.IsPackage && insp.PackageSha256.Length == 64 && insp.EntrySha256 == PluginTrust.Sha256File(negDll) &&
              insp.PluginIds.Contains("photographereye.negative") && insp.PluginTypes.Count == 1 && !insp.Signature.Signed,
            $"pepkg inspect: sha256, ids, types without executing code ({insp.PluginTypes.Count} types, {insp.Signature.Display})");

        var mgr = PluginManager.Instance;
        mgr.ScanDirs.Clear();
        mgr.ScanDirs.Add((PluginManager.ImportDir, "imported"));
        mgr.Install(insp);
        mgr.Install(PluginPackage.Inspect(pkgPbm));
        var installed = mgr.Files.FirstOrDefault(f => f.IsPackage && f.Plugins.Any(p => p.Id == "sample.negative"));
        Check(installed is not null && installed.Error is null && mgr.Registry.FindFilter("sample.negative:negative") is not null &&
              File.Exists(Path.Combine(PluginManager.ImportDir, insp.InstallFolder, PluginPackage.ManifestFileName)),
            "pepkg installs into import dir and loads after Reload");

        // ---------------- importer via host bridge ----------------
        var ppm = Path.Combine(appData, "test.ppm");
        using (var fs = File.Create(ppm))
        {
            var hdr = System.Text.Encoding.ASCII.GetBytes("P6\n4 2\n255\n");
            fs.Write(hdr, 0, hdr.Length);
            for (int i = 0; i < 8; i++) { fs.WriteByte((byte)(i * 30)); fs.WriteByte(128); fs.WriteByte(255); }
        }
        var imp = PluginImport.Find(ppm);
        BitmapSource? ib = null;
        try { if (imp is not null) ib = PluginImport.Load(imp, ppm, 0); } catch (Exception ex) { Console.WriteLine("  import: " + ex.Message); }
        bool ppmOk = false;
        if (ib is not null && ib.PixelWidth == 4 && ib.PixelHeight == 2)
        {
            var px = new byte[4 * 2 * 4];
            ib.CopyPixels(px, 16, 0);
            ppmOk = px[0] == 255 && px[1] == 128 && px[2] == 0 && px[4 * 4 + 2] == 120;
        }
        Check(ppmOk, "netpbm importer loads P6 through PluginImport");

        mgr.SetPluginEnabled("sample.negative", false);
        Check(mgr.Registry.FindFilter("sample.negative:negative") is null && mgr.Registry.FindImporter("x.ppm") is not null,
            "disabling one plugin leaves the other packages loaded");
        mgr.Install(PluginPackage.Inspect(pkg));
        Check(mgr.Registry.FindFilter("sample.negative:negative") is not null, "reinstalling a package re-enables its plugins");
        foreach (var f in mgr.Files.Where(f => f.IsPackage).ToList()) mgr.Remove(f.FileName);
        Check(mgr.Files.Count == 0 && !Directory.Exists(Path.Combine(PluginManager.ImportDir, insp.InstallFolder)), "imported packages removed");
    }
}
