using System;
using System.Collections.Generic;
using System.IO;

namespace ImgViewer.Services;

/// <summary>
/// Writes the edit fingerprint (<c>_eye_\&lt;photo file name&gt;.eye</c> in the photo folder) as soon as an edit is committed, so edits survive a restart
/// without saving the photo. A photo that was never edited gets no file; an unchanged state is not written again.
/// </summary>
public sealed class EyeAutoSave
{
    private readonly Dictionary<string, string> _written = new(StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; set; } = true;

    /// <summary>Last write error (folder read-only etc.), null after a successful write.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Write <paramref name="state"/> for <paramref name="imagePath"/>. <paramref name="edited"/> is false while the photo
    /// still shows the state it was opened with: then no new file is created (an existing one is still kept up to date).
    /// Returns true when the file was written.
    /// </summary>
    public bool Save(string imagePath, EyeState state, bool edited)
    {
        if (!Enabled) return false;
        var json = EyeFile.Serialize(state);
        if (_written.TryGetValue(imagePath, out var prev) && prev == json) return false;
        if (!edited && !EyeFile.Exists(imagePath)) return false;
        try
        {
            EyeFile.WriteJson(imagePath, json);
            _written[imagePath] = json;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.Log($"EYE AUTOSAVE ERROR: {Path.GetFileName(imagePath)} [{ex.GetType().Name}] {ex.Message}");
            return false;
        }
    }

    /// <summary>The file was written elsewhere (Save Photo): remember it so the same state is not written twice.</summary>
    public void Written(string imagePath, EyeState state) => _written[imagePath] = EyeFile.Serialize(state);
}
