using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using ImgViewer.Plugins;
using ImgViewer;
using ImgViewer.Imaging;
using ImgViewer.Services;

internal static partial class Program
{
    /// <summary>Command context of the B&W profile tests: a parameter store and a scripted Ask.</summary>
    public class BwContextProxy : DispatchProxy
    {
        public Dictionary<string, double> Values = new();
        public Dictionary<string, string> Text = new();
        public Func<string, IReadOnlyList<ParamDef>, FilterParams?> AskReply = (_, _) => null;
        public string Status = "";

        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            switch (m?.Name)
            {
                case "GetValue": return Values.TryGetValue((string)a![1]!, out var v) ? v : double.NaN;
                case "SetValue": Values[(string)a![1]!] = (double)a[2]!; return null;
                case "GetText": return Text.TryGetValue((string)a![1]!, out var t) ? t : null;
                case "SetText": Text[(string)a![1]!] = (string)a[2]!; return null;
                case "Ask": return AskReply((string)a![0]!, (IReadOnlyList<ParamDef>)a[1]!);
                case "SetStatus": Status = (string)a![0]!; return null;
                case "ShowMessage": Status = "message: " + (string)a![0]!; return null;
            }
            return m is not null && m.ReturnType.IsValueType && m.ReturnType != typeof(void) ? Activator.CreateInstance(m.ReturnType) : null;
        }
    }

    /// <summary>
    /// B&W profiles plugin: monochrome output, colour-filter profiles move colours the right way, strength blends,
    /// toning tints, grain is deterministic and weaker in downscaled previews, custom profiles survive the file
    /// round trip and are saved / deleted through the section commands (the look of the photo stays the same).
    /// </summary>
    private static void TestBwProfiles()
    {
        var path = FindPluginDll("bwprofiles", "ImgViewer.Plugins.BwProfiles.dll");
        if (path is null) { Check(false, "bw profiles dll present (build eyePlugins/bwprofiles first)"); return; }
        var entry = PluginManager.LoadFromFile(path);
        Check(entry.Error is null && entry.Plugins.Count == 1 && entry.Plugins[0].Error is null, $"bw profiles: loads ({entry.Error})");
        var reg = new PluginRegistry(entry.Plugins);
        var f = reg.FindFilter("photographereye.bwprofiles:bw");
        Check(f is not null && f.D.Stage == FilterStage.Display && f.D.SupportsMask, "bw profiles: display-stage filter with mask support");
        if (f is null) return;
        var asm = f.D.Render.Method.DeclaringType!.Assembly;
        var store = asm.GetType("ImgViewer.Plugins.BwProfiles.BwProfileStore")!;
        var fileProp = store.GetProperty("FilePath")!;
        string tmpDir = Path.Combine(Path.GetTempPath(), "pe_bwprofiles_test_" + Environment.ProcessId);
        Directory.CreateDirectory(tmpDir);
        string file = Path.Combine(tmpDir, "profiles.txt");
        fileProp.SetValue(null, file);
        store.GetMethod("Deserialize")!.Invoke(null, new object[] { "" });

        var profileDef = f.D.Params.First(p => p.Id == "profile");
        var ids = profileDef.Choices.Select(c => c.Id).ToList();
        Check(ids.Count >= 13 && ids[0] == "none" && profileDef.DefaultText == "none" && new[] { "neutral", "red", "orange", "green", "blue", "infrared", "highkey", "lowkey", "selenium", "sepia" }.All(ids.Contains),
            $"bw profiles: built-in fine-art profiles listed ({ids.Count})");

        // Patches: saturated red, green, blue, yellow, mid grey, light grey.
        var patches = new (float R, float G, float B)[] { (0.8f, 0.15f, 0.12f), (0.15f, 0.65f, 0.2f), (0.12f, 0.2f, 0.85f), (0.85f, 0.8f, 0.15f), (0.5f, 0.5f, 0.5f), (0.8f, 0.8f, 0.8f) };
        const int PW = 64, W = PW * 6, H = 64;
        PluginImage Make()
        {
            var im = new PluginImage(W, H);
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var c = patches[x / PW];
                int i = y * W + x;
                im.R[i] = c.R; im.G[i] = c.G; im.B[i] = c.B;
            }
            return im;
        }
        FilterParams P(string profile, Dictionary<string, double>? v = null, Dictionary<string, string>? t = null)
        {
            var text = new Dictionary<string, string>(t ?? new()) { ["profile"] = profile };
            return new FilterParams(f.D.Params, v ?? new Dictionary<string, double>(), text);
        }
        PluginImage Run(FilterParams p, double scale = 1.0)
        {
            var im = Make();
            f.D.Render(im, p, new RenderContext { Stage = FilterStage.Display, Space = PluginColorSpace.Srgb, Scale = scale, FullWidth = W, FullHeight = H, Cancel = CancellationToken.None });
            return im;
        }
        static double Mean(float[] pl, int patch) { double s = 0; for (int y = 0; y < H; y++) for (int x = patch * PW; x < (patch + 1) * PW; x++) s += pl[y * W + x]; return s / (PW * H); }
        static double Std(float[] pl, int patch)
        {
            double m = Mean(pl, patch), s = 0;
            for (int y = 0; y < H; y++) for (int x = patch * PW; x < (patch + 1) * PW; x++) { double d = pl[y * W + x] - m; s += d * d; }
            return Math.Sqrt(s / (PW * H));
        }

        {
            var st = new PluginEditState();
            st.SetText(f, profileDef, "neutral");
            bool on = st.IsFilterActive(f);
            st.SetText(f, profileDef, "none");
            Check(on && !st.IsFilterActive(f), "bw profiles: picking Neutral (first profile) turns the conversion on, \"choose a profile\" leaves the photo alone");
        }
        var neutral = Run(P("neutral"));
        double maxChroma = 0;
        for (int i = 0; i < W * H; i++) maxChroma = Math.Max(maxChroma, Math.Max(Math.Abs(neutral.R[i] - neutral.G[i]), Math.Abs(neutral.B[i] - neutral.G[i])));
        Check(maxChroma < 1e-5, $"bw profiles: neutral is monochrome (max chroma {maxChroma:0.######})");
        Check(Math.Abs(Mean(neutral.G, 4) - 0.5) < 0.01 && Math.Abs(Mean(neutral.G, 5) - 0.8) < 0.01,
            $"bw profiles: neutral keeps greys ({Mean(neutral.G, 4):0.###}, {Mean(neutral.G, 5):0.###})");
        Check(Mean(neutral.G, 1) > Mean(neutral.G, 2), "bw profiles: neutral uses luminance (green lighter than blue)");

        var red = Run(P("red"));
        Check(Mean(red.G, 0) > Mean(neutral.G, 0) + 0.08 && Mean(red.G, 2) < Mean(neutral.G, 2) - 0.03,
            $"bw profiles: red filter lightens reds ({Mean(neutral.G, 0):0.###} -> {Mean(red.G, 0):0.###}) and darkens blues ({Mean(neutral.G, 2):0.###} -> {Mean(red.G, 2):0.###})");
        var green = Run(P("green"));
        Check(Mean(green.G, 1) > Mean(neutral.G, 1) + 0.05 && Mean(green.G, 0) < Mean(neutral.G, 0), "bw profiles: green filter lightens foliage, darkens reds");
        var blue = Run(P("blue"));
        Check(Mean(blue.G, 2) > Mean(neutral.G, 2) + 0.05, "bw profiles: blue filter lightens blues");
        var hk = Run(P("highkey"));
        var lk = Run(P("lowkey"));
        Check(Mean(hk.G, 4) > Mean(neutral.G, 4) + 0.05 && Mean(lk.G, 4) < Mean(neutral.G, 4) - 0.05,
            $"bw profiles: high key brighter ({Mean(hk.G, 4):0.###}), low key darker ({Mean(lk.G, 4):0.###}) than neutral grey");

        // Fine-tuning adds to the profile: Reds +50 on Neutral lightens reds only.
        var tuned = Run(P("neutral", new() { ["mixred"] = 50 }));
        Check(Mean(tuned.G, 0) > Mean(neutral.G, 0) + 0.05 && Math.Abs(Mean(tuned.G, 2) - Mean(neutral.G, 2)) < 0.01 && Math.Abs(Mean(tuned.G, 4) - Mean(neutral.G, 4)) < 1e-4,
            "bw profiles: Reds slider lightens reds, leaves blues and greys");

        // Strength.
        Check(f.D.IsIdentity(P("red", new() { ["amount"] = 0 })) && !f.D.IsIdentity(P("red")), "bw profiles: strength 0 is identity");
        var half = Run(P("neutral", new() { ["amount"] = 50 }));
        double expR = (patches[0].R + Mean(neutral.R, 0)) / 2;
        Check(Math.Abs(Mean(half.R, 0) - expR) < 0.01, $"bw profiles: strength 50 blends halfway ({Mean(half.R, 0):0.###} vs {expR:0.###})");

        // Toning: sepia warms (R > B) the light grey, selenium cools the shadows; own colours override.
        var sepia = Run(P("sepia"));
        Check(Mean(sepia.R, 5) > Mean(sepia.B, 5) + 0.03 && Mean(sepia.R, 4) > Mean(sepia.B, 4) + 0.02, "bw profiles: sepia tones warm");
        var cyan = Run(P("cyanotype"));
        Check(Mean(cyan.B, 4) > Mean(cyan.R, 4) + 0.05, "bw profiles: cyanotype tones blue");
        var own = Run(P("neutral", new() { ["owntint"] = 1 }, new() { ["shadowtint"] = "#204060", ["highlighttint"] = "#204060" }));
        Check(Mean(own.B, 4) > Mean(own.R, 4) + 0.03, "bw profiles: own toning colours apply on a profile without toning");

        // Grain: deterministic, present at full size, weaker in a downscaled preview.
        var g1 = Run(P("trix"));
        var g2 = Run(P("trix"));
        bool same = true;
        for (int i = 0; i < W * H && same; i++) same = g1.G[i] == g2.G[i];
        var gp = Run(P("trix"), 0.25);
        double sFull = Std(g1.G, 4), sPrev = Std(gp.G, 4);
        Check(same && sFull > 0.01 && sPrev < sFull * 0.8 && Std(neutral.G, 4) < 1e-6,
            $"bw profiles: grain deterministic, visible ({sFull:0.####}), weaker in preview ({sPrev:0.####})");
        Check(Std(g1.G, 4) > 0 && Math.Abs(Mean(g1.G, 4) - Mean(Run(P("trix", new() { ["grain"] = -100 })).G, 4)) < 0.01, "bw profiles: grain keeps the mean level");

        // Custom profiles: file round trip.
        var profType = asm.GetType("ImgViewer.Plugins.BwProfiles.BwProfile")!;
        var make = profType.GetMethod("Make")!;
        object prof = make.Invoke(null, new object?[] { "x", "Night = sky, test", new double[] { 10, 20, 30, 40, 50, 60, 70 }, 15.5, -5, 3, 12, 40, "#112233", "#AABBCC", 25, -20 })!;
        store.GetMethod("SetCustom")!.Invoke(null, new object?[] { 4, prof });
        string ser = (string)store.GetMethod("Serialize")!.Invoke(null, null)!;
        store.GetMethod("Deserialize")!.Invoke(null, new object[] { "" });
        Check(store.GetMethod("Custom")!.Invoke(null, new object[] { 4 }) is null, "bw profiles: clearing the store");
        store.GetMethod("Deserialize")!.Invoke(null, new object[] { ser });
        var back = store.GetMethod("Custom")!.Invoke(null, new object[] { 4 });
        string? Fld(object? o, string n) => o is null ? null : Convert.ToString(profType.GetField(n)!.GetValue(o) is double[] arr ? string.Join(",", arr) : profType.GetField(n)!.GetValue(o), System.Globalization.CultureInfo.InvariantCulture);
        Check(Fld(back, "Name") == "Night = sky, test" && Fld(back, "Id") == "custom4" && Fld(back, "Mix") == "10,20,30,40,50,60,70"
              && Fld(back, "Contrast") == "15.5" && Fld(back, "ShadowTint") == "#112233" && Fld(back, "Balance") == "-20",
            "bw profiles: custom profile survives the file round trip");
        store.GetMethod("Deserialize")!.Invoke(null, new object[] { "[11]\nname=x\n[0]\nname=y\n[2]\nmix=500,abc\ncontrast=nan\nshadowtint=red\n" });
        var bad = store.GetMethod("Custom")!.Invoke(null, new object[] { 2 });
        Check(bad is not null && Fld(bad, "Mix")!.StartsWith("100,0") && Fld(bad, "Contrast") == "0" && Fld(bad, "ShadowTint") == "#808080",
            "bw profiles: bad file values are clamped or ignored");
        store.GetMethod("Deserialize")!.Invoke(null, new object[] { "" });

        // Section commands: save the tuned look into slot 3, then delete it.
        var save = reg.Commands.FirstOrDefault(c => c.FullId == "photographereye.bwprofiles:save");
        var del = reg.Commands.FirstOrDefault(c => c.FullId == "photographereye.bwprofiles:delete");
        Check(save is not null && del is not null && save.D.Placement == CommandPlacement.Section && save.D.SectionFilter == "bw",
            "bw profiles: save / delete are section commands of the filter");
        if (save is null || del is null) return;
        var ctx = DispatchProxy.Create<ICommandContext, BwContextProxy>();
        var px = (BwContextProxy)(object)ctx;
        foreach (var d in f.D.Params)
            if (d.IsText) px.Text[d.Id] = d.DefaultText; else px.Values[d.Id] = d.Default;
        px.Text["profile"] = "red";
        px.Values["contrast"] = 20;
        px.Values["mixblue"] = -10;
        px.Values["grain"] = 30;
        string? askedSlotDefault = null;
        px.AskReply = (title, defs) =>
        {
            var slot = defs.First(d => d.Id.StartsWith("slot"));
            askedSlotDefault = slot.DefaultText;
            var name = defs.First(d => d.Id.StartsWith("name"));
            return new FilterParams(defs, new Dictionary<string, double>(), new Dictionary<string, string> { [slot.Id] = "3", [name.Id] = "Harness sky" });
        };
        var beforeSave = Run(new FilterParams(f.D.Params, new Dictionary<string, double>(px.Values), new Dictionary<string, string>(px.Text)));
        save.D.Execute(ctx);
        Check(askedSlotDefault == "1", $"bw profiles: save offers the first empty slot ({askedSlotDefault})");
        Check(px.Text["profile"] == "custom3" && px.Values["contrast"] == 0 && px.Values["mixblue"] == 0 && px.Values["grain"] == 0 && px.Values["amount"] == 100,
            "bw profiles: after saving the photo uses the profile, fine-tuning folded in");
        Check(File.Exists(file) && File.ReadAllText(file).Contains("name=Harness sky") && profileDef.Choices.Any(c => c.Id == "custom3" && c.Label.Contains("Harness sky")),
            "bw profiles: saved to the file and listed in the profile choice");
        var afterSave = Run(new FilterParams(f.D.Params, new Dictionary<string, double>(px.Values), new Dictionary<string, string>(px.Text)));
        double diff = 0;
        for (int i = 0; i < W * H; i++) diff = Math.Max(diff, Math.Abs(afterSave.G[i] - beforeSave.G[i]));
        Check(diff < 1e-4, $"bw profiles: the saved profile looks the same as the tuned one (max diff {diff:0.######})");

        px.AskReply = (title, defs) =>
        {
            var slot = defs.First(d => d.Id.StartsWith("slot"));
            askedSlotDefault = slot.DefaultText;
            return new FilterParams(defs, new Dictionary<string, double>(), new Dictionary<string, string> { [slot.Id] = slot.DefaultText });
        };
        del.D.Execute(ctx);
        var afterDelete = Run(new FilterParams(f.D.Params, new Dictionary<string, double>(px.Values), new Dictionary<string, string>(px.Text)));
        diff = 0;
        for (int i = 0; i < W * H; i++) diff = Math.Max(diff, Math.Abs(afterDelete.G[i] - beforeSave.G[i]));
        Check(askedSlotDefault == "3" && px.Text["profile"] == "neutral" && !profileDef.Choices.Any(c => c.Id == "custom3") && !File.ReadAllText(file).Contains("Harness sky"),
            "bw profiles: delete removes the profile from the file and the choice");
        Check(diff < 1e-4, $"bw profiles: deleting the photo's profile keeps its look as fine-tuning (max diff {diff:0.######})");
        try { Directory.Delete(tmpDir, true); } catch (IOException) { }
    }

    /// <summary>B&W classic: one choice of 6 looks and nothing else; every look is monochrome and the looks differ.</summary>
    private static void TestBwClassic()
    {
        var path = FindPluginDll("bwclassic", "ImgViewer.Plugins.BwClassic.dll");
        if (path is null) { Check(false, "bw classic dll present (build eyePlugins/bwclassic first)"); return; }
        var entry = PluginManager.LoadFromFile(path);
        Check(entry.Error is null && entry.Plugins.Count == 1 && entry.Plugins[0].Error is null, $"bw classic: loads ({entry.Error})");
        var reg = new PluginRegistry(entry.Plugins);
        var f = reg.FindFilter("photographereye.bwclassic:bwclassic");
        Check(f is not null && f.D.Stage == FilterStage.Display && !f.D.SupportsMask, "bw classic: display-stage filter without a mask");
        if (f is null) return;
        var choice = f.D.Params.Count == 1 ? f.D.Params[0] : null;
        Check(choice is not null && choice.Kind == ParamKind.Choice && choice.Choices.Count == 7 && choice.DefaultText == "none" && choice.Choices[0].Id == "none" && choice.Choices[1].Id == "classic",
            "bw classic: a single choice of 6 looks after \"choose a look\" (the default, so picking any look switches the filter on), no other settings");
        if (choice is null) return;
        FilterParams Look(string id) => new FilterParams(f.D.Params, new Dictionary<string, double>(), new Dictionary<string, string> { ["profile"] = id });
        Check(f.D.IsIdentity(Look("none")) && !f.D.IsIdentity(Look("classic")) && !f.D.IsIdentity(Look("highkey")), "bw classic: only \"choose a look\" is identity");
        // Picking the first look in the panel switches the filter on (the host turns a panel filter on when a value differs from its default).
        var st = new PluginEditState();
        st.SetText(f, choice, "classic");
        bool onFirst = st.IsFilterActive(f);
        st.SetText(f, choice, "none");
        Check(onFirst && !st.IsFilterActive(f), "bw classic: picking the first look turns the conversion on, \"choose a look\" turns it off");
        Check(reg.Commands.Count == 0, "bw classic: no commands");
        const int W = 96, H = 48;
        var src = new PluginImage(W, H);
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = y * W + x;
            double hue = x / (double)W, lum = y / (double)(H - 1);
            src.R[i] = (float)(lum * (0.5 + 0.5 * Math.Cos(2 * Math.PI * hue)));
            src.G[i] = (float)(lum * (0.5 + 0.5 * Math.Cos(2 * Math.PI * (hue - 1 / 3.0))));
            src.B[i] = (float)(lum * (0.5 + 0.5 * Math.Cos(2 * Math.PI * (hue - 2 / 3.0))));
        }
        var outs = new List<float[]>();
        bool mono = true;
        foreach (var c in choice.Choices.Skip(1))
        {
            var im = new PluginImage(W, H, (float[])src.R.Clone(), (float[])src.G.Clone(), (float[])src.B.Clone());
            var p = new FilterParams(f.D.Params, new Dictionary<string, double>(), new Dictionary<string, string> { ["profile"] = c.Id });
            f.D.Render(im, p, new RenderContext { Stage = FilterStage.Display, Space = PluginColorSpace.Srgb, FullWidth = W, FullHeight = H, Cancel = CancellationToken.None });
            for (int i = 0; i < W * H; i++)
            {
                if (Math.Abs(im.R[i] - im.G[i]) > 1e-5 || Math.Abs(im.B[i] - im.G[i]) > 1e-5) mono = false;
                if (float.IsNaN(im.G[i]) || im.G[i] < 0 || im.G[i] > 1) mono = false;
            }
            outs.Add(im.G);
        }
        Check(mono, "bw classic: every look is monochrome and in range");
        double minDiff = double.MaxValue;
        for (int a = 0; a < outs.Count; a++)
        for (int b = a + 1; b < outs.Count; b++)
        {
            double d = 0;
            for (int i = 0; i < W * H; i++) d += Math.Abs(outs[a][i] - outs[b][i]);
            minDiff = Math.Min(minDiff, d / (W * H));
        }
        Check(minDiff > 0.01, $"bw classic: the 6 looks are clearly different (min mean diff {minDiff:0.###})");
    }
}
