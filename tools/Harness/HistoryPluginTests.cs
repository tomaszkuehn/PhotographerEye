using System;
using System.Collections.Generic;
using System.Linq;
using ImgViewer;
using ImgViewer.Plugins;
using ImgViewer.Services;

internal static partial class Program
{
    private static void TestHistoryPlugin()
    {
        // ---------------- step labels ----------------
        var s0 = new EyeState();
        Check(HistoryLabels.Label(s0, s0 with { Exposure = 0.5 }) == "Exposure", "history labels: core slider");
        Check(HistoryLabels.Label(s0, s0 with { CropX = 0.1, CropW = 0.8 }) == "Crop", "history labels: crop fields collapse to one name");
        Check(HistoryLabels.Label(s0, s0 with { Rot90 = 1 }) == "Rotate" && HistoryLabels.Label(s0, s0 with { Angle = 2 }) == "Straighten",
              "history labels: rotate / straighten");
        Check(HistoryLabels.Label(s0, s0 with { CurveR = new[] { new[] { 0.0, 0.1 }, new[] { 1.0, 1.0 } } }) == "Red curve", "history labels: channel curve");
        var pv = s0 with { PluginValues = new Dictionary<string, double> { ["photographereye.heal:spot1/size"] = 3 } };
        Check(HistoryLabels.Label(s0, pv) == "spot1: size", $"history labels: plugin value without names ({HistoryLabels.Label(s0, pv)})");
        string Names(string f, string p) => p is null ? (f == "photographereye.heal:spot1" ? "Heal Spot 1" : null) : p == "size" ? "Size" : null;
        Check(HistoryLabels.Label(s0, pv, Names) == "Heal Spot 1: Size", $"history labels: plugin value with names ({HistoryLabels.Label(s0, pv, Names)})");
        var mk = s0 with
        {
            PluginMasks = new Dictionary<string, MaskSpec>
            {
                ["core:adjustments"] = new MaskSpec { Kind = "brush", Strokes = new List<double[]> { new double[] { 1, 0.1, 0.2, 0.2 } } }
            }
        };
        Check(HistoryLabels.Label(s0, mk, (f, p) => f == "core:adjustments" ? "Adjustments" : null) == "Adjustments brush", "history labels: brush stroke");
        var many = s0 with { Exposure = 1, Contrast = 2, Saturation = 3, Clarity = 4 };
        Check(HistoryLabels.Label(s0, many) == "Exposure, Contrast +2", $"history labels: many changes shortened ({HistoryLabels.Label(s0, many)})");

        // ---------------- plugin ----------------
        var path = FindPluginDll("history", "ImgViewer.Plugins.History.dll");
        if (path is null) { Check(false, "history dll present (build eyePlugins/history first)"); return; }
        var entry = PluginManager.LoadFromFile(path);
        Check(entry.Error is null && entry.Plugins.Count == 1 && entry.Plugins[0].Error is null, $"history plugin loads ({entry.Error})");
        var reg = new PluginRegistry(entry.Plugins);
        var panel = reg.ListPanels.FirstOrDefault(r => r.FullId == "photographereye.history:steps");
        Check(panel is not null && panel.D.D.Label == "History" && panel.D.D.Select is not null, "history plugin: list panel registered");
        if (panel is null) return;

        // Fake host history driven like the main window (HistoryCoalesce keeps the undo list).
        var hist = new List<EyeState> { s0 };
        int pos = 0;
        string lastKey = null;
        bool Equal(EyeState a, EyeState b) => HistoryCoalesce.ChangeKey(a, b).Length == 0;
        void Commit(EyeState st)
        {
            lastKey = HistoryCoalesce.Commit(hist, ref pos, st, null, 50, Equal);
        }
        var oldSteps = EditHistoryService.StepsSource;
        var oldPos = EditHistoryService.PositionSource;
        var oldGo = EditHistoryService.GoToTarget;
        EditHistoryService.StepsSource = () => hist.Select((h, i) => new HistoryStep { Index = i, Label = i == 0 ? HistoryLabels.Opened : HistoryLabels.Label(hist[i - 1], h) }).ToList();
        EditHistoryService.PositionSource = () => pos;
        EditHistoryService.GoToTarget = i =>
        {
            if (i < 0 || i >= hist.Count) return false;
            pos = i;
            return true;
        };
        int refreshes = 0;
        panel.D.OnRefresh = () => refreshes++;
        try
        {
            Commit(s0 with { Exposure = 0.5 });
            Commit(hist[pos] with { Contrast = 10 });
            Commit(hist[pos] with { Rot90 = 1 });
            EditHistoryService.RaiseChanged(entry.Plugins);
            Check(refreshes == 1, "history plugin: Changed refreshes the panel");
            var items = panel.D.D.Items();
            Check(items.Select(i => i.Text).SequenceEqual(new[] { "Opened", "Exposure", "Contrast", "Rotate" }) && panel.D.D.Selected() == 3,
                  $"history plugin: lists all steps, latest selected ({string.Join(" | ", items.Select(i => i.Text))})");

            panel.D.D.Select(1);
            Check(pos == 1 && panel.D.D.Selected() == 1 && hist.Count == 4, "history plugin: selecting a step shows that version, later steps kept");
            panel.D.D.Select(3);
            panel.D.D.Select(1);
            Check(pos == 1 && hist.Count == 4, "history plugin: moving back and forth keeps the history");

            // An edit on the shown older version drops everything after it.
            Commit(hist[pos] with { Saturation = 20 });
            items = panel.D.D.Items();
            Check(hist.Count == 3 && pos == 2 && items.Select(i => i.Text).SequenceEqual(new[] { "Opened", "Exposure", "Saturation" }),
                  $"history plugin: edit on an older version starts a new branch ({string.Join(" | ", items.Select(i => i.Text))})");

            // A faulted plugin gets no more events.
            entry.Plugins[0].Faulted = true;
            EditHistoryService.RaiseChanged(entry.Plugins);
            Check(refreshes == 1, "history plugin: faulted plugin is not called");
            entry.Plugins[0].Faulted = false;
        }
        finally
        {
            panel.D.OnRefresh = null;
            EditHistoryService.StepsSource = oldSteps;
            EditHistoryService.PositionSource = oldPos;
            EditHistoryService.GoToTarget = oldGo;
        }
        _ = lastKey;
    }
}
