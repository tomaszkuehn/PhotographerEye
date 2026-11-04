using System;
using System.Collections.Generic;
using System.Linq;

namespace ImgViewer.Services;

/// <summary>
/// Turns the difference between two edit states into the keys plugins see in EditChanged:
/// "core.Exposure", "core.Crop", "core.Stretch", ... for the basic settings, "pluginId:filterId/paramId" for filter
/// parameters, "pluginId:filterId#active" / "#mask" and "#order".
/// </summary>
public static class EditChangeKeys
{
    public static List<string> Map(EyeState a, EyeState b, out List<(string Id, bool On)> toggled)
    {
        toggled = new List<(string, bool)>();
        var keys = new List<string>();
        var raw = HistoryCoalesce.ChangeKey(a, b);
        if (raw.Length == 0) return keys;
        foreach (var k in Split(raw))
        {
            if (k == nameof(EyeState.Version)) continue;
            if (k == nameof(EyeState.PluginActive))
            {
                var sa = new HashSet<string>(a.PluginActive ?? new List<string>(), StringComparer.Ordinal);
                var sb = new HashSet<string>(b.PluginActive ?? new List<string>(), StringComparer.Ordinal);
                foreach (var id in sb.Except(sa).OrderBy(x => x, StringComparer.Ordinal)) { toggled.Add((id, true)); keys.Add(id + "#active"); }
                foreach (var id in sa.Except(sb).OrderBy(x => x, StringComparer.Ordinal)) { toggled.Add((id, false)); keys.Add(id + "#active"); }
                continue;
            }
            if (k == nameof(EyeState.PluginOrder)) { keys.Add("#order"); continue; }
            if (Inner(k, nameof(EyeState.PluginValues)) is { } v) { keys.Add(v); continue; }
            if (Inner(k, nameof(EyeState.PluginText)) is { } t) { keys.Add(t); continue; }
            if (Inner(k, nameof(EyeState.PluginMasks)) is { } m) { keys.Add(m + "#mask"); continue; }
            keys.Add("core." + CoreParams.ChangeKey(k));
        }
        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Splits ChangeKey output on the commas between keys (not inside brackets).</summary>
    private static IEnumerable<string> Split(string raw)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '[') depth++;
            else if (raw[i] == ']') depth = Math.Max(0, depth - 1);
            else if (raw[i] == ',' && depth == 0)
            {
                if (i > start) yield return raw[start..i];
                start = i + 1;
            }
        }
        if (raw.Length > start) yield return raw[start..];
    }

    /// <summary>"Prop[key]" or "Prop[key]#strokesN" → key; null for other properties.</summary>
    private static string? Inner(string k, string prop)
    {
        if (!k.StartsWith(prop + "[", StringComparison.Ordinal)) return null;
        int end = k.LastIndexOf(']');
        return end > prop.Length + 1 ? k.Substring(prop.Length + 1, end - prop.Length - 1) : null;
    }
}
