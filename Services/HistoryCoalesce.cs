using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace ImgViewer.Services;

/// <summary>
/// Undo history: consecutive edits of the same control (same changed fields, or the same plugin parameter) collapse
/// into one step that holds only the last value. Returning to the value before that step removes the step.
/// </summary>
public static class HistoryCoalesce
{
    private static readonly PropertyInfo[] Props = typeof(EyeState)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.Name is not (nameof(EyeState.History) or nameof(EyeState.HistoryPos)))
        .ToArray();

    /// <summary>
    /// Names of what differs between two states, e.g. "Exposure" or "PluginValues[x:amount]". Empty when equal.
    /// </summary>
    public static string ChangeKey(EyeState a, EyeState b)
    {
        var keys = new List<string>();
        foreach (var p in Props)
        {
            object? va = p.GetValue(a), vb = p.GetValue(b);
            if (va is IDictionary || vb is IDictionary)
            {
                var da = va as IDictionary;
                var db = vb as IDictionary;
                var all = new SortedSet<string>(StringComparer.Ordinal);
                if (da is not null) foreach (var k in da.Keys) all.Add(k.ToString()!);
                if (db is not null) foreach (var k in db.Keys) all.Add(k.ToString()!);
                foreach (var k in all)
                {
                    object? ea = da is not null && da.Contains(k) ? da[k] : null;
                    object? eb = db is not null && db.Contains(k) ? db[k] : null;
                    if (Json(ea) == Json(eb)) continue;
                    // Each brush stroke is its own step: the stroke count makes the key unique.
                    int sa = (ea as MaskSpec)?.Strokes?.Count ?? 0, sb = (eb as MaskSpec)?.Strokes?.Count ?? 0;
                    keys.Add(sa != sb ? $"{p.Name}[{k}]#strokes{sb}" : $"{p.Name}[{k}]");
                }
            }
            else if (Json(va) != Json(vb))
            {
                keys.Add(p.Name);
            }
        }
        return string.Join(",", keys);
    }

    private static string Json(object? v) => v is null ? "null" : JsonSerializer.Serialize(v, v.GetType());

    /// <summary>
    /// Records <paramref name="cur"/> in <paramref name="hist"/>. When it changes the same thing as the last recorded
    /// step (<paramref name="lastKey"/>) and that step is the newest one, the step is replaced instead of a new one
    /// being added. Returns the key of the newest step for the next call (null: the next edit starts a new step).
    /// </summary>
    public static string? Commit(List<EyeState> hist, ref int pos, EyeState cur, string? lastKey, int max,
        Func<EyeState, EyeState, bool> equal)
    {
        if (pos < 0 || pos >= hist.Count)
        {
            hist.Add(cur);
            pos = hist.Count - 1;
            return null;
        }
        if (equal(hist[pos], cur)) return lastKey;
        string key = ChangeKey(hist[pos], cur);
        if (lastKey is not null && key.Length > 0 && key == lastKey && pos == hist.Count - 1 && pos > 0)
        {
            if (equal(hist[pos - 1], cur))
            {
                hist.RemoveAt(pos);
                pos--;
                return null;
            }
            hist[pos] = cur;
            return key;
        }
        if (pos < hist.Count - 1) hist.RemoveRange(pos + 1, hist.Count - 1 - pos);
        hist.Add(cur);
        if (hist.Count > max) hist.RemoveAt(0);
        pos = hist.Count - 1;
        return key;
    }
}
