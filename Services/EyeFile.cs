using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ImgViewer.Services;

public sealed record EyeState
{
    public int Version { get; init; } = 5;
    public double BaseEv { get; init; }
    public double[][]? ToneCurve { get; init; }
    public double ShadowLift { get; init; }
    public double Gamma { get; init; } = 1.0;
    public double[][]? Stretch { get; init; }
    public double[][]? StretchEv { get; init; }
    /// <summary>Histogram R/G/B curves, [input, output] pairs in display space 0..1.</summary>
    public double[][]? CurveR { get; init; }
    public double[][]? CurveG { get; init; }
    public double[][]? CurveB { get; init; }
    public double[][]? LevelR { get; init; }
    public double[][]? LevelG { get; init; }
    public double[][]? LevelB { get; init; }
    public int Mono { get; init; }
    public double Exposure { get; init; }
    public double Contrast { get; init; }
    public double Temperature { get; init; }
    public double Tint { get; init; }
    public double Saturation { get; init; }
    public double Highlights { get; init; }
    public double Whites { get; init; }
    public double Blacks { get; init; }
    public double Clarity { get; init; }
    public double Sharpen { get; init; }
    public double Dehaze { get; init; }
    public double Denoise { get; init; }
    public double ColorNoise { get; init; }
    public int Rot90 { get; init; }
    public double Angle { get; init; }
    public double CropX { get; init; }
    public double CropY { get; init; }
    public double CropW { get; init; } = 1.0;
    public double CropH { get; init; } = 1.0;
    public string CropAspect { get; init; } = "original";
    public Dictionary<string, double>? PluginValues { get; init; }
    public List<string>? PluginActive { get; init; }
    public Dictionary<string, string>? PluginText { get; init; }
    public List<string>? PluginOrder { get; init; }
    public Dictionary<string, MaskSpec>? PluginMasks { get; init; }
    public List<EyeState>? History { get; init; }
    public int? HistoryPos { get; init; }
    /// <summary>The AI assistant conversation about this photo (the top-level state only; never in history steps).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Ai.AiSavedSession? Assistant { get; init; }
}

/// <summary>Local mask attached to a plugin filter. Coordinates are fractions of the un-rotated, un-cropped photo.</summary>
public sealed record MaskSpec
{
    /// <summary>"linear" (P = x1,y1,x2,y2), "radial" (P = cx,cy,rx,ry,feather[,rotation degrees]), "brush" (Strokes), "luma" (P = lo,hi,feather), "plugin:&lt;pluginId:maskId&gt;" or "link" (the mask of <see cref="Ref"/>).</summary>
    public string Kind { get; init; } = "";
    /// <summary>For a "link" mask: the key of the mask it uses (a filter FullId or "core:adjustments"); Invert is relative to it.</summary>
    public string? Ref { get; init; }
    public double[]? P { get; init; }
    public bool Invert { get; init; }
    /// <summary>Brush strokes: [mode (1 paint, 0 erase), radius (fraction of width), x1, y1, x2, y2, ...].</summary>
    public List<double[]>? Strokes { get; init; }
    public Dictionary<string, double>? Values { get; init; }
    public Dictionary<string, string>? Text { get; init; }
    /// <summary>
    /// Adjustments mask only: the panel slider values of the mask (CoreParam keys, slider units, defaults left out).
    /// They add to the whole-photo values inside the mask; outside it only the whole-photo values apply.
    /// </summary>
    public Dictionary<string, double>? Adjust { get; init; }
    /// <summary>
    /// Own values of plugin filters inside this mask ("pluginFullId:paramId" keys, slider units): a tool scoped to
    /// another mask keeps its values there, and inside this mask these replace them (a parameter not listed falls back
    /// to the tool's value). Set by the tool's sliders while this mask is the one active on the photo.
    /// </summary>
    public Dictionary<string, double>? Plugin { get; init; }
}

public static class EyeFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>Subfolder next to the photos that holds the .eye files of all photos of that folder.</summary>
    public const string FolderName = "_eye_";

    /// <summary>Where the edit file is written: &lt;photo folder&gt;\_eye_\&lt;file name with extension&gt;.eye (RAW + JPEG pairs stay apart).</summary>
    public static string SidecarPath(string imagePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imagePath)) ?? "", FolderName, Path.GetFileName(imagePath) + ".eye");

    /// <summary>Old location (before the _eye_ folder): &lt;photo name&gt;.eye next to the photo. Read only.</summary>
    public static string LegacySidecarPath(string imagePath) => Path.ChangeExtension(imagePath, ".eye");

    /// <summary>The file to read: the _eye_ one, else the old one next to the photo, else null.</summary>
    public static string? ExistingPath(string imagePath)
    {
        var p = SidecarPath(imagePath);
        if (File.Exists(p)) return p;
        var legacy = LegacySidecarPath(imagePath);
        return File.Exists(legacy) ? legacy : null;
    }

    public static bool Exists(string imagePath) => ExistingPath(imagePath) is not null;

    public static void Save(string imagePath, EyeState state) => WriteJson(imagePath, Serialize(state));

    public static string Serialize(EyeState state) => JsonSerializer.Serialize(state, JsonOptions);

    /// <summary>Write through a temp file so a crash or full disk never leaves a half-written fingerprint.</summary>
    public static void WriteJson(string imagePath, string json)
    {
        var dest = SidecarPath(imagePath);
        var tmp = dest + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(tmp, json);
            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    public static bool TryLoad(string imagePath, out EyeState state)
    {
        state = new EyeState();
        try
        {
            if (ExistingPath(imagePath) is not { } src) return false;
            var loaded = JsonSerializer.Deserialize<EyeState>(File.ReadAllText(src), JsonOptions);
            if (loaded is not { Version: 1 or 2 or 3 or 4 or 5 }) return false;
            state = loaded;
            return true;
        }
        catch (Exception)
        {
            state = new EyeState();
            return false;
        }
    }
}