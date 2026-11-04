using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using ImgViewer;
using ImgViewer.Services;

internal static partial class Program
{
    /// <summary>
    /// The basic-adjustment table (<see cref="CoreParams"/>): keys, aliases, Get/Set, ranges matching the sliders in
    /// MainWindow.xaml, undo labels, EditChanged keys and coalescing groups, and every EyeState field listed.
    /// </summary>
    private static void TestCoreParams()
    {
        var all = CoreParams.All;
        Check(all.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Count &&
              all.Select(p => p.Field).Distinct().Count() == all.Count, "coreparams: keys and fields unique");
        Check(all.All(p => CoreParams.Find(p.Key) == p && CoreParams.Find("core." + p.Key.ToLowerInvariant()) == p && CoreParams.Find(p.Field) == p),
              "coreparams: Find by key, core.key (any case) and field name");
        Check(CoreParams.Find("bogus") is null && CoreParams.Find(" ") is null && CoreParams.Find(null) is null, "coreparams: unknown keys");
        Check(all.All(p => p.Min < p.Max && p.Default >= p.Min && p.Default <= p.Max), "coreparams: default inside the range");

        var s = new EyeState();
        bool rt = all.All(p =>
        {
            double v = p.Integer ? p.Max : (p.Min + p.Max) * 0.37;
            return Math.Abs(p.Get(p.Set(s, v)) - v) < 1e-12 && typeof(EyeState).GetProperty(p.Field) is not null;
        });
        Check(rt, "coreparams: Set/Get round trip on the named EyeState field");
        Check(all.All(p => Math.Abs(p.Get(s) - p.Default) < 1e-12), "coreparams: a new EyeState holds the defaults");
        var changed = all.First(p => p.Key == "Contrast").Set(s, 5);
        Check(!CoreParams.ValuesEqual(s, changed) && CoreParams.ValuesEqual(s, s with { }), "coreparams: ValuesEqual");

        // Every EyeState property is described (a new field without an entry would get a raw name in the history).
        string[] unlisted = { "Version", "PluginValues", "PluginText", "PluginMasks", "History", "HistoryPos" };
        var missing = typeof(EyeState).GetProperties().Select(p => p.Name)
            .Where(n => !unlisted.Contains(n) && CoreParams.FieldLabel(n) == n && CoreParams.Find(n) is null).ToList();
        Check(missing.Count == 0, $"coreparams: every EyeState field has a label ({string.Join(", ", missing)})");

        // Labels, EditChanged keys and coalescing groups as before the table.
        Check(CoreParams.FieldLabel("ShadowLift") == "Shadow lift" && CoreParams.FieldLabel("ColorNoise") == "Color noise" &&
              CoreParams.FieldLabel("Mono") == "Black & white" && CoreParams.FieldLabel("Exposure") == "Exposure" &&
              CoreParams.FieldLabel("CropAspect") == "Crop" && CoreParams.FieldLabel("StretchEv") == "Histogram" &&
              CoreParams.FieldLabel("Angle") == "Straighten" && CoreParams.FieldLabel("PluginOrder") == "Filter order" &&
              CoreParams.FieldLabel("Whatever") == "Whatever", "coreparams: undo labels");
        Check(HistoryLabels.FromKey("ShadowLift,Exposure") == "Shadow lift, Exposure" && HistoryLabels.FromKey("CropX,CropW") == "Crop",
              "coreparams: HistoryLabels uses the table");
        Check(CoreParams.ChangeKey("ShadowLift") == "Shadows" && CoreParams.ChangeKey("CropH") == "Crop" &&
              CoreParams.ChangeKey("Stretch") == "Stretch" && CoreParams.ChangeKey("StretchEv") == "Stretch" &&
              CoreParams.ChangeKey("Exposure") == "Exposure" && CoreParams.ChangeKey("CurveR") == "CurveR", "coreparams: EditChanged keys");
        var keys = EditChangeKeys.Map(s, s with { ShadowLift = 3, CropW = 0.5, CropAspect = "3:2", StretchEv = new[] { new[] { 0.0, 1.0 } } }, out _);
        Check(keys.OrderBy(k => k).SequenceEqual(new[] { "core.Crop", "core.Shadows", "core.Stretch" }),
              $"coreparams: EditChangeKeys.Map ({string.Join(" ", keys)})");
        Check(HistoryCoalesce.GroupKey("CropX,CropW,CropAspect") == "Crop" && HistoryCoalesce.GroupKey("Exposure,CropY") == "Exposure,Crop" &&
              HistoryCoalesce.GroupKey("PluginValues[a:b/c]") == "PluginValues[a:b/c]" && HistoryCoalesce.GroupKey("") == "",
              "coreparams: coalescing groups");

        // Plugin API wrappers.
        Check(PluginEditOps.CoreKeyNames.SequenceEqual(all.Select(p => p.Key)) && PluginEditOps.CoreKey("shadowlift") == "Shadows",
              "coreparams: plugin keys come from the table");

        // Value text.
        var exp = CoreParams.Find("Exposure")!;
        var con = CoreParams.Find("Contrast")!;
        Check(exp.Format(0.5) == "+0.50" && exp.Format(-1.25) == "-1.25" && exp.Format(-0.001) == "0.00" &&
              con.Format(-0.3) == "0" && con.Format(12.6) == "13" && con.Format(-40) == "-40", "coreparams: value text");
        Check(CoreParams.Find("Mono")!.Clamp(1.6) == 2 && con.Clamp(250) == 100, "coreparams: Clamp");

        // The sliders named in the table exist in MainWindow.xaml with the same range.
        var root = FindRepoRoot();
        if (root is null)
        {
            Check(false, "coreparams: repository root not found");
            return;
        }
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
        var named = doc.Descendants().Where(e => e.Attribute(x + "Name") is not null)
            .GroupBy(e => e.Attribute(x + "Name")!.Value).ToDictionary(g => g.Key, g => g.First());
        foreach (var p in all)
        {
            foreach (var name in new[] { p.Slider, p.PanelSlider }.Where(n => n is not null))
            {
                bool ok = named.TryGetValue(name!, out var el) && el.Name.LocalName == "Slider";
                double min = ok ? Attr(el!, "Minimum", 0) : double.NaN, max = ok ? Attr(el!, "Maximum", 10) : double.NaN;
                Check(ok && min == p.Min && max == p.Max, $"coreparams: {p.Key} slider {name} range {min}..{max} = {p.Min}..{p.Max}");
            }
            if (p.ValueText is { } t)
                Check(named.TryGetValue(t, out var tb) && tb.Name.LocalName == "TextBlock", $"coreparams: {p.Key} value text {t}");
        }
    }

    private static double Attr(XElement e, string name, double def) =>
        e.Attribute(name) is { } a ? double.Parse(a.Value, CultureInfo.InvariantCulture) : def;
}
