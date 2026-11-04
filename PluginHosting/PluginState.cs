using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ImgViewer.Plugins;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>
/// Per-photo plugin edit state. Keys are namespaced: values/text "pluginId:filterId/paramId", active/order "pluginId:filterId",
/// masks by "pluginId:filterId". Data of plugins that are not loaded is kept untouched (never pruned) so a photo
/// round-trips through a machine without the plugin.
/// </summary>
public sealed class PluginEditState
{
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Active { get; } = new(StringComparer.Ordinal);
    /// <summary>User filter order (full ids). Filters not listed follow by their default order.</summary>
    public List<string> Order { get; } = new();
    public Dictionary<string, MaskSpec> Masks { get; } = new(StringComparer.Ordinal);

    public static string Key(string filterFullId, string paramId) => filterFullId + "/" + paramId;

    public static string PluginIdOf(string key)
    {
        int c = key.IndexOf(':');
        return c > 0 ? key.Substring(0, c) : "";
    }

    public bool IsEmpty => Values.Count == 0 && Text.Count == 0 && Active.Count == 0 && Order.Count == 0 && Masks.Count == 0;

    public double GetValue(Registered<FilterDescriptor> f, ParamDef d) =>
        Values.TryGetValue(Key(f.FullId, d.Id), out var v) ? v : d.Default;

    public string GetText(Registered<FilterDescriptor> f, ParamDef d) =>
        Text.TryGetValue(Key(f.FullId, d.Id), out var v) && v is not null ? v : d.DefaultText;

    public void SetValue(Registered<FilterDescriptor> f, ParamDef d, double v)
    {
        var k = Key(f.FullId, d.Id);
        if (Math.Abs(v - d.Default) < 1e-9) Values.Remove(k);
        else Values[k] = v;
    }

    public void SetText(Registered<FilterDescriptor> f, ParamDef d, string? v)
    {
        var k = Key(f.FullId, d.Id);
        if (v is null || v == d.DefaultText) Text.Remove(k);
        else Text[k] = v;
    }

    public FilterParams ParamsFor(Registered<FilterDescriptor> f)
    {
        var values = new Dictionary<string, double>();
        var text = new Dictionary<string, string>();
        foreach (var d in f.D.Params)
        {
            if (d.IsText) text[d.Id] = GetText(f, d);
            else values[d.Id] = GetValue(f, d);
        }
        return new FilterParams(f.D.Params, values, text);
    }

    /// <summary>True when the filter's parameters (or toggle) differ from defaults - ignoring IsIdentity.</summary>
    public bool IsFilterOn(Registered<FilterDescriptor> f)
    {
        if (f.D.Placement == FilterPlacement.Toolbar || f.D.Params.Count == 0)
            return Active.Contains(f.FullId);
        foreach (var d in f.D.Params)
        {
            if (d.IsText)
            {
                if (Text.TryGetValue(Key(f.FullId, d.Id), out var t) && t != d.DefaultText) return true;
            }
            else if (Values.TryGetValue(Key(f.FullId, d.Id), out var v) && Math.Abs(v - d.Default) > 1e-9) return true;
        }
        return false;
    }

    public bool IsFilterActive(Registered<FilterDescriptor> f)
    {
        if (f.Owner is { Faulted: true }) return false;
        if (!IsFilterOn(f)) return false;
        var id = f.D.IsIdentity;
        if (id is null) return true;
        try
        {
            return !id(ParamsFor(f));
        }
        catch (Exception ex)
        {
            PluginManager.Instance.ReportFailure(f.Owner, $"{f.FullId} IsIdentity", ex);
            return false;
        }
    }

    public bool HasEffect(PluginRegistry reg) => reg.Filters.Any(IsFilterActive);

    public bool HasEffect(PluginRegistry reg, FilterStage stage) => reg.Filters.Any(f => f.D.Stage == stage && IsFilterActive(f));

    public void ResetFilter(Registered<FilterDescriptor> f)
    {
        var prefix = f.FullId + "/";
        foreach (var k in Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) Values.Remove(k);
        foreach (var k in Text.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) Text.Remove(k);
        Active.Remove(f.FullId);
        Masks.Remove(f.FullId);
    }

