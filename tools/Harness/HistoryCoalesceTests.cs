using System;
using System.Collections.Generic;
using ImgViewer.Services;

internal static partial class Program
{
    private static void TestHistoryCoalesce()
    {
        static bool Eq(EyeState a, EyeState b) => HistoryCoalesce.ChangeKey(a, b).Length == 0;
        var s0 = new EyeState();
        var hist = new List<EyeState> { s0 };
        int pos = 0;
        string? key = null;

        // Brightness up, down, up: one step with the last value.
        foreach (var ev in new[] { 0.5, -0.3, 0.8 })
            key = HistoryCoalesce.Commit(hist, ref pos, s0 with { Exposure = ev }, key, 100, Eq);
        Check(hist.Count == 2 && pos == 1 && hist[1].Exposure == 0.8, $"history: same slider coalesces into one step ({hist.Count})");

        // Another slider starts a new step.
        var s1 = hist[1] with { Contrast = 20 };
        key = HistoryCoalesce.Commit(hist, ref pos, s1, key, 100, Eq);
        key = HistoryCoalesce.Commit(hist, ref pos, s1 with { Contrast = 35 }, key, 100, Eq);
        Check(hist.Count == 3 && hist[2].Contrast == 35 && hist[2].Exposure == 0.8, "history: other slider gets its own step");

        // Back to exposure: new step again (not merged into the first exposure step).
        key = HistoryCoalesce.Commit(hist, ref pos, hist[2] with { Exposure = 1.0 }, key, 100, Eq);
        Check(hist.Count == 4, "history: returning to an earlier slider adds a step");

        // Moving the slider back to its value before the step removes the step.
        key = HistoryCoalesce.Commit(hist, ref pos, hist[3] with { Exposure = 0.8 }, key, 100, Eq);
        Check(hist.Count == 3 && pos == 2, "history: slider moved back to the previous value drops the step");

        // Plugin parameters are keyed separately.
        var p0 = hist[2] with { PluginValues = new Dictionary<string, double> { ["a:x"] = 0, ["a:y"] = 0 } };
        key = HistoryCoalesce.Commit(hist, ref pos, p0, key, 100, Eq);
        int n = hist.Count;
        key = HistoryCoalesce.Commit(hist, ref pos, p0 with { PluginValues = new() { ["a:x"] = 5, ["a:y"] = 0 } }, key, 100, Eq);
        key = HistoryCoalesce.Commit(hist, ref pos, p0 with { PluginValues = new() { ["a:x"] = 9, ["a:y"] = 0 } }, key, 100, Eq);
        key = HistoryCoalesce.Commit(hist, ref pos, p0 with { PluginValues = new() { ["a:x"] = 9, ["a:y"] = 3 } }, key, 100, Eq);
        Check(hist.Count == n + 2 && hist[^2].PluginValues!["a:x"] == 9, "history: plugin parameters coalesce per parameter");

        // After undo (key reset) the next edit after a redo-truncation is a new step.
        pos--;
        key = null;
        key = HistoryCoalesce.Commit(hist, ref pos, hist[pos] with { Saturation = 10 }, key, 100, Eq);
        Check(hist.Count == n + 2 && pos == hist.Count - 1 && hist[pos].Saturation == 10, "history: edit after undo replaces redo branch");

        // Brush strokes on one mask stay separate steps.
        MaskSpec M(int strokes) => new() { Kind = "brush", Strokes = new List<double[]>(new double[strokes][]) };
        var b0 = hist[pos] with { PluginMasks = new() { ["a"] = M(0) } };
        key = HistoryCoalesce.Commit(hist, ref pos, b0, key, 100, Eq);
        int nb = hist.Count;
        key = HistoryCoalesce.Commit(hist, ref pos, b0 with { PluginMasks = new() { ["a"] = M(1) } }, key, 100, Eq);
        key = HistoryCoalesce.Commit(hist, ref pos, b0 with { PluginMasks = new() { ["a"] = M(2) } }, key, 100, Eq);
        Check(hist.Count == nb + 2, "history: each brush stroke is its own step");
    }
}
