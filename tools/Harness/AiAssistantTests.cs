using System;
using System.Collections.Generic;
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
        Check(names.SequenceEqual(new[] { "get_state", "analyze_image", "set_core", "reset_core" }), "ai: text-only catalogue");
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
            Check(chat.ToolCounts.All(c => c == 4) && !chat.AnyImage, "ai: text-only model gets no images");
            Check(session.Messages[0]!["role"]!.ToString() == "system", "ai: system prompt first");

            // compaction: system, the plain request, one summary with the changes and the answer
            var h = session.Messages.OfType<JsonObject>().ToList();
            Check(h.Count == 3 && h[1]["role"]!.ToString() == "user" && h[1]["content"]!.ToString() == "przygotuj do albumu" &&
                  h[2]["role"]!.ToString() == "assistant" && h[2]["tool_calls"] is null &&
                  h[2]["content"]!.ToString().StartsWith("[changes: ") && h[2]["content"]!.ToString().Contains("Saturation") &&
                  h[2]["content"]!.ToString().EndsWith("Gotowe: rozjaśniłem zdjęcie."), "ai: the turn is folded into request + summary");
            int fullCtx = AiSession.EstimateTokens(chat.LastMessages!);
            Check(AiSession.EstimateTokens(session.Messages) < fullCtx, $"ai: compaction shrinks the history ({fullCtx} -> {AiSession.EstimateTokens(session.Messages)} est. tokens)");

            // token counters from the server's counts (scripted: 100 read + 10 written per call)
            Check(session.TotalCalls == 5 && session.TotalPromptTokens == 500 && session.TotalOutputTokens == 50 &&
                  session.LastPromptTokens == 100 && !session.Estimated, "ai: token counters add up the reported counts");

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
            var res = session.RunTurnAsync(new ScriptedChat(loop), ed, "m", false, "x", new AiTurnOptions(8, TokenBudget: 250), null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(res.StoppedAtBudget && res.ModelCalls == 3 && session.OverBudget(250) && !session.OverBudget(0),
                "ai: the token budget stops the turn before the next call");
            Check(session.Messages.OfType<JsonObject>().Last()["content"]!.ToString().Contains("token budget"), "ai: budget stop noted in the history");
            // no counts from the server: estimated from the request size
            var s2 = new AiSession();
            s2.RunTurnAsync(new ScriptedChat(Final("ok")) { NoCounts = true }, ed, "m", false, "x", new AiTurnOptions(), null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(s2.Estimated && s2.LastPromptTokens > 500 && s2.TotalOutputTokens > 0, "ai: missing counts are estimated");
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
            Check(chat.ToolCounts.All(c => c == 5) && chat.FirstUserHadImage, "ai: vision model gets get_preview and the photo with the request");
            Check(!session.Messages.ToJsonString().Contains("\"images\""), "ai: no image stays in the history after the turn");
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
            return Task.FromResult(NoCounts ? new AiChatReply(msg) : new AiChatReply(msg, 100, 10));
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

        public JsonObject PhotoInfo() => new() { ["type"] = "JPG", ["width"] = 8, ["height"] = 8 };
    }
}
