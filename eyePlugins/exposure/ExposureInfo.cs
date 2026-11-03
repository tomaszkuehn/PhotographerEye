using System;
using System.Collections.Generic;
using System.Globalization;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.ExposureInfo;

// ---------------------------------------------------------------------
//  Metadata provider: EV100, megapixels, 35 mm hint
// ---------------------------------------------------------------------

public sealed class ExposureInfoPlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.exposure",
        Name = "Exposure info",
        Version = "2.0",
        Author = "PhotographerEye",
        Description = "Adds EV100 and megapixels to the photo info tooltip."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddMetadataProvider(new MetadataProviderDescriptor
        {
            Id = "ev",
            Label = "Exposure",
            Provide = m =>
            {
                var list = new List<KeyValuePair<string, string>>();
                if (m.Aperture > 0 && m.ShutterSeconds > 0)
                {
                    double ev = Math.Log(m.Aperture.Value * m.Aperture.Value / m.ShutterSeconds.Value, 2);
                    if (m.Iso > 0) ev -= Math.Log(m.Iso.Value / 100.0, 2);
                    list.Add(new KeyValuePair<string, string>("EV100", ev.ToString("0.0", CultureInfo.InvariantCulture)));
                }
                if (m.Width > 0 && m.Height > 0)
                    list.Add(new KeyValuePair<string, string>("Megapixels", (m.Width * (double)m.Height / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MP"));
                return list;
            }
        });
    }
}
