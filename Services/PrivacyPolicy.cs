using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace ImgViewer.Services;

/// <summary>
/// The privacy policy shown on the first start (it must be accepted) and from the settings. The texts are
/// Assets\privacy-pl.md and Assets\privacy-en.md, embedded in the app; raising <see cref="Version"/> asks everyone again.
/// </summary>
public static class PrivacyPolicy
{
    /// <summary>Change it whenever the content of the policy changes: users then accept the new version on the next start.</summary>
    public const string Version = "2026-11-11";

    public static readonly string[] Languages = { "pl", "en" };

    /// <summary>Polish for a Polish Windows, otherwise English.</summary>
    public static string DefaultLanguage =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl" ? "pl" : "en";

    public static bool IsAccepted(ViewerSettings s) => s.PrivacyAcceptedVersion == Version;

    public static ViewerSettings Accept(ViewerSettings s, DateTime now) =>
        s with { PrivacyAcceptedVersion = Version, PrivacyAcceptedAt = now };

    /// <summary>The Markdown text of the policy in <paramref name="lang"/> ("pl" or "en").</summary>
    public static string Text(string lang)
    {
        string name = $"ImgViewer.Assets.privacy-{(lang == "pl" ? "pl" : "en")}.md";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException("missing resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
