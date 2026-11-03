using System;
using System.Collections.Generic;
using System.IO;
using ImgViewer.Services;

internal static partial class Program
{
    private static void TestEyeAutoSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pe_eyeauto_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var img = Path.Combine(dir, "a.jpg");
            var eye = EyeFile.SidecarPath(img);
            var a = new EyeAutoSave();
            var st0 = new EyeState { Exposure = 0 };
            var st1 = new EyeState { Exposure = 0.7, PluginText = new Dictionary<string, string> { ["photographereye.heal:spot1/target"] = "0.3,0.5" } };

            Check(!a.Save(img, st0, edited: false) && !File.Exists(eye), "eye autosave: an unedited photo gets no file");
            Check(a.Save(img, st1, edited: true) && File.Exists(eye), "eye autosave: an edit writes the file at once");
            Check(EyeFile.TryLoad(img, out var back) && Math.Abs(back.Exposure - 0.7) < 1e-9 &&
                  back.PluginText?["photographereye.heal:spot1/target"] == "0.3,0.5",
                  "eye autosave: file holds the edit (plugin values too)");
            var t0 = File.GetLastWriteTimeUtc(eye);
            File.SetLastWriteTimeUtc(eye, t0.AddMinutes(-5));
            Check(!a.Save(img, st1, edited: true) && File.GetLastWriteTimeUtc(eye) < t0, "eye autosave: an unchanged state is not written again");
            Check(a.Save(img, st0, edited: false) && EyeFile.TryLoad(img, out back) && back.Exposure == 0,
                  "eye autosave: an existing file follows the state (undo back to the start)");
            Check(!File.Exists(eye + ".tmp"), "eye autosave: no temp file left");

            a.Enabled = false;
            Check(!a.Save(img, st1, edited: true) && EyeFile.TryLoad(img, out back) && back.Exposure == 0, "eye autosave: off writes nothing");
            a.Enabled = true;

            // Save Photo already wrote this state: no second write.
            var b = new EyeAutoSave();
            b.Written(img, st0);
            Check(!b.Save(img, st0, edited: true), "eye autosave: state written by Save Photo is not written twice");

            // Read-only sidecar: the error is reported, nothing thrown.
            File.SetAttributes(eye, FileAttributes.ReadOnly);
            bool saved = b.Save(img, st1, edited: true);
            File.SetAttributes(eye, FileAttributes.Normal);
            Check(!saved && b.LastError is not null && !File.Exists(eye + ".tmp"), $"eye autosave: write error reported, temp file removed ({b.LastError})");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
