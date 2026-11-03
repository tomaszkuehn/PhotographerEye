using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImgViewer;

/// <summary>A preset: core keys "core.Exposure" ... and plugin keys "pluginId:filterId/paramId".</summary>
public sealed class UserPreset
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "My presets";
    public Dictionary<string, double> Values { get; set; } = new();
    public Dictionary<string, string> Text { get; set; } = new();
    public List<string> Active { get; set; } = new();
    [JsonIgnore] public string? FilePath { get; set; }
    /// <summary>Owner plugin for plugin presets (keys without ':' are relative to it).</summary>
    [JsonIgnore] public string? PluginId { get; set; }
}

public static class PresetStore
{
    public static string Dir => Path.Combine(PluginManager.AppDataDir, "Presets");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static List<UserPreset> LoadUser(string? dir = null)
    {
        dir ??= Dir;
        var list = new List<UserPreset>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var p = JsonSerializer.Deserialize<UserPreset>(File.ReadAllText(f), Opts);
                if (p is null) continue;
                p.Values ??= new();
                p.Text ??= new();
                p.Active ??= new();
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = Path.GetFileNameWithoutExtension(f);
                p.FilePath = f;
                list.Add(p);
            }
            catch (Exception ex)
            {
                Debug.Log($"PRESETS: skip {f} [{ex.GetType().Name}] {ex.Message}");
            }
        }
        return list;
    }

    public static string Save(UserPreset preset, string? dir = null)
    {
        dir ??= Dir;
        Directory.CreateDirectory(dir);
        var safe = string.Concat(preset.Name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safe.Length == 0) safe = "preset";
        var path = Path.Combine(dir, safe + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(preset, Opts));
        preset.FilePath = path;
        return path;
    }

    public static void Delete(UserPreset preset)
    {
        if (preset.FilePath is not null && File.Exists(preset.FilePath)) File.Delete(preset.FilePath);
    }

    /// <summary>Plugin presets from the registry, with keys resolved to full ids.</summary>
    public static List<UserPreset> FromRegistry(PluginRegistry reg)
    {
        var list = new List<UserPreset>();
        foreach (var r in reg.Presets)
        {
            var d = r.D;
            var p = new UserPreset { Name = d.Name, Group = string.IsNullOrWhiteSpace(d.Group) ? r.PluginName : d.Group, PluginId = r.PluginId };
            foreach (var kv in d.Values) p.Values[Resolve(kv.Key, r.PluginId)] = kv.Value;
            foreach (var kv in d.Text) p.Text[Resolve(kv.Key, r.PluginId)] = kv.Value;
            foreach (var a in d.Active) p.Active.Add(a.Contains(':') ? a : r.PluginId + ":" + a);
            list.Add(p);
        }
        return list;
    }

    private static string Resolve(string key, string pluginId) =>
        key.StartsWith("core.", StringComparison.Ordinal) || key.Contains(':') ? key : pluginId + ":" + key;
}
