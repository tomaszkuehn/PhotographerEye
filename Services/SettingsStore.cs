using System;
using System.IO;
using System.Text.Json;

namespace ImgViewer.Services;

public sealed record ViewerSettings
{
    public double ShadowLift { get; init; }
    public double Gamma { get; init; } = 1.0;
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImgViewer");

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