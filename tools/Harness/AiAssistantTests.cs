using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ImgViewer.Plugins;
using ImgViewer.Services;
using ImgViewer.Services.Ai;

internal static partial class Program
{
    /// <summary>
    /// AI assistant, stage 1, without a server: image statistics, the function catalogue, the agent loop with a scripted
    /// model and a fake editor (clamping, string arguments, unknown function, step limit, previews only for vision
    /// models), the Ollama response parsing, the model classification and the API key encryption.
    /// </summary>
    private static void TestAiAssistant()
    {
        // ---- ImageProbe ----
        var grey = Solid(64, 48, 0.5f, 0.5f, 0.5f);
        var st = ImageProbe.Analyze(grey);
        Check(Math.Abs(st.MeanLuma - 50) < 0.2 && st.Monochrome && st.ColorCast == "neutral" && st.ClippedHighlightsPct == 0 &&
              st.ClippedShadowsPct == 0 && st.TonalRange < 0.2, "ai: probe of flat grey");
        var warm = Solid(32, 32, 0.8f, 0.6f, 0.4f);
        var sw = ImageProbe.Analyze(warm);
        Check(sw.ColorCast == "warm (orange)" && !sw.Monochrome && sw.MeanSaturation > 40, "ai: probe sees a warm cast");
        Check(ImageProbe.Analyze(Solid(16, 16, 0.3f, 0.35f, 0.6f)).ColorCast == "cool (blue)", "ai: probe sees a cool cast");
        Check(ImageProbe.Analyze(Solid(16, 16, 0.4f, 0.6f, 0.4f)).ColorCast == "green", "ai: probe sees a green cast");
        // left half black, right half white: clipping both ends, zones dark left / bright right, wide range
        var split = new PluginImage(40, 30);
        for (int y = 0; y < 30; y++)
            for (int x = 20; x < 40; x++)
                split.R[y * 40 + x] = split.G[y * 40 + x] = split.B[y * 40 + x] = 1f;
        var ss = ImageProbe.Analyze(split);
        Check(Math.Abs(ss.ClippedHighlightsPct - 50) < 0.1 && Math.Abs(ss.ClippedShadowsPct - 50) < 0.1 && ss.TonalRange > 99 &&
              ss.Zones.Length == 9 && ss.Zones[0] < 1 && ss.Zones[2] > 99 && ss.Zones[6] < 1 && ss.Zones[8] > 99, "ai: probe clipping and zones");
        var sj = ss.ToJson();
        Check(sj["luminance_percentiles_1_5_50_95_99"] is JsonArray { Count: 5 } && sj["zones_3x3_luminance"] is JsonArray { Count: 9 },
            "ai: probe JSON shape");

        // ---- catalogue ----
        var cat = AiTools.Catalog(vision: false);
        var names = cat.Select(t => t!["function"]!["name"]!.ToString()).ToList();
        Check(names.Take(4).SequenceEqual(new[] { "get_state", "analyze_image", "set_core", "reset_core" }) && !names.Contains("get_preview"), "ai: text-only catalogue");
        int textTools = cat.Count;
        Check(AiTools.Catalog(vision: true).Any(t => t!["function"]!["name"]!.ToString() == "get_preview"), "ai: vision catalogue has get_preview");
        var props = (JsonObject)cat.First(t => t!["function"]!["name"]!.ToString() == "set_core")!["function"]!["parameters"]!["properties"]!;
        Check(CoreParams.All.All(p => props[p.Key] is JsonObject o && (double)o["minimum"]! == p.Min && (double)o["maximum"]! == p.Max),
            "ai: set_core lists every basic setting with its range");
        Check(props["Mono"]!["type"]!.ToString() == "integer" && props["Exposure"]!["type"]!.ToString() == "number", "ai: integer vs number");

        // ---- agent loop with a scripted model ----
        {
            var ed = new FakeEditor();
            var chat = new ScriptedChat(
                Call(("get_state", new JsonObject()), ("analyze_image", new JsonObject())),
                Call(("set_core", new JsonObject { ["Exposure"] = 9, ["Contrast"] = "15", ["Mono"] = 1.6, ["Bogus"] = 3 })),
                Call(("set_core", JsonValue.Create("{\"values\":{\"saturation\":-20}}"))),
                Call(("frobnicate", new JsonObject())),
                Final("Gotowe: rozjaśniłem zdjęcie."));
            var session = new AiSession();
            var events = new List<AiEvent>();
            var res = session.RunTurnAsync(chat, ed, "m", vision: false, "przygotuj do albumu", new AiTurnOptions(8), events.Add, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(res.FinalText == "Gotowe: rozjaśniłem zdjęcie." && res.ModelCalls == 5 && res.Changed && !res.StoppedAtLimit, "ai: loop ends with the answer");
            Check(ed.Core["Exposure"] == 5 && ed.Core["Contrast"] == 15 && ed.Core["Mono"] == 2 && ed.Core["Saturation"] == -20,
                "ai: values clamped, numeric strings and nested 'values' accepted, keys case-insensitive");
            Check(ed.SetCalls == 2 && ed.Waits >= 2, "ai: each set_core sets once and waits for the render");
            // what the model got in its last call (the history is compacted after the turn)
            var toolMsgs = chat.LastMessages!.OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool").ToList();
            Check(toolMsgs.Count == 5 && toolMsgs.All(m => m["tool_name"] is not null), "ai: one tool message per call");
            Check(toolMsgs[2]["content"]!.ToString().Contains("\"ignored\":\"Bogus\"") && toolMsgs[2]["content"]!.ToString().Contains("image_after"),
                "ai: set_core reports ignored keys and the statistics after");
            Check(toolMsgs[4]["content"]!.ToString().Contains("unknown function"), "ai: unknown function becomes an error result");
            Check(events.First().Kind == AiEventKind.User && events.Last().Kind == AiEventKind.Assistant &&
                  events.Any(e => e.Kind == AiEventKind.Step && e.Text.Contains("→")), "ai: transcript has the request, steps and the answer");
            Check(chat.ToolCounts.All(c => c == textTools) && !chat.AnyImage, "ai: text-only model gets no images");
            Check(session.Messages[0]!["role"]!.ToString() == "system", "ai: system prompt first");

            // compaction: system, the plain request, one summary with the changes and the answer
            var h = session.Messages.OfType<JsonObject>().ToList();
            Check(h.Count == 3 && h[1]["role"]!.ToString() == "user" && h[1]["content"]!.ToString() == "przygotuj do albumu" &&
                  h[2]["role"]!.ToString() == "assistant" && h[2]["tool_calls"] is null &&
                  h[2]["content"]!.ToString().StartsWith("[changes: ") && h[2]["content"]!.ToString().Contains("Saturation") &&
                  h[2]["content"]!.ToString().EndsWith("Gotowe: rozjaśniłem zdjęcie."), "ai: the turn is folded into request + summary");
            int fullCtx = AiSession.EstimateTokens(chat.LastMessages!);
            Check(AiSession.EstimateTokens(session.Messages) < fullCtx, $"ai: compaction shrinks the history ({fullCtx} -> {AiSession.EstimateTokens(session.Messages)} est. tokens)");

            // token counters from the server's counts (scripted: 100k read + 10 written per call)
            Check(session.TotalCalls == 5 && session.TotalPromptTokens == 500_000 && session.TotalOutputTokens == 50 &&
                  session.LastPromptTokens == 100_000 && !session.Estimated && !session.LastPromptEstimated,
                  "ai: token counters add up the reported counts");
            Check(session.ImagesSent == 0 && !session.Transcript.Any(e => e.Text.Contains("📷")), "ai: text-only model: no preview sent or noted");

            // a follow-up turn builds on the short history
            var chat2 = new ScriptedChat(Final("Cieplej."));
            session.RunTurnAsync(chat2, ed, "m", false, "cieplej", new AiTurnOptions(8), null, CancellationToken.None).GetAwaiter().GetResult();
            Check(chat2.LastMessages!.Count == 4 && session.Messages.Count == 5 && session.TotalCalls == 6, "ai: follow-up sends the folded history");
        }

        // ---- token budget ----
        {
            var ed = new FakeEditor();
            var session = new AiSession();
            var loop = Enumerable.Range(0, 10).Select(_ => Call(("analyze_image", new JsonObject()))).ToArray();
            var res = session.RunTurnAsync(new ScriptedChat(loop), ed, "m", false, "x", new AiTurnOptions(8, TokenBudget: 250_000), null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(res.StoppedAtBudget && res.ModelCalls == 3 && session.OverBudget(250_000) && !session.OverBudget(0),
                "ai: the token budget stops the turn before the next call");
            Check(session.Messages.OfType<JsonObject>().Last()["content"]!.ToString().Contains("token budget"), "ai: budget stop noted in the history");
            // no counts from the server: estimated from the request size
            var s2 = new AiSession();
            s2.RunTurnAsync(new ScriptedChat(Final("ok")) { NoCounts = true }, ed, "m", false, "x", new AiTurnOptions(), null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(s2.Estimated && s2.LastPromptEstimated && s2.LastPromptTokens > 500 && s2.TotalOutputTokens > 0, "ai: missing counts are estimated");
            // a count below what the request holds (cached prefix left out) is raised to the estimate
            var s3 = new AiSession();
            s3.RunTurnAsync(new ScriptedChat(Final("ok")) { PromptCount = 100 }, ed, "m", false, "x", new AiTurnOptions(), null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(s3.LastPromptEstimated && s3.LastPromptTokens > 500 && s3.TotalPromptTokens == s3.LastPromptTokens,
                $"ai: an undercounted context is raised to the estimate ({s3.LastPromptTokens})");
            Check(AiSession.Fmt(950) == "950" && AiSession.Fmt(12345) == "12.3k" && AiSession.Fmt(2_500_000) == "2.5M", "ai: token formatting");
            Check(ImgViewer.AssistantWindow.ParseTokens("200k") == 200_000 && ImgViewer.AssistantWindow.ParseTokens("1,5M") == 1_500_000 &&
                  ImgViewer.AssistantWindow.ParseTokens("16384") == 16384 && ImgViewer.AssistantWindow.ParseTokens("abc") is null, "ai: token input parsing");
        }

        // ---- vision: preview attached to the request, get_preview adds one image, older ones dropped ----
        {
            var ed = new FakeEditor();
            var chat = new ScriptedChat(Call(("get_preview", new JsonObject())), Final("ok"));
            var session = new AiSession();
            session.RunTurnAsync(chat, ed, "m", vision: true, "b&w", new AiTurnOptions(8), null, CancellationToken.None).GetAwaiter().GetResult();
            var sent = chat.LastMessages!.OfType<JsonObject>().ToList();
            int images = sent.Count(m => m["images"] is not null);
            Check(images == 1 && sent.Last(m => m["images"] is not null)["role"]!.ToString() == "user" &&
                  chat.LastMessages!.ToJsonString().Contains("older preview removed"), "ai: only the newest preview is kept");
            Check(chat.ToolCounts.All(c => c == textTools + 1) && chat.FirstUserHadImage, "ai: vision model gets get_preview and the photo with the request");
            Check(!session.Messages.ToJsonString().Contains("\"images\""), "ai: no image stays in the history after the turn");
            int noted = session.Transcript.Count(e => e.Kind == AiEventKind.Step && e.Text == AiSession.ImageSentNote);
            Check(session.ImagesSent >= 2 && noted == session.ImagesSent, $"ai: every preview sent is noted ({session.ImagesSent})");
        }

        // ---- step limit and follow-up turn ----
        {
            var ed = new FakeEditor();
            var loop = Enumerable.Range(0, 10).Select(_ => Call(("analyze_image", new JsonObject()))).ToArray();
            var session = new AiSession();
            var res = session.RunTurnAsync(new ScriptedChat(loop), ed, "m", false, "x", new AiTurnOptions(3), null, CancellationToken.None).GetAwaiter().GetResult();
            Check(res.StoppedAtLimit && res.ModelCalls == 3 && !res.Changed, "ai: step limit stops the turn");
            var last = session.Messages.OfType<JsonObject>().Last();
            Check(session.Messages.Count == 3 && last["role"]!.ToString() == "assistant" && last["content"]!.ToString().Contains("step limit"),
                "ai: a stopped turn is closed for the next one");
        }

        // ---- cancellation and a failing model leave a usable history ----
        {
            var ed = new FakeEditor();
            var session = new AiSession();
            using var cts = new CancellationTokenSource();
            var chat = new ScriptedChat(Call(("set_core", new JsonObject { ["Exposure"] = 1 }))) { OnCall = n => { if (n == 2) cts.Cancel(); } };
            bool cancelled = false;
            try
            {
                session.RunTurnAsync(chat, ed, "m", false, "x", new AiTurnOptions(8), null, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            var lastC = session.Messages.OfType<JsonObject>().Last()["content"]!.ToString();
            Check(cancelled && ed.Core["Exposure"] == 1 && lastC.Contains("stopped") && lastC.Contains("Exposure") &&
                  !session.Messages.OfType<JsonObject>().Any(m => m["role"]?.ToString() == "tool"),
                "ai: Stop keeps the changes so far and folds them into the summary");
            bool failed = false;
            try
            {
                session.RunTurnAsync(new ScriptedChat(), ed, "m", false, "y", new AiTurnOptions(8), null, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }
            Check(failed && session.Messages.OfType<JsonObject>().Last()["content"]!.ToString().Contains("failed"), "ai: a failed call is noted");
        }

        // ---- reset_core ----
        {
            var ed = new FakeEditor();
            ed.Core["Exposure"] = 1.5;
            ed.Core["Contrast"] = 30;
            var r = AiTools.ExecuteAsync(ed, "reset_core", new JsonObject { ["keys"] = new JsonArray("Exposure") }, false, CancellationToken.None).GetAwaiter().GetResult();
            Check(r.Changed && ed.Core["Exposure"] == 0 && ed.Core["Contrast"] == 30, "ai: reset_core resets only the given keys");
            r = AiTools.ExecuteAsync(ed, "reset_core", null, false, CancellationToken.None).GetAwaiter().GetResult();
            Check(r.Changed && ed.Core["Contrast"] == 0 && ed.Core["Gamma"] == 1, "ai: reset_core without keys resets everything");
            r = AiTools.ExecuteAsync(ed, "set_core", new JsonObject { ["Nope"] = 1 }, false, CancellationToken.None).GetAwaiter().GetResult();
            Check(r.Error && !r.Changed, "ai: set_core without a valid key is an error");
            r = AiTools.ExecuteAsync(ed, "get_preview", null, false, CancellationToken.None).GetAwaiter().GetResult();
            Check(r.Error && r.ImageBase64 is null, "ai: get_preview refused for a text-only model");
        }

        // ---- Ollama responses ----
        var tags = JsonNode.Parse("{\"models\":[{\"name\":\"qwen2.5vl:7b\",\"size\":6000},{\"name\":\"Gemma3:4b\",\"size\":3000},{\"model\":\"llama3.1:8b\"}]}")!.AsObject();
        var parsed = OllamaClient.ParseTags(tags);
        Check(parsed.Count == 3 && parsed[0].Name == "Gemma3:4b" && parsed.Any(p => p.Name == "llama3.1:8b"), "ai: /api/tags parsed and sorted");
        var caps = OllamaClient.ParseCapabilities(JsonNode.Parse("{\"capabilities\":[\"completion\",\"vision\",\"tools\"]}")!.AsObject());
        Check(caps is not null && caps.Contains("vision") && caps.Contains("tools") && OllamaClient.ParseCapabilities(new JsonObject()) is null,
            "ai: /api/show capabilities parsed");
        Check(OllamaClient.ParseContextLength(JsonNode.Parse("{\"model_info\":{\"qwen2.context_length\":32768,\"general.x\":1}}")!.AsObject()) == 32768 &&
              OllamaClient.ParseContextLength(new JsonObject()) == 0, "ai: /api/show context length parsed");
        var reply = OllamaClient.ParseChat(JsonNode.Parse("{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"prompt_eval_count\":1234,\"eval_count\":56}")!.AsObject());
        Check(reply.PromptTokens == 1234 && reply.OutputTokens == 56 && reply.Message["content"]!.ToString() == "ok", "ai: /api/chat token counts parsed");
        bool chatErr = false;
        try { OllamaClient.ParseChat(JsonNode.Parse("{\"error\":\"model not found\"}")!.AsObject()); }
        catch (Exception ex) { chatErr = ex.Message.Contains("model not found"); }
        Check(chatErr, "ai: /api/chat error reported");

        // ---- model classification ----
        AiModelChoice C(string n, params string[] c) => AiModels.Classify(new AiModelInfo(n, 0, c, c.Length > 0));
        Check(C("qwen2.5vl:7b", "completion", "vision", "tools").Tier == AiModelTier.Recommended, "ai: vision+tools recommended");
        Check(C("gemma3:12b", "completion", "vision", "tools").Tier == AiModelTier.Vision, "ai: gemma is vision tier");
        Check(C("llama3.1:8b", "completion", "tools").Tier == AiModelTier.TextOnly, "ai: tools only is text-only");
        Check(C("llava:7b", "completion", "vision").Tier == AiModelTier.Unsuitable, "ai: no tools is unsuitable");
        Check(C("qwen3-vl:235b-cloud").Tier == AiModelTier.Recommended && C("qwen3-vl:235b-cloud").Note.Contains("cloud") &&
              C("qwen3-vl:235b-cloud").Note.Contains("guessed"), "ai: unknown capabilities guessed from the name");
        Check(C("moondream").Tier == AiModelTier.Unsuitable && C("phi3:mini").Tier == AiModelTier.Unsuitable, "ai: name guesses for no-tool models");
        var ranked = AiModels.Rank(new[]
        {
            new AiModelInfo("llava", 0, new[] { "vision" }, true),
            new AiModelInfo("llama3.1", 0, new[] { "tools" }, true),
            new AiModelInfo("qwen2.5vl", 0, new[] { "vision", "tools" }, true)
        });
        Check(ranked.Select(r => r.Name).SequenceEqual(new[] { "qwen2.5vl", "llama3.1", "llava" }) && ranked[0].Display.StartsWith("★"),
            "ai: ranking puts recommended first with a star");

        // ---- API key (DPAPI) and settings ----
        var prot = AiKeyProtector.Protect("sk-test-123");
        Check(prot is not null && !prot.Contains("sk-test") && AiKeyProtector.Unprotect(prot) == "sk-test-123" &&
              AiKeyProtector.Unprotect("garbage!") is null && AiKeyProtector.Protect("") is null, "ai: API key encrypted round trip");
        var s = new AiSettings { Mode = "cloud", CloudModel = "a", LocalModel = "b", ApiKeyProtected = prot };
        Check(s.IsCloud && s.Model == "a" && s.BaseUrl == "https://ollama.com" && s.ApiKey() == "sk-test-123" &&
              (s with { Mode = "local" }).ApiKey() is null && s.WithModel("z").CloudModel == "z" && s.WithModel("z").LocalModel == "b",
            "ai: settings per mode, no key sent locally");
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        Check(!json.Contains("sk-test") && !json.Contains("IsCloud") && !json.Contains("BaseUrl"), "ai: settings JSON holds no plain key nor computed values");

        // prompt history (Up / Down in the chat input)
        var ph = new AiSettings().WithPrompt("a").WithPrompt("b").WithPrompt("a");
        Check(ph.PromptHistory!.SequenceEqual(new[] { "b", "a" }), "ai: prompt history keeps one copy, newest last");
        for (int i = 0; i < 60; i++) ph = ph.WithPrompt("p" + i);
        Check(ph.PromptHistory!.Count == AiSettings.PromptHistoryMax && ph.PromptHistory[^1] == "p59" && ph.PromptHistory[0] == "p10",
            "ai: prompt history is capped, the oldest go first");
        var phJson = System.Text.Json.JsonSerializer.Serialize(ph);
        Check(System.Text.Json.JsonSerializer.Deserialize<AiSettings>(phJson)!.PromptHistory!.SequenceEqual(ph.PromptHistory), "ai: prompt history survives settings.json");

        TestPrivacyPolicy();
        TestAiAssistantStages();
    }

    private static void TestPrivacyPolicy()
    {
        string pl = PrivacyPolicy.Text("pl"), en = PrivacyPolicy.Text("en");
        Check(pl.StartsWith("# Polityka prywatności") && en.StartsWith("# PhotographerEye Privacy Policy"), "privacy: both texts are embedded");
        int Sections(string t) => t.Split('\n').Count(l => l.StartsWith("## "));
        Check(Sections(pl) == Sections(en) && Sections(pl) >= 8, "privacy: Polish and English have the same sections");
        var date = DateTime.ParseExact(PrivacyPolicy.Version, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        // (the harness runs with invariant globalization: Polish month names spelled out)
        string[] plMonths = { "stycznia", "lutego", "marca", "kwietnia", "maja", "czerwca", "lipca", "sierpnia", "września", "października", "listopada", "grudnia" };
        Check(pl.Contains($"{date.Day} {plMonths[date.Month - 1]} {date.Year}") && en.Contains(date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)),
            "privacy: the texts carry the date of Version (raise both together)");
        var fresh = new ViewerSettings();
        var ok = PrivacyPolicy.Accept(fresh with { Gamma = 1.2 }, new DateTime(2026, 11, 11, 10, 0, 0));
        Check(!PrivacyPolicy.IsAccepted(fresh) && PrivacyPolicy.IsAccepted(ok) && ok.Gamma == 1.2 && ok.PrivacyAcceptedAt!.Value.Hour == 10,
            "privacy: not accepted on the first start, accepted after Accept (other settings kept)");
        Check(!PrivacyPolicy.IsAccepted(ok with { PrivacyAcceptedVersion = "2000-01-01" }), "privacy: an older version must be accepted again");
        var back = System.Text.Json.JsonSerializer.Deserialize<ViewerSettings>(System.Text.Json.JsonSerializer.Serialize(ok))!;
        Check(PrivacyPolicy.IsAccepted(back) && back.PrivacyAcceptedAt == ok.PrivacyAcceptedAt, "privacy: the acceptance survives settings.json");
    }

    private static PluginImage Solid(int w, int h, float r, float g, float b)
    {
        var img = new PluginImage(w, h);
        Array.Fill(img.R, r);
        Array.Fill(img.G, g);
        Array.Fill(img.B, b);
        return img;
    }

    private static JsonObject Call(params (string Name, JsonNode Args)[] calls) => new()
    {
        ["role"] = "assistant",
        ["content"] = "",
        ["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
        {
            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.DeepClone() }
        }).ToArray())
    };

    private static JsonObject Final(string text) => new() { ["role"] = "assistant", ["content"] = text };

    private sealed class ScriptedChat : IAiChat
    {
        private readonly Queue<JsonObject> _replies;
        public readonly List<int> ToolCounts = new();
        public bool FirstUserHadImage, AnyImage, NoCounts;
        public int PromptCount = 100_000;
        public JsonArray? LastMessages;
        public Action<int>? OnCall;
        private int _n;

        public ScriptedChat(params JsonObject[] replies) => _replies = new Queue<JsonObject>(replies);

        public Task<AiChatReply> ChatAsync(string model, JsonArray messages, JsonArray tools, CancellationToken ct)
        {
            _n++;
            OnCall?.Invoke(_n);
            ct.ThrowIfCancellationRequested();
            ToolCounts.Add(tools.Count);
            LastMessages = (JsonArray)messages.DeepClone();
            AnyImage |= messages.ToJsonString().Contains("\"images\"");
            if (_n == 1)
                FirstUserHadImage = messages.OfType<JsonObject>().Last(m => m["role"]?.ToString() == "user")["images"] is not null;
            if (_replies.Count == 0) throw new InvalidOperationException("model unavailable");
            var msg = (JsonObject)_replies.Dequeue().DeepClone();
            return Task.FromResult(NoCounts ? new AiChatReply(msg) : new AiChatReply(msg, PromptCount, 10));
        }
    }

    private sealed class FakeEditor : IAiEditor
    {
        public readonly Dictionary<string, double> Core = CoreParams.All.ToDictionary(p => p.Key, p => p.Default, StringComparer.OrdinalIgnoreCase);
        public int SetCalls, Waits;

        public IReadOnlyDictionary<string, double> GetCore() => new Dictionary<string, double>(Core, StringComparer.OrdinalIgnoreCase);

        public void SetCore(IReadOnlyDictionary<string, double> values)
        {
            SetCalls++;
            foreach (var kv in values) Core[kv.Key] = kv.Value;
        }

        public Task WaitRenderedAsync(CancellationToken ct)
        {
            Waits++;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ImageStats Analyze()
        {
            float v = (float)Math.Clamp(0.5 + Core["Exposure"] * 0.1, 0, 1);
            return ImageProbe.Analyze(Solid(8, 8, v, v, v));
        }

        public string PreviewJpegBase64(int maxLongSide) => "AAAA";

        public JsonObject PhotoInfo() => new() { ["type"] = "JPG", ["width"] = 3000, ["height"] = 2000 };

        // ---- stage 2: one panel filter (every parameter kind) and one toolbar filter ----
        public readonly Dictionary<string, double> Vals = new() { ["amount"] = 0, ["soft"] = 0 };
        public readonly Dictionary<string, string> Texts = new() { ["mode"] = "a", ["tint"] = "#808080", ["curve"] = "0,0;1,1" };
        public bool FrameOn;
        public string? AppliedPreset;

        public IReadOnlyList<AiFilter> Filters() => new[]
        {
            new AiFilter("fx:tone", "Tone", "Looks", "Tones the photo.", false, true,
                Vals["amount"] != 0 || Vals["soft"] != 0 || Texts["mode"] != "a" || Texts["tint"] != "#808080", ToolMask.GetValueOrDefault("fx:tone"),
                new[]
                {
                    new AiParam("amount", "Amount", "slider", -100, 100, 0, Vals["amount"]),
                    new AiParam("soft", "Soft", "toggle", 0, 1, 0, Vals["soft"]),
                    new AiParam("mode", "Mode", "choice", 0, 0, 0, 0, Texts["mode"], new[] { "a", "b" }),
                    new AiParam("tint", "Tint", "color", 0, 0, 0, 0, Texts["tint"]),
                    new AiParam("curve", "Curve", "curve", 0, 0, 0, 0, Texts["curve"])
                }),
            new AiFilter("fx:frame", "Frame", "Effects", "", true, false, FrameOn, null, Array.Empty<AiParam>())
        };

        public void SetFilter(string id, IReadOnlyDictionary<string, double> values, IReadOnlyDictionary<string, string> text)
        {
            foreach (var kv in values) Vals[kv.Key] = kv.Value;
            foreach (var kv in text) Texts[kv.Key] = kv.Value;
        }

        public void SetFilterActive(string id, bool active) => FrameOn = active;

        public void ResetFilter(string id)
        {
            if (id == "fx:frame") { FrameOn = false; return; }
            Vals["amount"] = 0; Vals["soft"] = 0; Texts["mode"] = "a"; Texts["tint"] = "#808080";
        }

        public IReadOnlyList<AiPreset> Presets() => new[] { new AiPreset("user:Warm", "Warm", "My presets"), new AiPreset("fx:mono", "Mono look", "Tone") };

        public void ApplyPreset(string id)
        {
            AppliedPreset = id;
            if (id == "user:Warm") Core["Temperature"] = 20;
        }

        // ---- stage 3: a 3000 x 2000 photo, crop only (no straighten in the mapping) ----
        public AiGeometry Geo = new(0, 0, 0, 1, 1, "original", 1.5);
        public AiGeometry GetGeometry() => Geo;
        public void SetGeometry(AiGeometry g) => Geo = g with { Angle = Math.Clamp(g.Angle, -45, 45) };

        // ---- stage 4 ----
        public readonly Dictionary<string, AiMask> MaskSet = new();
        public readonly Dictionary<string, string> ToolMask = new();
        private int _maskN;

        public IReadOnlyList<AiMask> Masks() => MaskSet.Values
            .Select(m => m with { UsedBy = ToolMask.Where(t => t.Value == m.Key).Select(t => t.Key).ToList() }).ToList();

        public (double X, double Y) ViewToPhoto(double x, double y) => (Geo.CropX + x * Geo.CropW, Geo.CropY + y * Geo.CropH);
        public (double X, double Y) PhotoToView(double x, double y) => ((x - Geo.CropX) / Geo.CropW, (y - Geo.CropY) / Geo.CropH);
        public (int W, int H) PhotoPixelSize() => (3000, 2000);

        public string CreateMask(string kind, double[] p, bool invert)
        {
            var key = "mask:" + ++_maskN;
            MaskSet[key] = new AiMask(key, kind, p, invert, null, Array.Empty<string>());
            return key;
        }

        public void UpdateMask(string key, string kind, double[] p, bool invert) => MaskSet[key] = MaskSet[key] with { Kind = kind, P = p, Invert = invert };

        public void DeleteMask(string key)
        {
            MaskSet.Remove(key);
            foreach (var t in ToolMask.Where(t => t.Value == key).Select(t => t.Key).ToList()) ToolMask.Remove(t);
        }

        public void SetMaskValues(string key, IReadOnlyDictionary<string, double> values) =>
            MaskSet[key] = MaskSet[key] with { Values = values.Count > 0 ? new Dictionary<string, double>(values) : null };

        public void SetToolMask(string filterId, string? maskKey)
        {
            if (maskKey is null) ToolMask.Remove(filterId);
            else ToolMask[filterId] = maskKey;
        }
    }

    private static AiToolResult Run(IAiEditor ed, string name, JsonNode? args) =>
        AiTools.ExecuteAsync(ed, name, args, false, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Stages 2-4: plugins and presets, crop and straighten, local masks; the conversation stored in the .eye file.</summary>
    private static void TestAiAssistantStages()
    {
        const double E = 1e-6;
        var names = AiTools.Catalog(false).Select(t => t!["function"]!["name"]!.ToString()).ToList();
        Check(new[] { "list_plugins", "set_plugin", "set_plugin_active", "reset_plugin", "apply_preset", "set_crop", "straighten",
                  "create_mask", "set_mask_values", "link_tool_to_mask", "delete_mask" }.All(names.Contains), "ai: catalogue has the stage 2-4 tools");
        int catTokens = AiSession.EstimateTokens(AiTools.Catalog(true));
        Check(catTokens < 4500, $"ai: the function catalogue stays small ({catTokens} est. tokens)");

        // ---- stage 2: plugins ----
        {
            var ed = new FakeEditor();
            var r = Run(ed, "list_plugins", null);
            var lj = JsonNode.Parse(r.Content)!;
            var tone = lj["filters"]!.AsArray().First(f => f!["id"]!.ToString() == "fx:tone")!;
            Check(!r.Error && tone["params"]!["amount"]!["max"]!.GetValue<double>() == 100 && tone["params"]!["mode"]!["choices"]!.AsArray().Count == 2 &&
                  lj["presets"]!.AsArray().Count == 2 && lj["filters"]!.AsArray().Any(f => f!["toolbar"]?.GetValue<bool>() == true) &&
                  tone["no_mask"] is null && lj["filters"]!.AsArray().Any(f => f!["no_mask"]?.GetValue<bool>() == true),
                "ai: list_plugins gives filters, parameters and presets");
            r = Run(ed, "set_plugin", new JsonObject
            {
                ["filter"] = "tone",
                ["values"] = new JsonObject { ["amount"] = 500, ["soft"] = true, ["mode"] = "B", ["tint"] = "ff8800", ["curve"] = "0,0;1,1", ["nope"] = 1 }
            });
            Check(r.Changed && ed.Vals["amount"] == 100 && ed.Vals["soft"] == 1 && ed.Texts["mode"] == "b" && ed.Texts["tint"] == "#FF8800" &&
                  ed.Texts["curve"] == "0,0;1,1" && r.Content.Contains("curve: not settable") && r.Content.Contains("nope: unknown") &&
                  r.StepNote!.StartsWith("Tone: Amount 0 → 100"),
                "ai: set_plugin clamps, checks choices and colours, refuses curves, accepts a bare filter id");
            r = Run(ed, "set_plugin", new JsonObject { ["filter"] = "fx:tone", ["values"] = new JsonObject { ["mode"] = "zzz", ["tint"] = "red" } });
            Check(r.Error && !r.Changed && ed.Texts["mode"] == "b", "ai: set_plugin with only invalid values is an error");
            Check(Run(ed, "set_plugin", new JsonObject { ["filter"] = "fx:missing", ["values"] = new JsonObject { ["a"] = 1 } }).Error, "ai: unknown filter is an error");
            r = Run(ed, "set_plugin_active", new JsonObject { ["filter"] = "fx:tone", ["active"] = true });
            Check(r.Error, "ai: a panel filter is not switched with set_plugin_active");
            r = Run(ed, "set_plugin_active", new JsonObject { ["filter"] = "Frame", ["active"] = true });
            Check(r.Changed && ed.FrameOn, "ai: set_plugin_active switches a toolbar filter (by label)");
            r = Run(ed, "reset_plugin", new JsonObject { ["filter"] = "fx:tone" });
            Check(r.Changed && ed.Vals["amount"] == 0 && ed.Texts["mode"] == "a", "ai: reset_plugin");
            r = Run(ed, "apply_preset", new JsonObject { ["preset"] = "warm" });
            Check(r.Changed && ed.AppliedPreset == "user:Warm" && ed.Core["Temperature"] == 20, "ai: apply_preset by name");
            Check(Run(ed, "apply_preset", new JsonObject { ["preset"] = "nope" }).Error, "ai: unknown preset is an error");
            var gs = JsonNode.Parse(Run(ed, "get_state", null).Content)!;
            Check(gs["plugins_on"]!.AsArray().Count == 1 && gs["geometry"]!["frame_aspect"]!.GetValue<double>() == 1.5 && gs["masks"] is JsonArray,
                "ai: get_state lists the plugins on, the geometry and the masks");
        }

        // ---- stage 3: crop and straighten ----
        {
            var ed = new FakeEditor();
            var r = Run(ed, "set_crop", new JsonObject { ["aspect"] = "1:1" });
            var g = ed.Geo;
            Check(r.Changed && Math.Abs(g.CropW - 2.0 / 3) < E && Math.Abs(g.CropH - 1) < E && Math.Abs(g.CropX - 1.0 / 6) < E && g.CropY == 0 && g.Aspect == "original",
                "ai: square crop of a 3:2 frame is centred and full height");
            r = Run(ed, "set_crop", new JsonObject { ["aspect"] = "3:2" });
            Check(ed.Geo.CropW == 1 && ed.Geo.CropH == 1 && ed.Geo.Aspect == "3:2", "ai: 3:2 of a 3:2 frame is the whole frame, labelled");
            r = Run(ed, "set_crop", new JsonObject { ["aspect"] = "2:3", ["size"] = 0.5, ["center_x"] = 0.05, ["center_y"] = 0.9 });
            g = ed.Geo;
            Check(Math.Abs(g.CropW - 2.0 / 9) < E && Math.Abs(g.CropH - 0.5) < E && g.CropX == 0 && Math.Abs(g.CropY - 0.5) < E,
                $"ai: portrait crop, half size, kept inside the frame ({g.CropX:0.###},{g.CropY:0.###} {g.CropW:0.###}x{g.CropH:0.###})");
            r = Run(ed, "set_crop", new JsonObject { ["aspect"] = "free", ["x"] = 0.1, ["y"] = 0.2, ["w"] = 2, ["h"] = 0.5 });
            g = ed.Geo;
            Check(g.CropX == 0.1 && g.CropY == 0.2 && Math.Abs(g.CropW - 0.9) < E && g.CropH == 0.5, "ai: free crop rectangle clamped to the frame");
            Check(Run(ed, "set_crop", new JsonObject { ["aspect"] = "wide" }).Error, "ai: unknown aspect is an error");
            r = Run(ed, "set_crop", new JsonObject { ["reset"] = true });
            Check(ed.Geo.CropW == 1 && ed.Geo.CropX == 0 && ed.Geo.Aspect == "original", "ai: crop reset");
            r = Run(ed, "straighten", new JsonObject { ["angle"] = "-2.5" });
            Check(r.Changed && ed.Geo.Angle == -2.5 && r.Content.Contains("\"angle\":-2.5"), "ai: straighten");
            Run(ed, "straighten", new JsonObject { ["angle"] = 90 });
            Check(ed.Geo.Angle == 45, "ai: straighten clamped to 45°");
            Check(Run(ed, "straighten", new JsonObject()).Error, "ai: straighten without an angle is an error");
        }

        // ---- stage 4: masks ----
        {
            var ed = new FakeEditor();
            Run(ed, "set_crop", new JsonObject { ["aspect"] = "1:1" }); // view x 0..1 = photo x 1/6..5/6
            var r = Run(ed, "create_mask", new JsonObject { ["kind"] = "radial", ["center_x"] = 0.5, ["center_y"] = 0.25, ["radius_x"] = 0.3, ["radius_y"] = 0.2 });
            var m = ed.MaskSet.Values.Single();
            Check(r.Changed && m.Key == "mask:1" && m.Kind == "radial" && Math.Abs(m.P[0] - 0.5) < E && Math.Abs(m.P[1] - 0.25) < E &&
                  // the model's radii are the inner ellipse; the default feather (1 = 100 % in the panel) doubles it outside
                  Math.Abs(m.P[2] - 0.4) < E && Math.Abs(m.P[3] - 0.4) < E && Math.Abs(m.P[4] - 0.5) < E && r.Content.Contains("changes nothing yet"),
                "ai: radial mask placed in view coordinates (mapped through the crop)");
            JsonNode Gm() => JsonNode.Parse(Run(ed, "get_state", null).Content)!["masks"]!.AsArray()[0]!;
            double G(string k) => Gm()[k]!.GetValue<double>();
            Check(Math.Abs(G("center_x") - 0.5) < 1e-3 && Math.Abs(G("radius_x") - 0.3) < 1e-3 && Math.Abs(G("radius_y") - 0.2) < 1e-3 &&
                  Math.Abs(G("feather") - 1) < 1e-3, "ai: get_state shows the mask in view coordinates, feather as the panel slider");
            r = Run(ed, "create_mask", new JsonObject { ["mask"] = "mask:1", ["feather"] = 0.5 });
            m = ed.MaskSet["mask:1"];
            Check(r.Changed && Math.Abs(ImgViewer.MaskRender.RadialUserFeather(m.P[4]) - 0.5) < E && Math.Abs(m.P[2] - 0.3) < E && Math.Abs(m.P[1] - 0.25) < E &&
                  Math.Abs(G("radius_x") - 0.3) < 1e-3 && Math.Abs(G("feather") - 0.5) < 1e-3,
                "ai: feather 0.5 = 50 % in the panel; reshaping only the feather keeps the centre and the ellipse");
            Run(ed, "create_mask", new JsonObject { ["mask"] = "mask:1", ["center_x"] = 0.4 });
            Check(Math.Abs(G("center_x") - 0.4) < 1e-3 && Math.Abs(G("feather") - 0.5) < 1e-3 && Math.Abs(G("radius_y") - 0.2) < 1e-3,
                "ai: moving a mask keeps its feather and size");
            Run(ed, "create_mask", new JsonObject { ["mask"] = "mask:1", ["feather"] = 0 });
            Check(ed.MaskSet["mask:1"].P[4] == 0 && Math.Abs(G("radius_x") - 0.3) < 1e-3, "ai: feather 0 = hard edge on the ellipse");
            r = Run(ed, "set_mask_values", new JsonObject { ["mask"] = "1", ["values"] = new JsonObject { ["exposure"] = 0.5, ["Shadows"] = 20, ["Clarity"] = 0 } });
            m = ed.MaskSet["mask:1"];
            Check(r.Changed && m.Values!.Count == 1 && m.Values["Exposure"] == 0.5 && r.Content.Contains("Shadows") && r.StepNote!.Contains("+0.5"),
                "ai: set_mask_values (maskable keys only, zero dropped, short key accepted)");
            r = Run(ed, "set_mask_values", new JsonObject { ["mask"] = "mask:1", ["values"] = new JsonObject() });
            Check(r.Changed && ed.MaskSet["mask:1"].Values is null, "ai: set_mask_values {} removes the values");
            r = Run(ed, "link_tool_to_mask", new JsonObject { ["filter"] = "fx:tone", ["mask"] = "mask:1" });
            Check(r.Changed && ed.ToolMask["fx:tone"] == "mask:1" && ed.Masks()[0].UsedBy.Contains("fx:tone"), "ai: a filter limited to a mask");
            Check(Run(ed, "link_tool_to_mask", new JsonObject { ["filter"] = "fx:frame", ["mask"] = "mask:1" }).Error, "ai: a filter without mask support refused");
            Check(Run(ed, "link_tool_to_mask", new JsonObject { ["filter"] = "fx:tone", ["mask"] = "mask:9" }).Error, "ai: unknown mask refused");
            r = Run(ed, "create_mask", new JsonObject { ["mask"] = "mask:1", ["kind"] = "linear", ["x1"] = 0, ["y1"] = 0, ["x2"] = 0, ["y2"] = 0.5, ["invert"] = true });
            m = ed.MaskSet["mask:1"];
            Check(r.Changed && ed.MaskSet.Count == 1 && m.Kind == "linear" && m.Invert && Math.Abs(m.P[0] - 1.0 / 6) < E && m.P[3] == 0.5,
                "ai: create_mask with mask reshapes it");
            Check(Run(ed, "create_mask", new JsonObject { ["kind"] = "linear", ["x1"] = 0.5, ["y1"] = 0.5, ["x2"] = 0.5, ["y2"] = 0.5 }).Error,
                "ai: a linear mask needs two points apart");
            r = Run(ed, "create_mask", new JsonObject { ["kind"] = "luma", ["low"] = 0.9, ["high"] = 0.6 });
            Check(r.Changed && ed.MaskSet["mask:2"].P.SequenceEqual(new[] { 0.6, 0.9, 0.1 }), "ai: luma mask (range ordered)");
            Run(ed, "create_mask", new JsonObject { ["mask"] = "mask:2", ["feather"] = 0.3 });
            Check(ed.MaskSet["mask:2"].P.SequenceEqual(new[] { 0.6, 0.9, 0.3 }), "ai: reshaping a luma mask keeps its range");
            Check(Run(ed, "create_mask", new JsonObject { ["kind"] = "blob" }).Error, "ai: unknown mask kind refused");
            r = Run(ed, "link_tool_to_mask", new JsonObject { ["filter"] = "fx:tone", ["mask"] = "none" });
            Check(r.Changed && !ed.ToolMask.ContainsKey("fx:tone"), "ai: back to the whole photo");
            r = Run(ed, "delete_mask", new JsonObject { ["mask"] = "mask:2" });
            Check(r.Changed && !ed.MaskSet.ContainsKey("mask:2"), "ai: delete_mask");
            Check(Run(ed, "delete_mask", new JsonObject { ["mask"] = "mask:2" }).Error, "ai: deleting a missing mask is an error");
        }

        // ---- a whole turn with the new tools, folded into the summary ----
        {
            var ed = new FakeEditor();
            var chat = new ScriptedChat(
                Call(("straighten", new JsonObject { ["angle"] = 1.5 }), ("set_crop", new JsonObject { ["aspect"] = "4:5" })),
                Call(("create_mask", new JsonObject { ["kind"] = "radial", ["center_x"] = 0.5, ["center_y"] = 0.4, ["radius_x"] = 0.2, ["radius_y"] = 0.25 })),
                Call(("set_mask_values", new JsonObject { ["mask"] = "mask:1", ["values"] = new JsonObject { ["Exposure"] = 0.4 } })),
                Final("Wyprostowałem, wykadrowałem 4:5 i rozjaśniłem twarz."));
            var session = new AiSession();
            var res = session.RunTurnAsync(chat, ed, "m", false, "portret do druku", new AiTurnOptions(8), null, CancellationToken.None).GetAwaiter().GetResult();
            var sum = session.Messages.OfType<JsonObject>().Last()["content"]!.ToString();
            Check(res.Changed && sum.Contains("straighten") && sum.Contains("crop 4:5") && sum.Contains("created mask:1") && sum.Contains("Exposure +0.4"),
                "ai: stage 2-4 steps are folded into the turn summary");

            // ---- the conversation in the .eye file ----
            var saved = session.Export()!;
            var eye = new EyeState { Exposure = 0.3, Assistant = saved };
            var json = EyeFile.Serialize(eye);
            var back = System.Text.Json.JsonSerializer.Deserialize<EyeState>(json)!;
            var again = AiSession.Import(back.Assistant!);
            var a = session.Messages.OfType<JsonObject>().Select(x => x["role"] + "|" + x["content"]).ToList();
            var b = again.Messages.OfType<JsonObject>().Select(x => x["role"] + "|" + x["content"]).ToList();
            Check(a.SequenceEqual(b) && again.Transcript.Select(e => e.Kind + e.Text).SequenceEqual(session.Transcript.Select(e => e.Kind + e.Text)) &&
                  again.TotalPromptTokens == session.TotalPromptTokens && again.TotalOutputTokens == session.TotalOutputTokens &&
                  again.TotalCalls == session.TotalCalls, "ai: the conversation survives the .eye file (history, transcript, counters)");
            Check(!json.Contains("\"images\"") && !json.Contains("system") && !EyeFile.Serialize(new EyeState()).Contains("Assistant"),
                "ai: .eye holds no previews nor the system prompt; no conversation, no field");
            Check(new AiSession().Export() is null, "ai: an empty conversation is not stored");
            // the restored conversation continues
            var chat2 = new ScriptedChat(Final("ok"));
            again.RunTurnAsync(chat2, ed, "m", false, "dalej", new AiTurnOptions(8), null, CancellationToken.None).GetAwaiter().GetResult();
            Check(chat2.LastMessages!.Count == 4 && chat2.LastMessages[0]!["role"]!.ToString() == "system", "ai: a restored conversation continues with the system prompt");
        }
    }
}
