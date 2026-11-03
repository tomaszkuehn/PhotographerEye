using System;
using System.IO;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.WebExport;

// ---------------------------------------------------------------------
//  Web export + "publish" to an outbox folder (no dialog)
// ---------------------------------------------------------------------

public sealed class WebExportPlugin : IEyePlugin
{
    private IPluginHost _host;

    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.web",
        Name = "Web export",
        Version = "2.0",
        Author = "PhotographerEye",
        Description = "Resized JPEG for the web, and a one-click publish to an outbox folder.",
        Permissions = new[] { PluginPermissions.FileSystem }
    };

    public void Initialize(IPluginHost host)
    {
        _host = host;
        var options = new[]
        {
            ParamDef.Slider("size", "Long side (px)", 640, 4096, 2048, 32),
            ParamDef.Slider("quality", "JPEG quality", 50, 100, 85)
        };
        host.AddExporter(new ExporterDescriptor
        {
            Id = "web",
            Label = "Web JPEG…",
            FileFilter = "JPEG image (*.jpg)|*.jpg",
            DefaultSuffix = "_web",
            Options = options,
            Export = r => Write(r, r.DestinationPath)
        });
        host.AddExporter(new ExporterDescriptor
        {
            Id = "outbox",
            Label = "Publish to outbox",
            FileFilter = null,
            Options = options,
            Export = r =>
            {
                var dir = Path.Combine(_host.DataDirectory, "outbox");
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(r.SourcePath) + "_web.jpg");
                Write(r, dest);
                r.ResultMessage = "Published to " + dest;
            }
        });
    }

    private void Write(ExportRequest r, string dest)
    {
        var img = _host.Imaging.Resize(r.Image, (int)r.Options.Get("size"));
        r.Cancel.ThrowIfCancellationRequested();
        File.WriteAllBytes(dest, _host.Imaging.EncodeJpeg(img, (int)r.Options.Get("quality")));
        if (r.ResultMessage == null) r.ResultMessage = $"Exported {img.Width}x{img.Height} to {Path.GetFileName(dest)}";
    }
}
