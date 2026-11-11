using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ImgViewer.Services.Ai;

/// <summary>What the assistant may read and change in the app (the main window in the app, a fake in the harness).</summary>
public interface IAiEditor
{
    /// <summary>Current values of the basic settings (CoreParams keys).</summary>
    IReadOnlyDictionary<string, double> GetCore();
    /// <summary>Sets the given basic settings (already validated and clamped) and starts a render.</summary>
    void SetCore(IReadOnlyDictionary<string, double> values);
    /// <summary>Waits until the photo shows the current settings.</summary>
    Task WaitRenderedAsync(CancellationToken ct);
    /// <summary>Statistics of the shown render; null when no photo is shown.</summary>
    ImageStats? Analyze();
    /// <summary>JPEG of the shown render (base64); null when no photo is shown.</summary>
    string? PreviewJpegBase64(int maxLongSide);
    /// <summary>Size, file type and (when allowed) camera data of the photo.</summary>
    JsonObject PhotoInfo();
}

/// <summary>Result of one tool call.</summary>
public sealed record AiToolResult(string Content, string? ImageBase64 = null, string? StepNote = null, bool Changed = false, bool Error = false);

/// <summary>The functions the model may call (JSON schemas) and their execution with validation.</summary>
public static class AiTools
{
    public const int PreviewLongSide = 1024;

