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

    // ---- stage 2: plugin filters and presets ----
    /// <summary>Filters the assistant may use (faulted plugins left out), with their parameters and current values.</summary>
    IReadOnlyList<AiFilter> Filters();
    /// <summary>Sets parameters of one filter (already validated: numbers clamped, choices and colours checked) and renders.</summary>
    void SetFilter(string id, IReadOnlyDictionary<string, double> values, IReadOnlyDictionary<string, string> text);
    /// <summary>Switches a toolbar filter on or off and renders.</summary>
    void SetFilterActive(string id, bool active);
    /// <summary>Back to the filter's defaults (its mask stays) and renders.</summary>
    void ResetFilter(string id);
    /// <summary>User presets and plugin presets.</summary>
    IReadOnlyList<AiPreset> Presets();
    /// <summary>Applies a preset (whole-photo values) and renders.</summary>
    void ApplyPreset(string id);

    // ---- stage 3: crop and straighten ----
    AiGeometry GetGeometry();
    /// <summary>Sets angle and crop (already validated) and renders.</summary>
    void SetGeometry(AiGeometry g);

    // ---- stage 4: local masks ----
    /// <summary>Masks of the photo (MASK module masks and the tools' own masks).</summary>
    IReadOnlyList<AiMask> Masks();
    /// <summary>Maps a point of the photo as shown (after straighten and crop, 0..1) to the un-rotated, un-cropped photo (0..1).</summary>
    (double X, double Y) ViewToPhoto(double x, double y);
    /// <summary>Inverse of <see cref="ViewToPhoto"/>.</summary>
    (double X, double Y) PhotoToView(double x, double y);
    /// <summary>Pixel size of the un-rotated, un-cropped photo.</summary>
    (int W, int H) PhotoPixelSize();
    /// <summary>Creates an independent mask (kind "radial", "linear" or "luma", P in the .eye convention); returns its key.</summary>
    string CreateMask(string kind, double[] p, bool invert);
    /// <summary>Replaces the shape of an existing mask (the tools that use it follow).</summary>
    void UpdateMask(string key, string kind, double[] p, bool invert);
    /// <summary>Removes a mask; tools limited to it work on the whole photo again.</summary>
    void DeleteMask(string key);
    /// <summary>The mask's own STANDARD / DETAIL values (added to the whole-photo values inside it); empty removes them.</summary>
    void SetMaskValues(string key, IReadOnlyDictionary<string, double> values);
    /// <summary>Limits a filter to a mask (it follows the mask), or back to the whole photo when <paramref name="maskKey"/> is null.</summary>
    void SetToolMask(string filterId, string? maskKey);
}

/// <summary>One parameter of a plugin filter. Kind: slider, toggle, choice or color.</summary>
public sealed record AiParam(string Id, string Label, string Kind, double Min, double Max, double Default, double Value,
    string? Text = null, IReadOnlyList<string>? Choices = null);

/// <summary>A plugin filter. <see cref="Mask"/>: null (whole photo), a mask key it follows, or "own &lt;kind&gt;".</summary>
public sealed record AiFilter(string Id, string Label, string Group, string Description, bool Toolbar, bool SupportsMask,
    bool On, string? Mask, IReadOnlyList<AiParam> Params);

public sealed record AiPreset(string Id, string Name, string Group);

/// <summary>Straighten angle (degrees, positive clockwise), crop rectangle (fractions of the straightened photo), crop
/// aspect label, and the width/height ratio of the straightened photo before the crop.</summary>
public sealed record AiGeometry(double Angle, double CropX, double CropY, double CropW, double CropH, string Aspect, double FrameAspect);

