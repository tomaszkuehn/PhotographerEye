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
        Description = "Writes opened, closed, saved and exported files, edits and filter switches to events.log in its data folder."
    };

    public void Initialize(IPluginHost host)
    {
        _log = Path.Combine(host.DataDirectory, "events.log");
        host.Events.ImageOpened += (s, e) => Write($"opened  {e.Path}  {e.Meta.Width}x{e.Meta.Height}");
        host.Events.BeforeSave += (s, e) => Write($"saving  {e.SourcePath} -> {e.DestinationPath} ({e.Format})");
        host.Events.AfterSave += (s, e) => Write($"saved   {e.DestinationPath} ok={e.Success}");
        host.Events.AfterExport += (s, e) => Write($"export  {e.DestinationPath} via {e.Format} ok={e.Success}");
        host.Events.ImageClosed += (s, e) => Write($"closed  {e.Path}");
        host.Events.EditChanged += (s, e) => Write($"edit    {e.Path}  {string.Join(", ", e.Keys)}{(e.FromHistory ? "  (history)" : "")}");
        host.Events.FilterToggled += (s, e) => Write($"filter  {e.FilterId} {(e.Active ? "on" : "off")}  {e.Path}");
    }

    private void Write(string line)
    {
        lock (this)
            File.AppendAllText(_log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
    }
}
