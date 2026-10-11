using System;
using System.IO;

namespace ImgViewer;

public static class Debug
{
    private static readonly object Gate = new();

    public static void Log(string message)
    {
        if (Environment.GetEnvironmentVariable("IMGVIEWER_DEBUG") != "1") return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "imgviewer-debug.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }
}