    public MaskSpec? MaskFor(Registered<FilterDescriptor> f) =>
        f.D.SupportsMask && Masks.TryGetValue(f.FullId, out var m) && !string.IsNullOrEmpty(m.Kind) ? m : null;

    /// <summary>Filters of one stage in execution order.</summary>
    public List<Registered<FilterDescriptor>> Ordered(PluginRegistry reg, FilterStage stage)
    {
        var list = reg.Filters.Where(f => f.D.Stage == stage).ToList();
        return SortByOrder(list);
    }

    public List<Registered<FilterDescriptor>> SortByOrder(List<Registered<FilterDescriptor>> list)
    {
        var pos = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Order.Count; i++) pos.TryAdd(Order[i], i);
        var defaultIndex = list.Select((f, i) => (f, i)).ToDictionary(t => t.f.FullId, t => t.i);
        return list
            .OrderBy(f => pos.TryGetValue(f.FullId, out var p) ? 0 : 1)
            .ThenBy(f => pos.TryGetValue(f.FullId, out var p) ? p : 0)
            .ThenBy(f => f.D.Order)
            .ThenBy(f => defaultIndex[f.FullId])
            .ToList();
    }

    /// <summary>Move a filter up (-1) or down (+1) inside its stage; stores the full order of that stage.</summary>
    public bool Move(PluginRegistry reg, Registered<FilterDescriptor> f, int delta)
    {
        var stage = Ordered(reg, f.D.Stage);
        int i = stage.FindIndex(x => x.FullId == f.FullId);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= stage.Count) return false;
        (stage[i], stage[j]) = (stage[j], stage[i]);
        // Ids of other stages and of plugins that are not loaded stay; this stage is rewritten in full.
        var stageIds = new HashSet<string>(stage.Select(x => x.FullId), StringComparer.Ordinal);
        var rest = Order.Where(id => !stageIds.Contains(id)).ToList();
        Order.Clear();
        Order.AddRange(rest);
        Order.AddRange(stage.Select(x => x.FullId));
        return true;
    }

    /// <summary>
    /// Text identifying everything the active filters of <paramref name="stage"/> render from (order, parameters,
    /// masks): equal keys give equal output for the same input buffer.
    /// </summary>
    public string StageKey(PluginRegistry reg, FilterStage stage)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in Ordered(reg, stage))
        {
            if (!IsFilterActive(f)) continue;
            sb.Append(f.FullId).Append('{');
            foreach (var d in f.D.Params)
            {
                sb.Append(d.Id).Append('=');
                if (d.IsText) sb.Append(GetText(f, d).Replace(";", ";;"));
                else sb.Append(GetValue(f, d).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(';');
            }
            if (MaskFor(f) is { } m) sb.Append("mask=").Append(JsonSerializer.Serialize(m));
            sb.Append('}');
        }
        return sb.ToString();
    }

    public void Clear()
    {
        Values.Clear();
        Text.Clear();
        Active.Clear();
        Order.Clear();
        Masks.Clear();
    }

    public PluginEditState Clone()
    {
        var c = new PluginEditState();
        c.CopyFrom(this);
        return c;
    }

    public void CopyFrom(PluginEditState o)
    {
        Clear();
        foreach (var kv in o.Values) Values[kv.Key] = kv.Value;
        foreach (var kv in o.Text) Text[kv.Key] = kv.Value;
        foreach (var a in o.Active) Active.Add(a);
        Order.AddRange(o.Order);
        foreach (var kv in o.Masks) Masks[kv.Key] = CloneMask(kv.Value);
    }

    public static MaskSpec CloneMask(MaskSpec m) => m with
    {
        P = m.P is null ? null : (double[])m.P.Clone(),
        Strokes = m.Strokes?.Select(s => (double[])s.Clone()).ToList(),
        Values = m.Values is null ? null : new Dictionary<string, double>(m.Values),
        Text = m.Text is null ? null : new Dictionary<string, string>(m.Text)
    };

    public bool ContentEquals(PluginEditState o)
    {
        if (Values.Count != o.Values.Count || Text.Count != o.Text.Count || Active.Count != o.Active.Count ||
            Masks.Count != o.Masks.Count || !Order.SequenceEqual(o.Order)) return false;
        foreach (var kv in Values)
            if (!o.Values.TryGetValue(kv.Key, out var v) || Math.Abs(v - kv.Value) > 1e-9) return false;
        foreach (var kv in Text)
            if (!o.Text.TryGetValue(kv.Key, out var t) || t != kv.Value) return false;
        if (!Active.SetEquals(o.Active)) return false;
        foreach (var kv in Masks)
            if (!o.Masks.TryGetValue(kv.Key, out var m) || MaskJson(m) != MaskJson(kv.Value)) return false;
        return true;
    }

    private static string MaskJson(MaskSpec m) => JsonSerializer.Serialize(m);

    /// <summary>Plugin ids referenced by this state that are not loaded.</summary>
    public IReadOnlyList<string> MissingPlugins(PluginRegistry reg)
    {
        var ids = Values.Keys.Concat(Text.Keys).Concat(Active).Concat(Masks.Keys)
            .Select(PluginIdOf)
            .Where(id => id.Length > 0)
            .Distinct()
            .Where(id => !reg.HasPlugin(id))
            .ToList();
        return ids;
    }

    // ------------------------------------------------------------------
    //  .eye persistence
    // ------------------------------------------------------------------

    public void Load(EyeState? s, PluginRegistry reg)
    {
        Clear();
        if (s is null) return;
        if (s.PluginValues is not null)
            foreach (var kv in s.PluginValues)
            {
                var k = MigrateValueKey(kv.Key, reg);
                Values[k] = kv.Value;
            }
        if (s.PluginText is not null)
            foreach (var kv in s.PluginText)
                if (kv.Value is not null) Text[kv.Key] = kv.Value;
        if (s.PluginActive is not null)
            foreach (var a in s.PluginActive)
                Active.Add(MigrateFilterKey(a, reg));
        if (s.PluginOrder is not null) Order.AddRange(s.PluginOrder.Distinct());
        if (s.PluginMasks is not null)
            foreach (var kv in s.PluginMasks)
                if (kv.Value is not null) Masks[kv.Key] = CloneMask(kv.Value);
    }

    /// <summary>v1 (.eye v4) keys were "featureId" (one parameter) or "featureId/index".</summary>
    private static string MigrateValueKey(string key, PluginRegistry reg)
    {
        if (key.Contains(':')) return key;
        int slash = key.IndexOf('/');
        string fid = slash < 0 ? key : key.Substring(0, slash);
        int index = 0;
        if (slash >= 0 && !int.TryParse(key.Substring(slash + 1), out index)) return key;
        var f = reg.Filters.FirstOrDefault(x => x.LegacyId == fid);
        return f is null ? key : Key(f.FullId, "p" + index);
    }

    private static string MigrateFilterKey(string key, PluginRegistry reg)
    {
        if (key.Contains(':')) return key;
        var f = reg.Filters.FirstOrDefault(x => x.LegacyId == key);
        return f?.FullId ?? key;
    }

    public (Dictionary<string, double>? Values, List<string>? Active, Dictionary<string, string>? Text, List<string>? Order, Dictionary<string, MaskSpec>? Masks) Save()
    {
        return (
            Values.Count > 0 ? new Dictionary<string, double>(Values) : null,
            Active.Count > 0 ? Active.OrderBy(a => a, StringComparer.Ordinal).ToList() : null,
            Text.Count > 0 ? new Dictionary<string, string>(Text) : null,
            Order.Count > 0 ? new List<string>(Order) : null,
            Masks.Count > 0 ? Masks.ToDictionary(kv => kv.Key, kv => CloneMask(kv.Value)) : null);
    }

    // ------------------------------------------------------------------
    //  Presets
    // ------------------------------------------------------------------

    /// <summary>Resolve "filterId/param" (relative to <paramref name="pluginId"/>) or "pluginId:filterId/param".</summary>
    public static string ResolveKey(string key, string? pluginId)
    {
        if (key.Contains(':') || string.IsNullOrEmpty(pluginId)) return key;
        return pluginId + ":" + key;
    }
}
