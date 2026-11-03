using System;
using System.Globalization;
using System.IO;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.EventLog;

// ---------------------------------------------------------------------
//  Event logger (host events) writing to its data directory
// ---------------------------------------------------------------------

public sealed class EventLogPlugin : IEyePlugin
{
    private string _log;

    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.eventlog",
        Name = "Event log",
        Version = "2.0",
        Author = "PhotographerEye",
        Description = "Writes opened, saved and exported files to events.log in its data folder."
    };

    public void Initialize(IPluginHost host)
    {
        _log = Path.Combine(host.DataDirectory, "events.log");
        host.Events.ImageOpened += (s, e) => Write($"opened  {e.Path}  {e.Meta.Width}x{e.Meta.Height}");
        host.Events.BeforeSave += (s, e) => Write($"saving  {e.SourcePath} -> {e.DestinationPath} ({e.Format})");
        host.Events.AfterSave += (s, e) => Write($"saved   {e.DestinationPath} ok={e.Success}");
        host.Events.AfterExport += (s, e) => Write($"export  {e.DestinationPath} via {e.Format} ok={e.Success}");
    }

    private void Write(string line)
    {
        lock (this)
            File.AppendAllText(_log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
    }
}