    private static readonly Dictionary<string, string> Hints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Exposure"] = "exposure in EV stops",
        ["Contrast"] = "global contrast",
        ["Temperature"] = "white balance: negative cooler (blue), positive warmer (yellow)",
        ["Tint"] = "white balance: negative green, positive magenta",
        ["Saturation"] = "colour saturation",
        ["Highlights"] = "negative recovers bright areas, positive brightens them",
        ["Shadows"] = "positive lifts dark areas, negative deepens them",
        ["Whites"] = "white point: positive brighter whites (watch clipping)",
        ["Blacks"] = "black point: negative deeper blacks",
        ["Clarity"] = "local mid-tone contrast (texture, grit); negative softens (skin)",
        ["Dehaze"] = "removes haze / adds atmosphere when negative",
        ["Sharpen"] = "sharpening amount",
        ["Denoise"] = "luminance noise reduction",
        ["ColorNoise"] = "colour noise reduction",
        ["Gamma"] = "midtone gamma, 1 = neutral; above 1 brighter midtones",
        ["Mono"] = "black & white: 0 colour, 1 documentary B&W, 2 Tri-X 400 film B&W (grain)"
    };

    /// <summary>Function schemas in Ollama/OpenAI format. <paramref name="vision"/> adds get_preview.</summary>
    public static JsonArray Catalog(bool vision)
    {
        var coreProps = new JsonObject();
        foreach (var p in CoreParams.All)
        {
            coreProps[p.Key] = new JsonObject
            {
                ["type"] = p.Integer ? "integer" : "number",
                ["description"] = string.Format(CultureInfo.InvariantCulture, "{0}; range {1}..{2}, default {3}",
                    Hints.TryGetValue(p.Key, out var h) ? h : p.Label, p.Min, p.Max, p.Default),
                ["minimum"] = p.Min,
                ["maximum"] = p.Max
            };
        }
        var keyEnum = new JsonArray(CoreParams.All.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray());
        var tools = new JsonArray
        {
            Fn("get_state", "Current values of all basic settings with their ranges and defaults, and the photo info.",
                new JsonObject()),
            Fn("analyze_image", "Statistics of the photo as it looks now (after all edits): luminance percentiles, clipping, saturation, colour cast, 3x3 zone brightness. All values 0..100.",
                new JsonObject()),
            Fn("set_core", "Set one or more basic settings at once (absolute values, not deltas). Only the given keys change; values are clamped to the range. Returns the applied values and the image statistics after the change.",
                coreProps),
            Fn("reset_core", "Reset basic settings to their defaults. Without keys resets all of them.",
                new JsonObject { ["keys"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = keyEnum } } }),
        };
        if (vision)
            tools.Add(Fn("get_preview", "Look at the photo as it is now (a JPEG preview is attached to the next message).", new JsonObject()));
        return tools;
    }

    private static JsonObject Fn(string name, string description, JsonObject properties) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties }
        }
    };

    /// <summary>Arguments as an object: models send an object or a JSON string.</summary>
    public static JsonObject Args(JsonNode? args)
    {
        if (args is JsonObject o) return o;
        if (args is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
        {
            try
            {
                if (JsonNode.Parse(s) is JsonObject parsed) return parsed;
            }
            catch (JsonException)
            {
            }
        }
        return new JsonObject();
    }

    public static async Task<AiToolResult> ExecuteAsync(IAiEditor ed, string name, JsonNode? rawArgs, bool vision, CancellationToken ct)
    {
        var args = Args(rawArgs);
        try
        {
            switch (name)
            {
                case "get_state":
                {
                    var o = new JsonObject { ["photo"] = ed.PhotoInfo(), ["settings"] = StateJson(ed.GetCore()) };
                    return new AiToolResult(o.ToJsonString());
                }
                case "analyze_image":
                {
                    await ed.WaitRenderedAsync(ct);
                    var st = ed.Analyze();
                    return st is null
                        ? new AiToolResult(Err("no photo is shown"), Error: true)
                        : new AiToolResult(st.ToJson().ToJsonString(), StepNote: "analysed: " + st.Summary());
                }
                case "get_preview":
                {
                    if (!vision) return new AiToolResult(Err("this model cannot see images; use analyze_image"), Error: true);
                    await ed.WaitRenderedAsync(ct);
                    var jpg = ed.PreviewJpegBase64(PreviewLongSide);
                    return jpg is null
                        ? new AiToolResult(Err("no photo is shown"), Error: true)
                        : new AiToolResult("{\"ok\":true,\"note\":\"the preview is attached to the next message\"}", jpg, "looked at the photo");
                }
                case "set_core":
                    return await SetCoreAsync(ed, args, ct);
                case "reset_core":
                {
                    var keys = args["keys"] is JsonArray ka
                        ? ka.Select(k => k?.ToString() ?? "").Select(CoreParams.Find).Where(p => p is not null).Select(p => p!).ToList()
                        : CoreParams.All.ToList();
                    if (keys.Count == 0) keys = CoreParams.All.ToList();
                    var cur = ed.GetCore();
                    var changes = keys.Where(p => !cur.TryGetValue(p.Key, out var v) || Math.Abs(v - p.Default) > 1e-9)
                        .ToDictionary(p => p.Key, p => p.Default);
                    if (changes.Count > 0)
                    {
                        ed.SetCore(changes);
                        await ed.WaitRenderedAsync(ct);
                    }
                    var after = ed.Analyze();
                    var res = new JsonObject
                    {
                        ["reset"] = new JsonArray(changes.Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
                        ["image_after"] = after?.ToJson()
                    };
                    return new AiToolResult(res.ToJsonString(), StepNote: changes.Count == 0 ? null : "reset " + string.Join(", ", changes.Keys),
                        Changed: changes.Count > 0);
                }
                default:
                    return new AiToolResult(Err($"unknown function '{name}'"), Error: true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AiToolResult(Err(ex.Message), Error: true);
        }
    }

    private static async Task<AiToolResult> SetCoreAsync(IAiEditor ed, JsonObject args, CancellationToken ct)
    {
        // {"Exposure":0.3} or {"values":{...}} / {"settings":{...}}
        var src = args["values"] as JsonObject ?? args["settings"] as JsonObject ?? args;
        var cur = ed.GetCore();
        var apply = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var ignored = new List<string>();
        foreach (var kv in src)
        {
            var p = CoreParams.Find(kv.Key);
            if (p is null || !TryNumber(kv.Value, out double v))
            {
                ignored.Add(kv.Key);
                continue;
            }
            apply[p.Key] = p.Clamp(v);
        }
        if (apply.Count == 0)
            return new AiToolResult(Err("no valid settings given" + (ignored.Count > 0 ? " (unknown or not numbers: " + string.Join(", ", ignored) + ")" : "")), Error: true);

        ed.SetCore(apply);
        await ed.WaitRenderedAsync(ct);
        var after = ed.Analyze();
        var applied = new JsonObject();
        foreach (var kv in apply) applied[kv.Key] = Math.Round(kv.Value, 3);
        var res = new JsonObject { ["applied"] = applied, ["image_after"] = after?.ToJson() };
        if (ignored.Count > 0) res["ignored"] = string.Join(", ", ignored);
        return new AiToolResult(res.ToJsonString(), StepNote: Describe(apply, cur), Changed: true);
    }

    /// <summary>"Exposure 0.00 → 0.30, Contrast 0 → 15" for the transcript.</summary>
    public static string Describe(IReadOnlyDictionary<string, double> apply, IReadOnlyDictionary<string, double> before)
    {
        var sb = new StringBuilder();
        foreach (var kv in apply)
        {
            var p = CoreParams.Find(kv.Key)!;
            if (sb.Length > 0) sb.Append(", ");
            double was = before.TryGetValue(p.Key, out var b) ? b : p.Default;
            sb.Append(p.Label).Append(' ').Append(p.Format(was)).Append(" → ").Append(p.Format(kv.Value));
        }
        return sb.ToString();
    }

    public static JsonObject StateJson(IReadOnlyDictionary<string, double> core)
    {
        var o = new JsonObject();
        foreach (var p in CoreParams.All)
        {
            o[p.Key] = new JsonObject
            {
                ["value"] = Math.Round(core.TryGetValue(p.Key, out var v) ? v : p.Default, 3),
                ["min"] = p.Min,
                ["max"] = p.Max,
                ["default"] = p.Default
            };
        }
        return o;
    }

    private static bool TryNumber(JsonNode? n, out double v)
    {
        v = 0;
        if (n is not JsonValue jv) return false;
        if (jv.TryGetValue(out double d)) v = d;
        else if (jv.TryGetValue(out int i)) v = i;
        else if (jv.TryGetValue(out long l)) v = l;
        else if (jv.TryGetValue(out bool bo)) v = bo ? 1 : 0;
        else if (jv.TryGetValue(out string? s) && double.TryParse(s?.Trim().TrimStart('+'), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) v = d;
        else return false;
        return double.IsFinite(v);
    }

    private static string Err(string message) => new JsonObject { ["error"] = message }.ToJsonString();
}
