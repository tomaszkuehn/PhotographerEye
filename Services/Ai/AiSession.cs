using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ImgViewer.Services.Ai;

public enum AiEventKind
{
    User,
    Assistant,
    /// <summary>A tool changed or inspected the photo (muted line in the transcript).</summary>
    Step,
    Error,
    Info
}

public sealed record AiEvent(AiEventKind Kind, string Text);

public sealed record AiTurnResult(string FinalText, int ModelCalls, bool Changed, bool StoppedAtLimit, bool StoppedAtBudget = false);

/// <summary>Limits of one turn: model calls, and the token budget of the whole conversation (0 = none).</summary>
public sealed record AiTurnOptions(int MaxSteps = 8, long TokenBudget = 0);

/// <summary>
/// One conversation about one photo: the message history and the agent loop (model → tools → render → model ...).
/// Independent of WPF; the app supplies <see cref="IAiEditor"/> and <see cref="IAiChat"/>.
/// <para>
/// Context control: after every turn its working messages (function calls, their JSON results, previews) are folded
/// into one short assistant message — "[changes: …] answer" — so the history grows by a few dozen tokens per request
/// instead of thousands; within a turn only the newest preview stays; the conversation has a token budget.
/// </para>
/// </summary>
public sealed class AiSession
{
    public const string SystemPrompt = """
You are the photo-editing assistant built into PhotographerEye, a photo editor. The user describes the result they want
("prepare this photo for a family album", "make it look like reportage", "artistic black and white", "a bit warmer").
You edit the photo yourself by calling the functions; the user sees every change live.

How to work:
1. Inspect first: call get_state and analyze_image (and get_preview when available) before changing anything.
2. Decide on a look that fits the request and the photo, then apply it with set_core (several keys in one call).
   set_core takes absolute values, not deltas, and returns statistics of the result.
3. Check the result (statistics, preview when available) and refine with small corrections. Usually 2-4 rounds are enough;
   be economical with calls.
4. Keep it natural unless asked otherwise: avoid blown highlights (clipped_highlights_pct above ~0.5) and blocked shadows
   unless they are part of the style; skin tones must stay believable.
5. Finish with a short answer in the language of the user's request: what you changed and why (2-4 sentences).
   Do not list raw JSON. If something cannot be done with the available functions (cropping, masks, plugins are not
   available yet), say so briefly.

Earlier requests appear in short form ("[changes: …]"); the settings may also have been changed by hand since, so call
get_state before building on them.

Style guidance (starting points, adapt to the photo):
- Family album: bright and friendly, natural warm white balance, gentle contrast, open shadows, soft clarity (0..-10)
  for skin, moderate saturation, no clipping.
- Reportage / documentary: honest light, stronger contrast and clarity, slightly reduced saturation, deeper blacks,
  neutral white balance; grit is fine, no glossy look.
- Artistic black & white: Mono 1 (documentary) or 2 (Tri-X film grain), strong tonal separation (contrast, whites,
  blacks), clarity for texture; dodge nothing you cannot see — rely on global tones.
Follow-up requests ("warmer", "less contrast", "undo the B&W") start from the current settings.
""";

    /// <summary>Rough token cost of one attached image (vision models: ~1k for a 1024 px preview).</summary>
    public const int ImageTokens = 1000;

    private readonly JsonArray _messages = new();
    private readonly List<AiEvent> _transcript = new();
    private readonly List<string> _turnChanges = new();

