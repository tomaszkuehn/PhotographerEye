using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ImgViewer.Services;

/// <summary>
/// One basic adjustment: plugin key ("Exposure", "Shadows", ...), the <see cref="EyeState"/> field, the history label,
/// the slider range and default, and the controls of the main window that show it.
/// </summary>
public sealed class CoreParam
{
    /// <summary>Key of the plugin API (GetCore / SetCore), presets ("core.&lt;Key&gt;") and EditChanged ("core.&lt;Key&gt;").</summary>
    public string Key { get; init; } = "";
    /// <summary>Property of <see cref="EyeState"/> holding the value.</summary>
    public string Field { get; init; } = "";
    /// <summary>Name of an undo step that changes only this setting.</summary>
    public string Label { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; }
    public double Default { get; init; }
    /// <summary>x:Name of the slider holding the edit value (null: no slider, e.g. Mono).</summary>
    public string? Slider { get; init; }
    /// <summary>x:Name of the adjustments-panel slider when it is not <see cref="Slider"/> (Shadows: panel mirror of the toolbar slider).</summary>
    public string? PanelSlider { get; init; }
    /// <summary>x:Name of the panel TextBlock showing the value.</summary>
    public string? ValueText { get; init; }
    public int Decimals { get; init; }
    /// <summary>Show "+" for positive values.</summary>
    public bool Signed { get; init; }
    public bool Integer { get; init; }
    public Func<EyeState, double> Get { get; init; } = _ => 0;
    public Func<EyeState, double, EyeState> Set { get; init; } = (s, _) => s;

    /// <summary>Slider in the adjustments panel (double-click reset, value text), null when none.</summary>
    public string? Panel => PanelSlider ?? (ValueText is null ? null : Slider);

    public double Clamp(double v) => Math.Clamp(Integer ? Math.Round(v) : v, Min, Max);

    public string Format(double v)
    {
        var r = Math.Round(v, Decimals);
        if (r == 0) r = 0; // no "-0"
        var text = r.ToString(Decimals > 0 ? "0." + new string('0', Decimals) : "0", CultureInfo.InvariantCulture);
        return Signed && r > 0 ? "+" + text : text;
    }
}

/// <summary>
/// The basic adjustments and the other edit-state fields in one table. Everything that lists them (plugin
/// GetCore / SetCore / GetCoreRange, presets, the panel sliders, undo labels, EditChanged keys, undo coalescing)
/// reads it, so a new slider is one entry here plus its slider in MainWindow.xaml and its use in the renderer.
/// </summary>
public static class CoreParams
{
    public static IReadOnlyList<CoreParam> All { get; } = new[]
    {
        P("Exposure", nameof(EyeState.Exposure), "Exposure", -5, 5, 0, "ExpSlider", "ExpValue", (s, v) => s with { Exposure = v }, s => s.Exposure, decimals: 2, signed: true),
        P("Contrast", nameof(EyeState.Contrast), "Contrast", -100, 100, 0, "ContrastSlider", "ContrastValue", (s, v) => s with { Contrast = v }, s => s.Contrast),
        P("Temperature", nameof(EyeState.Temperature), "Temperature", -100, 100, 0, "TempSlider", "TempValue", (s, v) => s with { Temperature = v }, s => s.Temperature),
        P("Tint", nameof(EyeState.Tint), "Tint", -100, 100, 0, "TintSlider", "TintValue", (s, v) => s with { Tint = v }, s => s.Tint),
        P("Saturation", nameof(EyeState.Saturation), "Saturation", -100, 100, 0, "SatSlider", "SatValue", (s, v) => s with { Saturation = v }, s => s.Saturation),
        P("Highlights", nameof(EyeState.Highlights), "Highlights", -100, 100, 0, "HighlightsSlider", "HighlightsValue", (s, v) => s with { Highlights = v }, s => s.Highlights),
        new CoreParam
        {
            // The toolbar "Shadow lift" slider holds the value; the panel "Shadows" slider mirrors it.
            Key = "Shadows", Field = nameof(EyeState.ShadowLift), Label = "Shadow lift", Min = -100, Max = 100,
            Slider = "ShadowSlider", PanelSlider = "PanelShadowSlider", ValueText = "PanelShadowValue",
            Get = s => s.ShadowLift, Set = (s, v) => s with { ShadowLift = v }
        },
        P("Whites", nameof(EyeState.Whites), "Whites", -100, 100, 0, "WhitesSlider", "WhitesValue", (s, v) => s with { Whites = v }, s => s.Whites),
        P("Blacks", nameof(EyeState.Blacks), "Blacks", -100, 100, 0, "BlacksSlider", "BlacksValue", (s, v) => s with { Blacks = v }, s => s.Blacks),
        P("Clarity", nameof(EyeState.Clarity), "Clarity", -100, 100, 0, "ClaritySlider", "ClarityValue", (s, v) => s with { Clarity = v }, s => s.Clarity),
        P("Dehaze", nameof(EyeState.Dehaze), "Dehaze", -100, 100, 0, "DehazeSlider", "DehazeValue", (s, v) => s with { Dehaze = v }, s => s.Dehaze),
        P("Sharpen", nameof(EyeState.Sharpen), "Sharpen", 0, 150, 0, "SharpenSlider", "SharpenValue", (s, v) => s with { Sharpen = v }, s => s.Sharpen),
        P("Denoise", nameof(EyeState.Denoise), "Denoise", 0, 100, 0, "DenoiseSlider", "DenoiseValue", (s, v) => s with { Denoise = v }, s => s.Denoise),
        P("ColorNoise", nameof(EyeState.ColorNoise), "Color noise", 0, 100, 0, "ColorNoiseSlider", "ColorNoiseValue", (s, v) => s with { ColorNoise = v }, s => s.ColorNoise),
        new CoreParam
        {
            Key = "Gamma", Field = nameof(EyeState.Gamma), Label = "Gamma", Min = 0.5, Max = 2, Default = 1, Slider = "GammaSlider", Decimals = 2,
            Get = s => s.Gamma, Set = (s, v) => s with { Gamma = v }
        },
        new CoreParam
        {
            // 0 colour, 1 documentary, 2 Tri-X (MonoMode); a toggle button, not a slider.
            Key = "Mono", Field = nameof(EyeState.Mono), Label = "Black & white", Min = 0, Max = 2, Integer = true,
            Get = s => s.Mono, Set = (s, v) => s with { Mono = (int)Math.Round(v) }
        },
    };

