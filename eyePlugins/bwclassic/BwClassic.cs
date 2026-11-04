using System.Linq;
using ImgViewer.Plugins;
using ImgViewer.Plugins.BwProfiles;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.BwClassic;

// ---------------------------------------------------------------------
//  B&W classic: the simplest black & white conversion. One choice of six classic looks that dominate
//  award-winning black & white photography, nothing else to set. Same engine as B&W profiles (BwEngine.cs).
// ---------------------------------------------------------------------

public sealed class BwClassicPlugin : IEyePlugin
{
    public const string FilterId = "bwclassic";

    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "photographereye.bwclassic",
        Name = "B&W classic",
        Version = "1.0",
        Author = "PhotographerEye",
        Description = "Simple black & white conversion: pick one of 6 classic looks of award-winning B&W photography, no other settings."
    };

    //                                                                          R    O    Y    G    C    B    M
    public static readonly BwProfile[] Profiles =
    {
        // Rich full-range print: deep blacks, clean whites, slight red-filter separation. The universal choice.
        BwProfile.Make("classic", "Classic contrast", new double[] { 20, 20, 10, -10, -20, -30, 5 }, contrast: 35, blacks: -8),
        // Ansel Adams style landscape: red filter darkens blue sky, clouds and light stone stand out.
        BwProfile.Make("landscape", "Dramatic sky (red filter)", new double[] { 60, 45, 20, -25, -55, -75, 20 }, contrast: 30),
        // Portrait: orange filter lightens and smooths skin, gentle contrast.
        BwProfile.Make("portrait", "Soft portrait (orange filter)", new double[] { 35, 45, 25, -10, -25, -35, 10 }, contrast: 5, brightness: 8),
        // Street / documentary: pushed high-speed film, punchy contrast and visible grain.
        BwProfile.Make("street", "Street — gritty film", new double[] { 5, 10, 10, 0, -10, -15, 0 }, contrast: 40, grain: 40, grainSize: 50),
        // Fine-art low key: dark, moody, light only where it matters.
        BwProfile.Make("lowkey", "Low key — moody", new double[] { 10, 10, 5, -10, -20, -30, 0 }, contrast: 50, brightness: -25, blacks: -15),
        // Fine-art minimalism: bright, airy, soft greys.
        BwProfile.Make("highkey", "High key — minimal", new double[] { 20, 25, 25, 10, 0, -10, 10 }, contrast: -15, brightness: 35, blacks: 10),
    };

    /// <summary>
    /// Default of the choice: no look picked, the photo stays in colour. A panel filter is only on when a value differs
    /// from its default, so the default must not be a real look, or picking it would not convert the photo.
    /// </summary>
    public const string NoneId = "none";

    public static BwProfile Find(string id) => Profiles.FirstOrDefault(p => p.Id == id);

    public void Initialize(IPluginHost host)
    {
        var choice = ParamDef.Choice("profile", "Look", NoneId,
            new[] { new ParamChoice(NoneId, "— choose a look —") }.Concat(Profiles.Select(p => new ParamChoice(p.Id, p.Name))).ToArray());
        choice.Tooltip = "Pick a look to turn the photo black & white; \"choose a look\" (or the section's reset) keeps it in colour";
        host.AddFilter(new FilterDescriptor
        {
            Id = FilterId,
            Label = "B&W classic",
            Description = "Black & white in one click: six classic looks of award-winning B&W photography",
            Group = "B&W classic",
            Stage = FilterStage.Display,
            SupportsMask = false,
            Params = new[] { choice },
            IsIdentity = p => Find(p.GetText("profile")) == null,
            Render = (img, p, ctx) =>
            {
                var look = Find(p.GetText("profile"));
                if (look != null) BwEngine.Apply(img, look, 1.0, ctx.Scale, ctx.Cancel);
            }
        });
    }
}
