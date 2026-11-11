using System;
using System.IO;
using System.Reflection;

namespace ImgViewer.Services;

/// <summary>
/// The privacy policy shown on the first start (it must be accepted) and from the settings. The text is
/// Assets\privacy-en.md (English only), embedded in the app; raising <see cref="Version"/> asks everyone again.
/// </summary>
public static class PrivacyPolicy
{
    /// <summary>Change it whenever the content of the policy changes: users then accept the new version on the next start.</summary>
    public const string Version = "2026-11-12";

    public static bool IsAccepted(ViewerSettings s) => s.PrivacyAcceptedVersion == Version;

    public static ViewerSettings Accept(ViewerSettings s, DateTime now) =>
        s with { PrivacyAcceptedVersion = Version, PrivacyAcceptedAt = now };

    /// <summary>The Markdown text of the policy.</summary>
    public static string Text()
    {
        const string name = "ImgViewer.Assets.privacy-en.md";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException("missing resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
