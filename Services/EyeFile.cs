using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ImgViewer.Services;

public sealed record EyeState
{
    public int Version { get; init; } = 2;
    public double ShadowLift { get; init; }
    public double Gamma { get; init; } = 1.0;
    public double[][]? Stretch { get; init; }
    public double[][]? StretchEv { get; init; }
    public int Mono { get; init; }
    public double Exposure { get; init; }
    public double Contrast { get; init; }
    public double Temperature { get; init; }
    public double Saturation { get; init; }
    public double Highlights { get; init; }
    public double Whites { get; init; }
    public double Blacks { get; init; }
    public double Clarity { get; init; }
    public double Sharpen { get; init; }
    public double Dehaze { get; init; }
    public double Denoise { get; init; }
    public double ColorNoise { get; init; }
    public List<EyeState>? History { get; init; }
    public int? HistoryPos { get; init; }
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
            if (loaded is not { Version: 1 or 2 }) return false;
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