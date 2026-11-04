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
    /// <summary>Adjustments panel: expanded (true) / collapsed (false) plugin sections chosen by the user (key = plugin id).</summary>
    public Dictionary<string, bool>? PluginSectionExpanded { get; init; }
    /// <summary>Live RAW preview resolution: "window", "sharp" (2x window) or "full" (see <see cref="PreviewQuality"/>).</summary>
    public string? PreviewQuality { get; init; }
    /// <summary>Remember the last rendered preview of each photo and show it at once when the photo is opened (null = on).</summary>
    public bool? RememberPreviews { get; init; }
    /// <summary>Write the .eye file as soon as an edit is committed (null = on).</summary>
    public bool? AutoSaveEdits { get; init; }
    /// <summary>Surround of the photo in the viewer: dark, black, grey18, light or white (null = dark).</summary>
    public string? ViewerBackground { get; init; }
    /// <summary>Background picture: behind the adjustments panel (when <see cref="PanelPicture"/>) and the welcome screen.</summary>
    public string? ViewerBackgroundImage { get; init; }
    /// <summary>Show the background picture behind the adjustments panel (null = on when a picture is chosen).</summary>
    public bool? PanelPicture { get; init; }
    /// <summary>Blur the background picture (null = on).</summary>
    public bool? ViewerBackgroundBlur { get; init; }
    /// <summary>Show the markers of all masks on the photo also when no mask is being edited (null = off).</summary>
    public bool? ShowMaskMarkers { get; init; }
    /// <summary>How much the background picture is darkened, 0..0.9 (null = 0.6).</summary>
    public double? ViewerBackgroundDim { get; init; }
    /// <summary>Folders opened recently, newest first (welcome screen).</summary>
    public List<string>? RecentFolders { get; init; }
    /// <summary>Slideshow interval in seconds (null = 5).</summary>
    public double? SlideInterval { get; init; }
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