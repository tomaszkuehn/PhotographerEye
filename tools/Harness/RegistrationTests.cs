using System;
using System.Linq;
using ImgViewer;
using ImgViewer.Plugins;

internal static partial class Program
{
    private sealed class SampleWidgetDescriptor { public string Name = ""; }

    /// <summary>
    /// Generic plugin registrations (<see cref="RegistrationStore"/>): every kind lives in one store, the registry
    /// and the Plugins window summary pick up a new kind without host changes.
    /// </summary>
    private static void TestRegistrations()
    {
        var dll = FindPluginDll("netpbm", "ImgViewer.Plugins.Netpbm.dll");
        if (dll is null) { Check(false, "registrations: netpbm dll present (build eyePlugins/netpbm first)"); return; }
        var e = PluginManager.LoadFromFile(dll);
        Check(e.Error is null && e.Plugins.Count == 1, $"registrations: netpbm loads ({e.Error})");
        if (e.Plugins.Count != 1) return;
        var lp = e.Plugins[0];
        lp.Enabled = true;
        Check(lp.Importers.Count > 0 && ReferenceEquals(lp.Importers, lp.Registrations.Of<ImporterDescriptor>()),
              "registrations: typed shortcut is the store list");
        var summary = lp.Registrations.Summary().ToList();
        var imp = summary.FirstOrDefault(s => s.Contains("importer(s)")) ?? "";
        Check(imp.StartsWith($"{lp.Importers.Count} importer(s): ") && lp.Importers.SelectMany(i => i.D.Extensions).All(imp.Contains),
              $"registrations: importer summary lists extensions ({imp})");
        Check(lp.Registrations.Count == summary.Sum(s => int.Parse(s.Split(' ')[0])), "registrations: summary counts add up");

        // A kind the host does not know: stored, labelled from its type name, visible in the registry.
        var w = new SampleWidgetDescriptor { Name = "w1" };
        lp.Registrations.Of<SampleWidgetDescriptor>().Add(new Registered<SampleWidgetDescriptor>
        {
            PluginId = lp.Id, PluginName = lp.Name, FullId = lp.Id + ":w1", LocalId = "w1", D = w, Owner = lp
        });
        Check(lp.Registrations.Summary().Contains("1 sample widget(s)"), "registrations: unknown kind labelled from the type name");
        var reg = new PluginRegistry(new[] { lp });
        Check(reg.All<SampleWidgetDescriptor>().Count == 1 && reg.All<SampleWidgetDescriptor>()[0].D == w,
              "registrations: registry exposes a new kind via All<T>()");
        Check(reg.Importers.Count == lp.Importers.Count && ReferenceEquals(reg.Importers, reg.All<ImporterDescriptor>()),
              "registrations: typed registry lists come from All<T>()");
        lp.Enabled = false;
        Check(new PluginRegistry(new[] { lp }).All<SampleWidgetDescriptor>().Count == 0, "registrations: disabled plugin contributes nothing");
        lp.Registrations.Clear();
        Check(lp.Registrations.Count == 0 && !lp.Registrations.Summary().Any(), "registrations: Clear empties every kind");
    }
}
