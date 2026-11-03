using System;
using System.Collections.Generic;
using System.Linq;

namespace ImgViewer.Services;

/// <summary>Readable names of edit history steps, built from <see cref="HistoryCoalesce.ChangeKey"/>.</summary>
public static class HistoryLabels
{
    public const string Opened = "Opened";

    private static readonly Dictionary<string, string> Core = new(StringComparer.Ordinal)
    {
        ["BaseEv"] = "Base exposure",
        ["ToneCurve"] = "Tone curve",
        ["ShadowLift"] = "Shadow lift",
        ["Gamma"] = "Gamma",
        ["Stretch"] = "Histogram",
        ["StretchEv"] = "Histogram",
        ["CurveR"] = "Red curve",
        ["CurveG"] = "Green curve",
        ["CurveB"] = "Blue curve",
        ["LevelR"] = "Red levels",
        ["LevelG"] = "Green levels",
        ["LevelB"] = "Blue levels",
        ["Mono"] = "Black & white",
        ["ColorNoise"] = "Color noise",
        ["Rot90"] = "Rotate",
        ["Angle"] = "Straighten",
        ["CropX"] = "Crop",
        ["CropY"] = "Crop",
        ["CropW"] = "Crop",
        ["CropH"] = "Crop",
        ["CropAspect"] = "Crop",
        ["PluginActive"] = "Filter on/off",
        ["PluginOrder"] = "Filter order",
    };

    /// <summary>
    /// Label of the step from <paramref name="prev"/> to <paramref name="cur"/>. <paramref name="filterName"/> maps
    /// "pluginId:filterId" and an optional parameter id to a display name (null: use the ids).
    /// </summary>
    public static string Label(EyeState prev, EyeState cur, Func<string, string?, string?>? filterName = null) =>
        FromKey(HistoryCoalesce.ChangeKey(prev, cur), filterName);

    public static string FromKey(string key, Func<string, string?, string?>? filterName = null)
    {
        var names = new List<string>();
        foreach (var raw in key.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var n = Part(raw.Trim(), filterName);
            if (n.Length > 0 && !names.Contains(n)) names.Add(n);
        }
        if (names.Count == 0) return "Edit";
        if (names.Count <= 3) return string.Join(", ", names);
        return string.Join(", ", names.Take(2)) + $" +{names.Count - 2}";
    }

    private static string Part(string k, Func<string, string?, string?>? filterName)
    {
        int br = k.IndexOf('[');
        if (br < 0) return Core.TryGetValue(k, out var c) ? c : k;

        string prop = k[..br];
        int end = k.IndexOf(']', br);
        string inner = end > br ? k[(br + 1)..end] : k[(br + 1)..];
        bool strokes = end > 0 && k.IndexOf("#strokes", end, StringComparison.Ordinal) > 0;
        // inner = "pluginId:filterId/paramId" (values, text) or "pluginId:filterId" (masks).
        string filter = inner, param = "";
        int slash = inner.IndexOf('/');
        if (slash >= 0)
        {
            filter = inner[..slash];
            param = inner[(slash + 1)..];
        }
        string fName = filterName?.Invoke(filter, null) ?? LocalId(filter);
        switch (prop)
        {
            case "PluginMasks":
                return fName + (strokes ? " brush" : " mask");
            case "PluginValues":
            case "PluginText":
                if (param.Length == 0) return fName;
                string pName = filterName?.Invoke(filter, param) ?? param;
                return $"{fName}: {pName}";
            default:
                return fName;
        }
    }

    private static string LocalId(string fullId)
    {
        int c = fullId.IndexOf(':');
        return c >= 0 ? fullId[(c + 1)..] : fullId;
    }
}