/// <summary>A mask of the photo: P in the .eye convention (fractions of the un-rotated, un-cropped photo).</summary>
public sealed record AiMask(string Key, string Kind, double[] P, bool Invert, IReadOnlyDictionary<string, double>? Values, IReadOnlyList<string> UsedBy);

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

        // stage 2: plugins and presets
        tools.Add(Fn("list_plugins", "Plugin filters (effects such as B&W profiles, split toning, vignette, grain) with their parameters, ranges and current values, and the presets that can be applied.",
            new JsonObject()));
        tools.Add(Fn("set_plugin", "Set parameters of one plugin filter (absolute values; only the given ones change). Sliders are clamped to their range, toggles take 0/1, choices one of the listed ids, colours #RRGGBB. A panel filter is on while any parameter differs from its default.",
            new JsonObject { ["filter"] = Str("filter id from list_plugins"), ["values"] = Obj("{paramId: value}") }));
        tools.Add(Fn("set_plugin_active", "Switch a toolbar filter (toolbar: true in list_plugins) on or off.",
            new JsonObject { ["filter"] = Str("filter id"), ["active"] = new JsonObject { ["type"] = "boolean" } }));
        tools.Add(Fn("reset_plugin", "Reset a plugin filter to its defaults (turns its effect off).",
            new JsonObject { ["filter"] = Str("filter id") }));
        tools.Add(Fn("apply_preset", "Apply a preset from list_plugins: it sets the whole-photo values it contains (basic settings and filters); everything else stays.",
            new JsonObject { ["preset"] = Str("preset id from list_plugins") }));

        // stage 3: crop and straighten
        tools.Add(Fn("set_crop", "Crop the photo. Coordinates are fractions 0..1 of the whole straightened photo before cropping (get_state shows the current crop). " +
                                 "Give an aspect (\"original\", \"free\" or W:H such as \"3:2\", \"2:3\", \"1:1\", \"4:5\", \"16:9\") and optionally center_x, center_y and size (fraction of the largest crop of that aspect, 0.2..1, default 1); " +
                                 "or aspect \"free\" with x, y, w, h. reset: true removes the crop.",
            new JsonObject
            {
                ["aspect"] = Str("original, free or W:H"), ["center_x"] = Num("0..1"), ["center_y"] = Num("0..1"), ["size"] = Num("0.2..1"),
                ["x"] = Num("left 0..1"), ["y"] = Num("top 0..1"), ["w"] = Num("width 0..1"), ["h"] = Num("height 0..1"),
                ["reset"] = new JsonObject { ["type"] = "boolean" }
            }));
        tools.Add(Fn("straighten", "Level the photo: rotation in degrees (absolute, -45..45, positive turns it clockwise). The frame is cut in automatically so no empty corners show.",
            new JsonObject { ["angle"] = Num("degrees -45..45") }));

        // stage 4: local masks
        tools.Add(Fn("create_mask", "Create a local mask, or reshape an existing one (give mask). Coordinates are fractions 0..1 of the photo as it is shown now (x right, y down). " +
                                    "radial: center_x, center_y, radius_x (fraction of the shown width), radius_y (of the shown height), feather 0..1 (default 0.5). " +
                                    "linear: full effect at (x1,y1), fading to none at (x2,y2). luma: luminance range low..high (0..1) with feather. invert: the outside instead. " +
                                    "A new mask changes nothing until it gets values (set_mask_values) or a filter is limited to it (link_tool_to_mask).",
            new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("radial", "linear", "luma") },
                ["mask"] = Str("key of an existing mask to reshape (omit to create)"),
                ["center_x"] = Num("radial"), ["center_y"] = Num("radial"), ["radius_x"] = Num("radial"), ["radius_y"] = Num("radial"),
                ["x1"] = Num("linear"), ["y1"] = Num("linear"), ["x2"] = Num("linear"), ["y2"] = Num("linear"),
                ["low"] = Num("luma"), ["high"] = Num("luma"), ["feather"] = Num("0..1"),
                ["invert"] = new JsonObject { ["type"] = "boolean" }
            }));
        tools.Add(Fn("set_mask_values", "Basic settings inside a mask, added to the whole-photo values there (Exposure 0.5 = the masked area 0.5 EV brighter than the rest). " +
                                        "Keys: " + string.Join(", ", MaskableKeys) + ". Replaces the mask's earlier values; {} removes them.",
            new JsonObject { ["mask"] = Str("mask key"), ["values"] = Obj("{key: value}") }));
        tools.Add(Fn("link_tool_to_mask", "Limit a plugin filter to a mask (its effect then applies only inside it), or mask \"none\" for the whole photo again.",
            new JsonObject { ["filter"] = Str("filter id"), ["mask"] = Str("mask key or none") }));
        tools.Add(Fn("delete_mask", "Remove a mask: its own values go with it and filters limited to it work on the whole photo again.",
            new JsonObject { ["mask"] = Str("mask key") }));
        return tools;
    }

    /// <summary>Basic settings that can have values of their own inside a mask (the STANDARD / DETAIL sliders).</summary>
    public static readonly string[] MaskableKeys =
        { "Exposure", "Contrast", "Highlights", "Whites", "Blacks", "Temperature", "Tint", "Saturation", "Clarity", "Dehaze", "Sharpen", "Denoise", "ColorNoise" };

    private static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
    private static JsonObject Num(string d) => new() { ["type"] = "number", ["description"] = d };
    private static JsonObject Obj(string d) => new() { ["type"] = "object", ["description"] = d };

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
                    o["geometry"] = GeometryJson(ed.GetGeometry());
                    var on = new JsonArray();
                    foreach (var f in ed.Filters().Where(f => f.On))
                    {
                        var fo = new JsonObject { ["id"] = f.Id, ["label"] = f.Label };
                        if (f.Mask is not null) fo["mask"] = f.Mask;
                        on.Add(fo);
                    }
                    o["plugins_on"] = on;
                    o["masks"] = MasksJson(ed);
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
                case "list_plugins":
                    return new AiToolResult(PluginsJson(ed).ToJsonString());
                case "set_plugin":
                    return await SetPluginAsync(ed, args, ct);
                case "set_plugin_active":
                {
                    var f = FindFilter(ed, args, out var err);
                    if (f is null) return err!;
                    if (!f.Toolbar) return Fail($"'{f.Id}' is a panel filter: it is on while a parameter differs from its default; use set_plugin or reset_plugin");
                    bool active = args["active"] is not JsonNode an || !TryNumber(an, out double av) || av != 0;
                    if (f.On == active) return new AiToolResult(Ok(new JsonObject { ["filter"] = f.Id, ["on"] = active, ["note"] = "unchanged" }));
                    ed.SetFilterActive(f.Id, active);
                    return await AfterAsync(ed, ct, new JsonObject { ["filter"] = f.Id, ["on"] = active }, $"{f.Label} {(active ? "on" : "off")}");
                }
                case "reset_plugin":
                {
                    var f = FindFilter(ed, args, out var err);
                    if (f is null) return err!;
                    ed.ResetFilter(f.Id);
                    return await AfterAsync(ed, ct, new JsonObject { ["filter"] = f.Id, ["reset"] = true }, $"{f.Label} reset");
                }
                case "apply_preset":
                {
                    string id = args["preset"]?.ToString() ?? args["id"]?.ToString() ?? "";
                    var all = ed.Presets();
                    var p = all.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
                            ?? all.FirstOrDefault(x => string.Equals(x.Name, id, StringComparison.OrdinalIgnoreCase));
                    if (p is null) return Fail($"unknown preset '{id}'; call list_plugins");
                    ed.ApplyPreset(p.Id);
                    return await AfterAsync(ed, ct, new JsonObject { ["preset"] = p.Id, ["state"] = "applied; call get_state for the values" }, $"preset {p.Name}");
                }
                case "set_crop":
                    return await SetCropAsync(ed, args, ct);
                case "straighten":
                {
                    if (!TryNumber(args["angle"], out double a)) return Fail("angle (degrees) is required");
                    var g = ed.GetGeometry();
                    double before = g.Angle;
                    a = Math.Round(Math.Clamp(a, -45, 45), 2);
                    ed.SetGeometry(g with { Angle = a });
                    return await AfterAsync(ed, ct, new JsonObject { ["geometry"] = GeometryJson(ed.GetGeometry()) },
                        string.Format(CultureInfo.InvariantCulture, "straighten {0:0.0}° → {1:0.0}°", before, a));
                }
                case "create_mask":
                    return await CreateMaskAsync(ed, args, ct);
                case "set_mask_values":
                {
                    var m = FindMask(ed, args, out var err);
                    if (m is null) return err!;
                    var src = args["values"] as JsonObject ?? new JsonObject();
                    var apply = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    var ignored = new List<string>();
                    foreach (var kv in src)
                    {
                        var key = MaskableKeys.FirstOrDefault(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase));
                        var p = key is null ? null : CoreParams.Find(key);
                        if (p is null || !TryNumber(kv.Value, out double v)) { ignored.Add(kv.Key); continue; }
                        double span = p.Max - p.Min;
                        v = Math.Clamp(v, -span, span);
                        if (Math.Abs(v) > 1e-9) apply[p.Key] = v;
                    }
                    if (apply.Count == 0 && src.Count > 0)
                        return Fail("no valid values; keys: " + string.Join(", ", MaskableKeys));
                    ed.SetMaskValues(m.Key, apply);
                    var applied = new JsonObject();
                    foreach (var kv in apply) applied[kv.Key] = Math.Round(kv.Value, 3);
                    var res = new JsonObject { ["mask"] = m.Key, ["values"] = applied };
                    if (ignored.Count > 0) res["ignored"] = string.Join(", ", ignored);
                    return await AfterAsync(ed, ct, res, apply.Count == 0
                        ? $"{m.Key}: own values removed"
                        : $"{m.Key}: " + string.Join(", ", apply.Select(kv => $"{CoreParams.Find(kv.Key)!.Label} {kv.Value.ToString("+0.##;-0.##", CultureInfo.InvariantCulture)}")));
                }
                case "link_tool_to_mask":
                {
                    var f = FindFilter(ed, args, out var err);
                    if (f is null) return err!;
                    if (!f.SupportsMask) return Fail($"'{f.Id}' cannot be limited to a mask");
                    string mk = args["mask"]?.ToString()?.Trim() ?? "";
                    if (mk.Length == 0 || mk.Equals("none", StringComparison.OrdinalIgnoreCase) || mk.Equals("whole", StringComparison.OrdinalIgnoreCase))
                    {
                        ed.SetToolMask(f.Id, null);
                        return await AfterAsync(ed, ct, new JsonObject { ["filter"] = f.Id, ["mask"] = "none" }, $"{f.Label}: whole photo");
                    }
                    var m = ed.Masks().FirstOrDefault(x => string.Equals(x.Key, mk, StringComparison.OrdinalIgnoreCase));
                    if (m is null) return Fail($"unknown mask '{mk}'; masks: {string.Join(", ", ed.Masks().Select(x => x.Key))}");
                    if (string.Equals(m.Key, f.Id, StringComparison.OrdinalIgnoreCase)) return Fail("a filter cannot follow its own mask");
                    ed.SetToolMask(f.Id, m.Key);
                    return await AfterAsync(ed, ct, new JsonObject { ["filter"] = f.Id, ["mask"] = m.Key }, $"{f.Label} → {m.Key}");
                }
                case "delete_mask":
                {
                    var m = FindMask(ed, args, out var err);
                    if (m is null) return err!;
                    ed.DeleteMask(m.Key);
                    return await AfterAsync(ed, ct, new JsonObject { ["deleted"] = m.Key }, $"{m.Key} deleted");
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
    private static AiToolResult Fail(string message) => new(Err(message), Error: true);
    private static string Ok(JsonObject o) => o.ToJsonString();

    /// <summary>Waits for the render, adds the image statistics and marks the step as a change.</summary>
    private static async Task<AiToolResult> AfterAsync(IAiEditor ed, CancellationToken ct, JsonObject res, string note)
    {
        await ed.WaitRenderedAsync(ct);
        res["image_after"] = ed.Analyze()?.ToJson();
        return new AiToolResult(res.ToJsonString(), StepNote: note, Changed: true);
    }

    private static double R(double v) => Math.Round(v, 3);

    // ---------------- stage 2: plugins ----------------

    private static AiFilter? FindFilter(IAiEditor ed, JsonObject args, out AiToolResult? err)
    {
        string id = args["filter"]?.ToString()?.Trim() ?? args["id"]?.ToString()?.Trim() ?? "";
        var all = ed.Filters();
        var f = all.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
                // a bare filter id ("vignette") when it is unique
                ?? (all.Count(x => x.Id.EndsWith(":" + id, StringComparison.OrdinalIgnoreCase)) == 1
                    ? all.First(x => x.Id.EndsWith(":" + id, StringComparison.OrdinalIgnoreCase)) : null)
                ?? (all.Count(x => string.Equals(x.Label, id, StringComparison.OrdinalIgnoreCase)) == 1
                    ? all.First(x => string.Equals(x.Label, id, StringComparison.OrdinalIgnoreCase)) : null);
        err = f is null ? Fail($"unknown filter '{id}'; call list_plugins") : null;
        return f;
    }

    public static JsonObject PluginsJson(IAiEditor ed)
    {
        var filters = new JsonArray();
        foreach (var f in ed.Filters())
        {
            var ps = new JsonObject();
            foreach (var p in f.Params)
            {
                var po = new JsonObject { ["label"] = p.Label, ["kind"] = p.Kind };
                switch (p.Kind)
                {
                    case "slider":
                        po["min"] = R(p.Min); po["max"] = R(p.Max); po["default"] = R(p.Default); po["value"] = R(p.Value);
                        break;
                    case "toggle":
                        po["default"] = p.Default != 0; po["value"] = p.Value != 0;
                        break;
                    case "choice":
                        po["choices"] = new JsonArray((p.Choices ?? Array.Empty<string>()).Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
                        po["value"] = p.Text;
                        break;
                    case "color":
                        po["value"] = p.Text;
                        break;
                }
                ps[p.Id] = po;
            }
            var fo = new JsonObject { ["id"] = f.Id, ["label"] = f.Label, ["group"] = f.Group };
            if (!string.IsNullOrWhiteSpace(f.Description))
                fo["about"] = f.Description.Length > 120 ? f.Description[..117].TrimEnd() + "..." : f.Description;
            if (f.Toolbar) fo["toolbar"] = true;
            fo["on"] = f.On;
            if (!f.SupportsMask) fo["no_mask"] = true; // link_tool_to_mask refuses it
            if (f.Mask is not null) fo["mask"] = f.Mask;
            fo["params"] = ps;
            filters.Add(fo);
        }
        var presets = new JsonArray();
        foreach (var p in ed.Presets())
            presets.Add(new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["group"] = p.Group });
        return new JsonObject { ["filters"] = filters, ["presets"] = presets };
    }

    private static async Task<AiToolResult> SetPluginAsync(IAiEditor ed, JsonObject args, CancellationToken ct)
    {
        var f = FindFilter(ed, args, out var err);
        if (f is null) return err!;
        var src = args["values"] as JsonObject ?? args["params"] as JsonObject;
        if (src is null || src.Count == 0) return Fail("values {paramId: value} are required");
        var nums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var applied = new JsonObject();
        var note = new StringBuilder();
        foreach (var kv in src)
        {
            var p = f.Params.FirstOrDefault(x => string.Equals(x.Id, kv.Key, StringComparison.OrdinalIgnoreCase))
                    ?? f.Params.FirstOrDefault(x => string.Equals(x.Label, kv.Key, StringComparison.OrdinalIgnoreCase));
            if (p is null) { problems.Add($"{kv.Key}: unknown"); continue; }
            switch (p.Kind)
            {
                case "slider":
                {
                    if (!TryNumber(kv.Value, out double v)) { problems.Add($"{p.Id}: not a number"); continue; }
                    v = Math.Clamp(v, Math.Min(p.Min, p.Max), Math.Max(p.Min, p.Max));
                    nums[p.Id] = v; applied[p.Id] = R(v);
                    Append(note, $"{p.Label} {R(p.Value).ToString(CultureInfo.InvariantCulture)} → {R(v).ToString(CultureInfo.InvariantCulture)}");
                    break;
                }
                case "toggle":
                {
                    if (!TryNumber(kv.Value, out double v)) { problems.Add($"{p.Id}: use true/false"); continue; }
                    nums[p.Id] = v != 0 ? 1 : 0; applied[p.Id] = v != 0;
                    Append(note, $"{p.Label} {(v != 0 ? "on" : "off")}");
                    break;
                }
                case "choice":
                {
                    string s = kv.Value?.ToString() ?? "";
                    var c = p.Choices?.FirstOrDefault(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase));
                    if (c is null) { problems.Add($"{p.Id}: one of {string.Join(", ", p.Choices ?? Array.Empty<string>())}"); continue; }
                    text[p.Id] = c; applied[p.Id] = c;
                    Append(note, $"{p.Label} {c}");
                    break;
                }
                case "color":
                {
                    string s = (kv.Value?.ToString() ?? "").Trim();
                    if (!s.StartsWith('#')) s = "#" + s;
                    if (s.Length != 7 || !s.Skip(1).All(Uri.IsHexDigit)) { problems.Add($"{p.Id}: colour as #RRGGBB"); continue; }
                    s = s.ToUpperInvariant();
                    text[p.Id] = s; applied[p.Id] = s;
                    Append(note, $"{p.Label} {s}");
                    break;
                }
                default:
                    problems.Add($"{p.Id}: not settable by the assistant");
                    break;
            }
        }
        if (nums.Count == 0 && text.Count == 0)
            return Fail("nothing applied: " + string.Join("; ", problems));
        ed.SetFilter(f.Id, nums, text);
        var res = new JsonObject { ["filter"] = f.Id, ["applied"] = applied, ["on"] = ed.Filters().FirstOrDefault(x => x.Id == f.Id)?.On ?? false };
        if (problems.Count > 0) res["ignored"] = string.Join("; ", problems);
        return await AfterAsync(ed, ct, res, $"{f.Label}: {note}");
    }

    private static void Append(StringBuilder sb, string s)
    {
        if (sb.Length > 0) sb.Append(", ");
        sb.Append(s);
    }

    // ---------------- stage 3: geometry ----------------

    public static JsonObject GeometryJson(AiGeometry g) => new()
    {
        ["angle"] = Math.Round(g.Angle, 2),
        ["crop"] = new JsonObject { ["x"] = R(g.CropX), ["y"] = R(g.CropY), ["w"] = R(g.CropW), ["h"] = R(g.CropH) },
        ["aspect"] = g.Aspect,
        ["frame_aspect"] = R(g.FrameAspect)
    };

    /// <summary>"3:2" → 1.5; "original" → the frame's own aspect; "free" / unknown → null.</summary>
    public static double? ParseAspect(string s, double frameAspect)
    {
        s = s.Trim().ToLowerInvariant();
        if (s is "" or "original" or "orig") return frameAspect;
        if (s is "free" or "none") return null;
        var parts = s.Split(':', 'x', '/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            && a > 0 && b > 0)
            return a / b;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0) return r;
        return double.NaN;
    }

    /// <summary>The app's crop aspect label for a ratio (it knows landscape ratios only; others are stored as "original", which only locks later hand edits).</summary>
    public static string AspectLabel(double? ratio, double frameAspect)
    {
        if (ratio is not double r) return "original";
        foreach (var (label, v) in new[] { ("3:2", 1.5), ("4:3", 4.0 / 3.0), ("16:9", 16.0 / 9.0), ("16:10", 1.6) })
            if (Math.Abs(r - v) < 0.005) return label;
        return "original";
    }

    private static async Task<AiToolResult> SetCropAsync(IAiEditor ed, JsonObject args, CancellationToken ct)
    {
        var g = ed.GetGeometry();
        double fa = g.FrameAspect > 0 ? g.FrameAspect : 1.5;
        AiGeometry ng;
        string note;
        if (args["reset"] is JsonNode rn && TryNumber(rn, out double rv) && rv != 0)
        {
            ng = g with { CropX = 0, CropY = 0, CropW = 1, CropH = 1, Aspect = "original" };
            note = "crop removed";
        }
        else
        {
            string aspectText = args["aspect"]?.ToString() ?? (args["w"] is not null ? "free" : "original");
            double? ratio = ParseAspect(aspectText, fa);
            if (ratio is double bad && double.IsNaN(bad)) return Fail($"unknown aspect '{aspectText}' (original, free or W:H)");
            bool hasRect = TryNumber(args["x"], out double x) & TryNumber(args["y"], out double y)
                           & TryNumber(args["w"], out double w) & TryNumber(args["h"], out double h);
            double min = 0.05;
            if (hasRect && ratio is null)
            {
                x = Math.Clamp(x, 0, 1 - min); y = Math.Clamp(y, 0, 1 - min);
                w = Math.Clamp(w, min, 1 - x); h = Math.Clamp(h, min, 1 - y);
            }
            else
            {
                // largest rectangle of the ratio inside the frame (fractions of the frame), scaled and centred
                double r = ratio ?? fa;
                double fw = 1, fh = 1;
                if (r >= fa) fh = fa / r; else fw = r / fa;
                double size = TryNumber(args["size"], out double sz) ? Math.Clamp(sz, 0.2, 1) : 1;
                if (hasRect && ratio is not null)
                {
                    // a rectangle with a fixed ratio: keep its centre and the larger of the given sides
                    size = Math.Clamp(Math.Max(w / fw, h / fh), 0.2, 1);
                }
                w = fw * size; h = fh * size;
                double cx = TryNumber(args["center_x"], out double cxv) ? cxv : hasRect ? x + w / 2 : g.CropX + g.CropW / 2;
                double cy = TryNumber(args["center_y"], out double cyv) ? cyv : hasRect ? y + h / 2 : g.CropY + g.CropH / 2;
                x = Math.Clamp(cx - w / 2, 0, 1 - w);
                y = Math.Clamp(cy - h / 2, 0, 1 - h);
            }
            ng = g with { CropX = x, CropY = y, CropW = w, CropH = h, Aspect = AspectLabel(ratio, fa) };
            note = string.Format(CultureInfo.InvariantCulture, "crop {0} {1:0.##}×{2:0.##} at {3:0.##},{4:0.##}", ratio is null ? "free" : aspectText.Trim(), w, h, x, y);
        }
        ed.SetGeometry(ng);
        return await AfterAsync(ed, ct, new JsonObject { ["geometry"] = GeometryJson(ed.GetGeometry()) }, note);
    }

    // ---------------- stage 4: masks ----------------

    private static AiMask? FindMask(IAiEditor ed, JsonObject args, out AiToolResult? err)
    {
        string key = args["mask"]?.ToString()?.Trim() ?? "";
        var all = ed.Masks();
        var m = all.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
                // "1" or "mask 1" for "mask:1"
                ?? all.FirstOrDefault(x => string.Equals(x.Key, "mask:" + key.Replace("mask", "", StringComparison.OrdinalIgnoreCase).Trim(' ', ':'), StringComparison.OrdinalIgnoreCase));
        err = m is null ? Fail(all.Count == 0 ? "there are no masks; create one with create_mask" : $"unknown mask '{key}'; masks: {string.Join(", ", all.Select(x => x.Key))}") : null;
        return m;
    }

    /// <summary>Masks with their shapes in view coordinates (as the model gives them).</summary>
    public static JsonArray MasksJson(IAiEditor ed)
    {
        var arr = new JsonArray();
        foreach (var m in ed.Masks())
        {
            var o = new JsonObject { ["mask"] = m.Key, ["kind"] = m.Kind };
            var p = m.P;
            switch (m.Kind)
            {
                case "radial" when p.Length >= 5:
                {
                    var (vx, vy) = ed.PhotoToView(p[0], p[1]);
                    var (exx, exy) = ed.PhotoToView(p[0] + p[2], p[1]);
                    var (eyx, eyy) = ed.PhotoToView(p[0], p[1] + p[3]);
                    o["center_x"] = R(vx); o["center_y"] = R(vy);
                    // view-fraction radii (the axes swap after a quarter turn)
                    o["radius_x"] = R(Math.Max(Math.Abs(exx - vx), Math.Abs(eyx - vx)));
                    o["radius_y"] = R(Math.Max(Math.Abs(exy - vy), Math.Abs(eyy - vy)));
                    o["feather"] = R(p[4]);
                    break;
                }
                case "linear" when p.Length >= 4:
                {
                    var (x1, y1) = ed.PhotoToView(p[0], p[1]);
                    var (x2, y2) = ed.PhotoToView(p[2], p[3]);
                    o["x1"] = R(x1); o["y1"] = R(y1); o["x2"] = R(x2); o["y2"] = R(y2);
                    break;
                }
                case "luma" when p.Length >= 3:
                    o["low"] = R(p[0]); o["high"] = R(p[1]); o["feather"] = R(p[2]);
                    break;
            }
            if (m.Invert) o["invert"] = true;
            if (m.Values is { Count: > 0 } vals)
            {
                var vo = new JsonObject();
                foreach (var kv in vals) vo[kv.Key] = R(kv.Value);
                o["values"] = vo;
            }
            if (m.UsedBy.Count > 0) o["used_by"] = new JsonArray(m.UsedBy.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray());
            arr.Add(o);
        }
        return arr;
    }

    private static async Task<AiToolResult> CreateMaskAsync(IAiEditor ed, JsonObject args, CancellationToken ct)
    {
        AiMask? existing = null;
        if (args["mask"] is JsonNode mn && !string.IsNullOrWhiteSpace(mn.ToString()))
        {
            existing = FindMask(ed, args, out var err);
            if (existing is null) return err!;
        }
        string kind = (args["kind"]?.ToString() ?? existing?.Kind ?? "radial").Trim().ToLowerInvariant();
        bool invert = args["invert"] is JsonNode inv ? TryNumber(inv, out double iv) && iv != 0 : existing?.Invert ?? false;
        double Get(string k, double d) => TryNumber(args[k], out double v) ? v : d;
        double feather = Math.Clamp(Get("feather", 0.5), 0, 1);
        double[] p;
        var (pw, ph) = ed.PhotoPixelSize();
        pw = Math.Max(1, pw); ph = Math.Max(1, ph);
        switch (kind)
        {
            case "radial":
            {
                double cx = Math.Clamp(Get("center_x", 0.5), -0.5, 1.5), cy = Math.Clamp(Get("center_y", 0.5), -0.5, 1.5);
                double rx = Math.Clamp(Get("radius_x", Get("radius", 0.25)), 0.01, 2), ry = Math.Clamp(Get("radius_y", Get("radius", rx)), 0.01, 2);
                var c = ed.ViewToPhoto(cx, cy);
                var ex = ed.ViewToPhoto(cx + rx, cy);
                var ey = ed.ViewToPhoto(cx, cy + ry);
                // distances in source pixels; a quarter turn makes the view x run along the photo's y
                double exdx = (ex.X - c.X) * pw, exdy = (ex.Y - c.Y) * ph, eydx = (ey.X - c.X) * pw, eydy = (ey.Y - c.Y) * ph;
                double lx = Math.Sqrt(exdx * exdx + exdy * exdy), ly = Math.Sqrt(eydx * eydx + eydy * eydy);
                bool swapped = Math.Abs(exdy) > Math.Abs(exdx);
                double srcRx = (swapped ? ly : lx) / pw, srcRy = (swapped ? lx : ly) / ph;
                p = new[] { c.X, c.Y, Math.Clamp(srcRx, 0.005, 3), Math.Clamp(srcRy, 0.005, 3), feather };
                break;
            }
            case "linear":
            {
                var a = ed.ViewToPhoto(Get("x1", 0.5), Get("y1", 0.0));
                var b = ed.ViewToPhoto(Get("x2", 0.5), Get("y2", 0.5));
                if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 0.01) return Fail("x1,y1 and x2,y2 must be apart");
                p = new[] { a.X, a.Y, b.X, b.Y };
                break;
            }
            case "luma":
            {
                double lo = Math.Clamp(Get("low", 0.7), 0, 1), hi = Math.Clamp(Get("high", 1), 0, 1);
                if (hi < lo) (lo, hi) = (hi, lo);
                p = new[] { lo, hi, Math.Clamp(Get("feather", 0.1), 0, 1) };
                break;
            }
            default:
                return Fail($"unknown kind '{kind}' (radial, linear, luma)");
        }
        string key;
        if (existing is not null)
        {
            ed.UpdateMask(existing.Key, kind, p, invert);
            key = existing.Key;
        }
        else key = ed.CreateMask(kind, p, invert);
        var res = new JsonObject { ["mask"] = key, ["kind"] = kind };
        if (existing is null) res["note"] = "the mask changes nothing yet: give it values (set_mask_values) or limit a filter to it (link_tool_to_mask)";
        return await AfterAsync(ed, ct, res, $"{(existing is null ? "created" : "reshaped")} {key} ({kind}{(invert ? ", inverted" : "")})");
    }
}
