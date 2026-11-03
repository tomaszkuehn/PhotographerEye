using System;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.Negative;

// ---------------------------------------------------------------------
//  Negative: toolbar toggle + command with a shortcut
// ---------------------------------------------------------------------

public sealed class NegativePlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.negative",
        Name = "Negative",
        Version = "2.0",
        Author = "PhotographerEye",
        Description = "Inverts the image. Toolbar toggle, also Ctrl+Shift+N."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddFilter(new FilterDescriptor
        {
            Id = "negative",
            Label = "Negative",
            Description = "Invert colors",
            Placement = FilterPlacement.Toolbar,
            Stage = FilterStage.Display,
            SupportsMask = true,
            Render = (img, p, ctx) =>
            {
                Parallel.For(0, img.Height, y =>
                {
                    int row = y * img.Width;
                    for (int i = row; i < row + img.Width; i++)
                    {
                        img.R[i] = 1f - img.R[i];
                        img.G[i] = 1f - img.G[i];
                        img.B[i] = 1f - img.B[i];
                    }
                });
            }
        });
        host.AddCommand(new CommandDescriptor
        {
            Id = "toggle",
            Label = "Toggle negative",
            Shortcut = "Ctrl+Shift+N",
            Placement = CommandPlacement.Menu,
            Execute = c =>
            {
                bool on = !c.IsActive("negative");
                c.SetActive("negative", on);
                c.SetStatus(on ? "Negative on" : "Negative off");
            }
        });
    }
}