    /// <summary>Edit-state fields that are not single values: undo label, EditChanged key, coalescing group.</summary>
    private static readonly (string Field, string Label, string Key, string Group)[] Other =
    {
        (nameof(EyeState.BaseEv), "Base exposure", "BaseEv", "BaseEv"),
        (nameof(EyeState.ToneCurve), "Tone curve", "ToneCurve", "ToneCurve"),
        (nameof(EyeState.Stretch), "Histogram", "Stretch", "Stretch"),
        (nameof(EyeState.StretchEv), "Histogram", "Stretch", "Stretch"),
        (nameof(EyeState.CurveR), "Red curve", "CurveR", "CurveR"),
        (nameof(EyeState.CurveG), "Green curve", "CurveG", "CurveG"),
        (nameof(EyeState.CurveB), "Blue curve", "CurveB", "CurveB"),
        (nameof(EyeState.LevelR), "Red levels", "LevelR", "LevelR"),
        (nameof(EyeState.LevelG), "Green levels", "LevelG", "LevelG"),
        (nameof(EyeState.LevelB), "Blue levels", "LevelB", "LevelB"),
        (nameof(EyeState.Rot90), "Rotate", "Rot90", "Rot90"),
        (nameof(EyeState.Angle), "Straighten", "Angle", "Angle"),
        // The crop rectangle and its aspect are one control: one key, one coalescing group.
        (nameof(EyeState.CropX), "Crop", "Crop", "Crop"),
        (nameof(EyeState.CropY), "Crop", "Crop", "Crop"),
        (nameof(EyeState.CropW), "Crop", "Crop", "Crop"),
        (nameof(EyeState.CropH), "Crop", "Crop", "Crop"),
        (nameof(EyeState.CropAspect), "Crop", "Crop", "Crop"),
        (nameof(EyeState.PluginActive), "Filter on/off", "PluginActive", "PluginActive"),
        (nameof(EyeState.PluginOrder), "Filter order", "PluginOrder", "PluginOrder"),
    };

    private static readonly Dictionary<string, (string Label, string Key, string Group)> ByField = BuildFields();
    private static readonly Dictionary<string, CoreParam> ByKey = All.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Keys { get; } = All.Select(p => p.Key).ToArray();

    private static CoreParam P(string key, string field, string label, double min, double max, double def, string slider, string valueText,
        Func<EyeState, double, EyeState> set, Func<EyeState, double> get, int decimals = 0, bool signed = false) => new()
    {
        Key = key, Field = field, Label = label, Min = min, Max = max, Default = def, Slider = slider, ValueText = valueText,
        Decimals = decimals, Signed = signed, Get = get, Set = set
    };

    private static Dictionary<string, (string, string, string)> BuildFields()
    {
        var d = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        foreach (var p in All) d[p.Field] = (p.Label, p.Key, p.Field);
        foreach (var o in Other) d[o.Field] = (o.Label, o.Key, o.Group);
        return d;
    }

    /// <summary>The setting for "exposure", "core.Exposure", "ShadowLift" (field name), ...; null when unknown.</summary>
    public static CoreParam? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim();
        if (k.StartsWith("core.", StringComparison.OrdinalIgnoreCase)) k = k[5..];
        return ByKey.TryGetValue(k, out var p) ? p : All.FirstOrDefault(x => x.Field.Equals(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Undo label of an <see cref="EyeState"/> field (the field name when not listed).</summary>
    public static string FieldLabel(string field) => ByField.TryGetValue(field, out var f) ? f.Label : field;

    /// <summary>EditChanged key (without "core.") of an <see cref="EyeState"/> field: "Crop", "Stretch", "Shadows", ...</summary>
    public static string ChangeKey(string field) => ByField.TryGetValue(field, out var f) ? f.Key : field;

    /// <summary>Undo coalescing group of an <see cref="EyeState"/> field: consecutive edits in one group merge.</summary>
    public static string Group(string field) => ByField.TryGetValue(field, out var f) ? f.Group : field;

    /// <summary>All single-value settings equal within <paramref name="eps"/>.</summary>
    public static bool ValuesEqual(EyeState a, EyeState b, double eps = 1e-9) =>
        All.All(p => Math.Abs(p.Get(a) - p.Get(b)) <= eps);
}
