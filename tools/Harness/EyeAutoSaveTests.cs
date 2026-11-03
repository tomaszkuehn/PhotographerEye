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

            // _eye_ subfolder: created when missing, one file per photo (RAW + JPEG of the same name stay apart).
            var sub = Path.Combine(dir, EyeFile.FolderName);
            Directory.Delete(sub, true);
            var raw = Path.Combine(dir, "a.nef");
            var c = new EyeAutoSave();
            Check(c.Save(raw, st1, edited: true) && Directory.Exists(sub) && File.Exists(Path.Combine(sub, "a.nef.eye")),
                  "eye autosave: missing _eye_ folder is created");
            Check(c.Save(img, st0 with { Exposure = 0.2 }, edited: true) && EyeFile.TryLoad(raw, out back) && Math.Abs(back.Exposure - 0.7) < 1e-9 &&
                  EyeFile.TryLoad(img, out back) && Math.Abs(back.Exposure - 0.2) < 1e-9,
                  "eye autosave: a.jpg and a.nef get separate files in _eye_");
            Check(!File.Exists(Path.Combine(dir, "a.eye")), "eye autosave: nothing written next to the photo");

            // Old file next to the photo: still read, the next write goes to _eye_ (old file left as is).
            var old = Path.Combine(dir, "b.jpg");
            File.WriteAllText(EyeFile.LegacySidecarPath(old), EyeFile.Serialize(st1));
            Check(EyeFile.TryLoad(old, out back) && Math.Abs(back.Exposure - 0.7) < 1e-9, "eye file: old b.eye next to the photo is still read");
            Check(c.Save(old, st0, edited: false) && File.Exists(EyeFile.SidecarPath(old)) && EyeFile.TryLoad(old, out back) && back.Exposure == 0,
                  "eye autosave: photo with an old file is saved to _eye_, which then wins");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
