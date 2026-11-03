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
}

/// <summary>Local mask attached to a plugin filter. Coordinates are fractions of the un-rotated, un-cropped photo.</summary>
public sealed record MaskSpec
{
    /// <summary>"linear" (P = x1,y1,x2,y2), "radial" (P = cx,cy,rx,ry,feather), "brush" (Strokes), "luma" (P = lo,hi,feather) or "plugin:&lt;pluginId:maskId&gt;".</summary>
    public string Kind { get; init; } = "";
    public double[]? P { get; init; }
    public bool Invert { get; init; }
    /// <summary>Brush strokes: [mode (1 paint, 0 erase), radius (fraction of width), x1, y1, x2, y2, ...].</summary>
    public List<double[]>? Strokes { get; init; }
    public Dictionary<string, double>? Values { get; init; }
    public Dictionary<string, string>? Text { get; init; }
}

public static class EyeFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string SidecarPath(string imagePath) => Path.ChangeExtension(imagePath, ".eye");

    public static void Save(string imagePath, EyeState state)
    {
        File.WriteAllText(SidecarPath(imagePath), JsonSerializer.Serialize(state, JsonOptions));
    }

    public static bool TryLoad(string imagePath, out EyeState state)
    {
        state = new EyeState();
        try
        {
            var loaded = JsonSerializer.Deserialize<EyeState>(File.ReadAllText(SidecarPath(imagePath)), JsonOptions);
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