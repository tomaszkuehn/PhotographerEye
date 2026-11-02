using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ImgViewer.Services;

public sealed record ViewerSettings
{
    public double ShadowLift { get; init; }
    public double Gamma { get; init; } = 1.0;
    public Dictionary<string, bool>? PluginFiles { get; init; }
    /// <summary>Why a plugin was switched off automatically (key = plugin id).</summary>
    public Dictionary<string, string>? PluginDisabledReason { get; init; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>%APPDATA%\PhotographerEye, or PHOTOGRAPHEREYE_APPDATA when set (tests).</summary>
    public static string DirectoryPath =>
        Environment.GetEnvironmentVariable("PHOTOGRAPHEREYE_APPDATA") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotographerEye");

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static ViewerSettings Load()
    {
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<ViewerSettings>(json, JsonOptions) ?? new ViewerSettings();
        }
        catch (Exception)
        {
            return new ViewerSettings();
        }
    }

    public static void Save(ViewerSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception)
        {
        }
    }
}