    public AiSession()
    {
        _messages.Add(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt });
    }

    public IReadOnlyList<AiEvent> Transcript => _transcript;

    /// <summary>Messages sent to the model (for tests and diagnostics).</summary>
    public JsonArray Messages => _messages;

    /// <summary>Tokens the model read in the last call (= the size of the context it got); 0 before the first call.</summary>
    public int LastPromptTokens { get; private set; }
    /// <summary>Totals over the whole conversation (all calls): read and written tokens.</summary>
    public long TotalPromptTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }
    public long TotalTokens => TotalPromptTokens + TotalOutputTokens;
    public int TotalCalls { get; private set; }
    /// <summary>True when the server did not report token counts and the totals are estimated.</summary>
    public bool Estimated { get; private set; }

    public bool OverBudget(long budget) => budget > 0 && TotalTokens >= budget;

    public void Note(AiEvent e) => _transcript.Add(e);

    /// <summary>Estimated size of the context the next call would send (history + function schemas), in tokens.</summary>
    public int EstimateNextContext(bool vision) => EstimateTokens(_messages) + EstimateTokens(AiTools.Catalog(vision));

    /// <summary>~4 characters per token for text and JSON, <see cref="ImageTokens"/> per image.</summary>
    public static int EstimateTokens(JsonArray nodes)
    {
        long chars = 0;
        int images = 0;
        foreach (var n in nodes)
        {
            if (n is not JsonObject o)
            {
                chars += n?.ToJsonString().Length ?? 0;
                continue;
            }
            foreach (var kv in o)
            {
                if (kv.Key == "images" && kv.Value is JsonArray imgs)
                    images += imgs.Count;
                else
                    chars += kv.Value?.ToJsonString().Length ?? 0;
            }
        }
        return (int)Math.Min(int.MaxValue, chars / 4 + (long)images * ImageTokens);
    }

    /// <summary>
    /// Runs one assistant turn for <paramref name="userText"/>. <paramref name="onEvent"/> gets the transcript lines as they
    /// happen (on the caller's context); the token counters are already updated when it is called. Cancelling stops at the
    /// next await; changes made so far stay. Whatever the outcome, the turn is then folded into a short summary.
    /// </summary>
    public async Task<AiTurnResult> RunTurnAsync(IAiChat chat, IAiEditor editor, string model, bool vision, string userText,
        AiTurnOptions options, Action<AiEvent>? onEvent, CancellationToken ct)
    {
        int userIdx = _messages.Count;
        _turnChanges.Clear();
        try
        {
            var res = await RunTurnCoreAsync(chat, editor, model, vision, userText, options, onEvent, ct);
            Compact(userIdx, userText, res.StoppedAtBudget ? "(Stopped: the token budget of this conversation was reached.)"
                : res.StoppedAtLimit ? "(Stopped: the step limit of one request was reached.)"
                : res.FinalText);
            return res;
        }
        catch (Exception ex)
        {
            Compact(userIdx, userText, ex is OperationCanceledException ? "(The user stopped this request.)" : "(This request failed: " + ex.Message + ")");
            throw;
        }
    }

    /// <summary>
    /// Replaces the turn's working messages (from the user message on) with the plain request and one assistant message
    /// "[changes: …] answer". Function calls and results go together, so the history stays valid for the next call.
    /// </summary>
    private void Compact(int userIdx, string userText, string outcome)
    {
        if (_messages.Count <= userIdx) return; // the request never reached the history
        while (_messages.Count > userIdx) _messages.RemoveAt(_messages.Count - 1);
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = userText });
        var summary = _turnChanges.Count > 0 ? "[changes: " + string.Join("; ", _turnChanges) + "]\n" + outcome : outcome;
        _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = summary });
        _turnChanges.Clear();
    }

    private async Task<AiTurnResult> RunTurnCoreAsync(IAiChat chat, IAiEditor editor, string model, bool vision, string userText,
        AiTurnOptions options, Action<AiEvent>? onEvent, CancellationToken ct)
    {
        void Emit(AiEventKind k, string t)
        {
            var e = new AiEvent(k, t);
            _transcript.Add(e);
            onEvent?.Invoke(e);
        }

        Emit(AiEventKind.User, userText);
        var user = new JsonObject { ["role"] = "user", ["content"] = userText };
        if (vision)
        {
            await editor.WaitRenderedAsync(ct);
            if (editor.PreviewJpegBase64(AiTools.PreviewLongSide) is { } jpg)
            {
                DropOldImages();
                user["images"] = new JsonArray(jpg);
                user["content"] = userText + "\n\n(The attached image is the photo as it looks now.)";
            }
        }
        _messages.Add(user);

        var tools = AiTools.Catalog(vision);
        bool changed = false;
        int calls = 0;
        int maxSteps = Math.Clamp(options.MaxSteps, 1, 30);
        while (calls < maxSteps)
        {
            ct.ThrowIfCancellationRequested();
            if (OverBudget(options.TokenBudget))
            {
                var msg = $"Token budget of this conversation reached ({Fmt(TotalTokens)} of {Fmt(options.TokenBudget)}). " +
                          "The changes so far are applied. Start a new conversation or raise the budget in ⚙.";
                Emit(AiEventKind.Info, msg);
                return new AiTurnResult(msg, calls, changed, StoppedAtLimit: false, StoppedAtBudget: true);
            }
            calls++;
            int estimate = EstimateTokens(_messages) + EstimateTokens(tools);
            var reply = await chat.ChatAsync(model, _messages, tools, ct);
            Count(reply, estimate);
            var content = reply.Message["content"]?.ToString() ?? "";
            var toolCalls = (reply.Message["tool_calls"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();

            var stored = new JsonObject { ["role"] = "assistant", ["content"] = content };
            if (toolCalls.Count > 0) stored["tool_calls"] = new JsonArray(toolCalls.Select(t => Json.Clone(t)).ToArray());
            _messages.Add(stored);

            if (toolCalls.Count == 0)
            {
                var final = string.IsNullOrWhiteSpace(content) ? "(no answer)" : content.Trim();
                Emit(AiEventKind.Assistant, final);
                return new AiTurnResult(final, calls, changed, StoppedAtLimit: false);
            }
            if (!string.IsNullOrWhiteSpace(content)) Emit(AiEventKind.Info, content.Trim());

            string? image = null;
            foreach (var tc in toolCalls)
            {
                var fn = tc["function"] as JsonObject;
                var name = fn?["name"]?.ToString() ?? "";
                var res = await AiTools.ExecuteAsync(editor, name, fn?["arguments"], vision, ct);
                changed |= res.Changed;
                if (res.ImageBase64 is not null) image = res.ImageBase64;
                if (res.Changed && res.StepNote is { } change) _turnChanges.Add(change);
                if (res.Error) Emit(AiEventKind.Step, $"{name}: {ErrorText(res.Content)}");
                else if (res.StepNote is { } note) Emit(AiEventKind.Step, note);
                _messages.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = name, ["content"] = res.Content });
            }
            if (image is not null)
            {
                DropOldImages();
                _messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "Preview of the photo now (requested with get_preview).",
                    ["images"] = new JsonArray(image)
                });
            }
        }

        const string limit = "Step limit reached — the changes so far are applied (undo removes them). Ask me to continue if needed.";
        Emit(AiEventKind.Info, limit);
        return new AiTurnResult(limit, calls, changed, StoppedAtLimit: true);
    }

    private void Count(AiChatReply reply, int estimate)
    {
        TotalCalls++;
        int prompt = reply.PromptTokens;
        int output = reply.OutputTokens;
        if (prompt <= 0)
        {
            prompt = estimate;
            Estimated = true;
        }
        if (output <= 0) output = (reply.Message.ToJsonString().Length + 3) / 4;
        LastPromptTokens = prompt;
        TotalPromptTokens += prompt;
        TotalOutputTokens += output;
    }

    /// <summary>"12.3k" / "850".</summary>
    public static string Fmt(long tokens) => tokens >= 1_000_000 ? (tokens / 1e6).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "M"
        : tokens >= 1000 ? (tokens / 1e3).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k"
        : tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Keeps only the newest image in the history (older previews would cost tokens and confuse the model).</summary>
    private void DropOldImages()
    {
        foreach (var m in _messages.OfType<JsonObject>())
        {
            if (m["images"] is null) continue;
            m.Remove("images");
            m["content"] = (m["content"]?.ToString() ?? "") + " [older preview removed]";
        }
    }

    private static string ErrorText(string json)
    {
        try
        {
            return JsonNode.Parse(json)?["error"]?.ToString() ?? json;
        }
        catch (Exception)
        {
            return json;
        }
    }
}
