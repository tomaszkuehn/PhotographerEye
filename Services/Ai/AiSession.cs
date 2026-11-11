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

public sealed record AiTurnResult(string FinalText, int ModelCalls, bool Changed, bool StoppedAtLimit);

/// <summary>
/// One conversation about one photo: the message history and the agent loop (model → tools → render → model ...).
/// Independent of WPF; the app supplies <see cref="IAiEditor"/> and <see cref="IAiChat"/>.
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
3. Check the result (statistics, preview when available) and refine with small corrections. Usually 2-4 rounds are enough.
4. Keep it natural unless asked otherwise: avoid blown highlights (clipped_highlights_pct above ~0.5) and blocked shadows
   unless they are part of the style; skin tones must stay believable.
5. Finish with a short answer in the language of the user's request: what you changed and why (2-4 sentences).
   Do not list raw JSON. If something cannot be done with the available functions (cropping, masks, plugins are not
   available yet), say so briefly.

Style guidance (starting points, adapt to the photo):
- Family album: bright and friendly, natural warm white balance, gentle contrast, open shadows, soft clarity (0..-10)
  for skin, moderate saturation, no clipping.
- Reportage / documentary: honest light, stronger contrast and clarity, slightly reduced saturation, deeper blacks,
  neutral white balance; grit is fine, no glossy look.
- Artistic black & white: Mono 1 (documentary) or 2 (Tri-X film grain), strong tonal separation (contrast, whites,
  blacks), clarity for texture; dodge nothing you cannot see — rely on global tones.
Follow-up requests ("warmer", "less contrast", "undo the B&W") start from the current settings.
""";

    private readonly JsonArray _messages = new();
    public IReadOnlyList<AiEvent> Transcript => _transcript;
    private readonly List<AiEvent> _transcript = new();

    public AiSession()
    {
        _messages.Add(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt });
    }

    /// <summary>Messages sent to the model (for tests and diagnostics).</summary>
    public JsonArray Messages => _messages;

    public void Note(AiEvent e) => _transcript.Add(e);

    /// <summary>
    /// Runs one assistant turn for <paramref name="userText"/>. <paramref name="onEvent"/> gets the transcript lines as they
    /// happen (on the caller's context). Cancelling stops at the next await; changes made so far stay.
    /// </summary>
    public async Task<AiTurnResult> RunTurnAsync(IAiChat chat, IAiEditor editor, string model, bool vision, string userText,
        int maxSteps, Action<AiEvent>? onEvent, CancellationToken ct)
    {
        int before = _messages.Count;
        try
        {
            return await RunTurnCoreAsync(chat, editor, model, vision, userText, maxSteps, onEvent, ct);
        }
        catch (Exception ex)
        {
            // a call left half-way (no tool results, or no answer) must not break the next turn: close it with a note
            if (_messages.Count > before)
                _messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = ex is OperationCanceledException ? "(The user stopped this turn.)" : "(This turn failed: " + ex.Message + ")"
                });
            throw;
        }
    }

    private async Task<AiTurnResult> RunTurnCoreAsync(IAiChat chat, IAiEditor editor, string model, bool vision, string userText,
        int maxSteps, Action<AiEvent>? onEvent, CancellationToken ct)
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
        maxSteps = Math.Clamp(maxSteps, 1, 30);
        while (calls < maxSteps)
        {
            ct.ThrowIfCancellationRequested();
            calls++;
            var msg = await chat.ChatAsync(model, _messages, tools, ct);
            var content = msg["content"]?.ToString() ?? "";
            var toolCalls = (msg["tool_calls"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();

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
        // let the next turn start cleanly: the model sees that it was stopped
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = "(The step limit was reached; the turn was stopped.)" });
        return new AiTurnResult(limit, calls, changed, StoppedAtLimit: true);
    }

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
