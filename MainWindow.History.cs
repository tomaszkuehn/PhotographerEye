using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImgViewer.Plugins;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>Edit history for plugins (IEditHistory) and plugin list panels.</summary>
public partial class MainWindow
{
    /// <summary>Step label cache: state → (previous state, label). Reference keys, so a replaced step gets a new label.</summary>
    private readonly ConditionalWeakTable<EyeState, Tuple<EyeState, string>> _histLabels = new();
    private (int, int, EyeState?, EyeState?, string?) _histRaised = (-2, -2, null, null, null);

    private void InitEditHistoryBridge()
    {
        EditHistoryService.StepsSource = HistorySteps;
        EditHistoryService.PositionSource = () => _histPos;
        EditHistoryService.GoToTarget = HistoryGoTo;
    }

    private IReadOnlyList<HistoryStep> HistorySteps()
    {
        var list = new List<HistoryStep>(_hist.Count);
        for (int i = 0; i < _hist.Count; i++)
            list.Add(new HistoryStep { Index = i, Label = i == 0 ? HistoryLabels.Opened : StepLabel(_hist[i - 1], _hist[i]) });
        return list;
    }

    private string StepLabel(EyeState prev, EyeState cur)
    {
        if (_histLabels.TryGetValue(cur, out var c) && ReferenceEquals(c.Item1, prev)) return c.Item2;
        string label;
        try
        {
            label = HistoryLabels.Label(prev, cur, HistoryFilterName);
        }
        catch (Exception ex)
        {
            Debug.Log($"HIST: label failed [{ex.GetType().Name}] {ex.Message}");
            label = "Edit";
        }
        _histLabels.AddOrUpdate(cur, Tuple.Create(prev, label));
        return label;
    }

    /// <summary>Display name of a filter ("pluginId:filterId") or of one of its parameters.</summary>
    private string? HistoryFilterName(string fullId, string? param)
    {
        var f = _pluginReg.FindFilter(fullId);
        if (f is null) return null;
        if (param is not null)
            return f.D.Params.FirstOrDefault(d => d.Id == param) is { Label.Length: > 0 } d ? d.Label : param;
        if (f.D.RepeatGroup.Length > 0)
        {
            string item = $"{f.D.RepeatGroup} {RepeatInstances(f).IndexOf(f) + 1}";
            return f.IsCore ? item : $"{f.PluginName} {item}";
        }
        // A plugin with one filter is named after the plugin.
        if (!f.IsCore && _pluginReg.Filters.Count(o => o.PluginId == f.PluginId) == 1) return f.PluginName;
        return f.D.Label;
    }

    /// <summary>Show history state <paramref name="index"/> (like undo/redo to it); the next edit drops the later steps.</summary>
    private bool HistoryGoTo(int index)
    {
        CommitHistNow();
        if (_currentPath is null || index < 0 || index >= _hist.Count) return false;
        if (index == _histPos) return true;
        _histPos = index;
        _histLastKey = null;
        var st = _hist[_histPos];
        Debug.Log($"HIST: goto -> pos={_histPos}/{_hist.Count}");
        ApplyEyeStateSuppressed(st);
        SyncSessionHist();
        UpdateUndoButtons();
        SetStatus(index == _hist.Count - 1 ? "History — latest state" : $"History — step {index} of {_hist.Count - 1}", error: false);
        return true;
    }

    /// <summary>Tell plugins that the history changed (skipped when nothing they can see changed).</summary>
    private void NotifyHistoryChanged()
    {
        var cur = _histPos >= 0 && _histPos < _hist.Count ? _hist[_histPos] : null;
        var last = _hist.Count > 0 ? _hist[^1] : null;
        var sig = (_hist.Count, _histPos, cur, last, _currentPath);
        if (sig.Item1 == _histRaised.Item1 && sig.Item2 == _histRaised.Item2 && ReferenceEquals(cur, _histRaised.Item3)
            && ReferenceEquals(last, _histRaised.Item4) && string.Equals(_currentPath, _histRaised.Item5, StringComparison.Ordinal))
            return;
        _histRaised = sig;
        EditHistoryService.RaiseChanged();
    }

    // ------------------------------------------------------------------
    //  List panels
    // ------------------------------------------------------------------

    private readonly Dictionary<string, ListBox> _listPanelBoxes = new(StringComparer.Ordinal);

    /// <summary>FullId of the list panel that has keyboard focus (kept across a panel rebuild).</summary>
    private string? FocusedListPanel() =>
        _listPanelBoxes.FirstOrDefault(kv => kv.Value.IsKeyboardFocusWithin).Key;

    private void RestoreListPanelFocus(string? fullId)
    {
        if (fullId is null || !_listPanelBoxes.TryGetValue(fullId, out var list)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!list.IsLoaded) return;
            (list.SelectedItem as ListBoxItem ?? list.Items.OfType<ListBoxItem>().LastOrDefault())?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void BuildListPanels(Brush muted)
    {
        _listPanelBoxes.Clear();
        foreach (var r in _pluginReg.ListPanels
                     .OrderBy(r => r.PluginName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Index))
            PluginPanelHost.Children.Add(BuildListPanel(r, muted));
    }

    private FrameworkElement BuildListPanel(Registered<ListPanelHandle> r, Brush muted)
    {
        var d = r.D.D;
        var list = new ListBox
        {
            Margin = new Thickness(0, 6, 0, 2),
            MaxHeight = d.VisibleRows * 21 + 6,
        };
        list.SetResourceReference(StyleProperty, "PanelList");
        System.Windows.Automation.AutomationProperties.SetAutomationId(list, "PluginList_" + r.FullId);
        _listPanelBoxes[r.FullId] = list;
        var count = new TextBlock { Foreground = muted, FontSize = 11 };
        bool filling = false;
        var shown = new List<(string, string)>();

        void Fill()
        {
            if (r.Owner is { Faulted: true }) return;
            filling = true;
            try
            {
                var items = d.Items?.Invoke() ?? Array.Empty<ListPanelItem>();
                int sel = d.Selected?.Invoke() ?? -1;
                var shownNow = items.Select(it => (it?.Text ?? "", it?.Detail ?? "")).ToList();
                if (!shownNow.SequenceEqual(shown))
                {
                bool hadFocus = list.IsKeyboardFocusWithin;
                shown = shownNow;
                list.Items.Clear();
                foreach (var it in items)
                {
                    var row = new DockPanel { LastChildFill = true };
                    if (!string.IsNullOrEmpty(it?.Detail))
                    {
                        var det = new TextBlock { Text = it!.Detail, Foreground = muted, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                        DockPanel.SetDock(det, Dock.Right);
                        row.Children.Add(det);
                    }
                    row.Children.Add(new TextBlock { Text = it?.Text ?? "", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                    list.Items.Add(new ListBoxItem { Content = row, ToolTip = it?.Text });
                }
                list.SelectedIndex = sel >= 0 && sel < list.Items.Count ? sel : -1;
                // Keep arrow-key navigation working after the rows were rebuilt.
                if (hadFocus) Dispatcher.BeginInvoke(() => (list.SelectedItem as ListBoxItem ?? list.Items.OfType<ListBoxItem>().LastOrDefault())?.Focus(),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else
                {
                    int want = sel >= 0 && sel < list.Items.Count ? sel : -1;
                    if (list.SelectedIndex != want) list.SelectedIndex = want;
                }
                if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
                count.Text = items.Count == 0 ? "" : $"{items.Count}";
            }
            catch (Exception ex)
            {
                if (r.Owner is not null) PluginManager.Instance.ReportFailure(r.Owner, "list panel " + r.LocalId, ex);
            }
            finally
            {
                filling = false;
            }
        }

        list.SelectionChanged += (_, _) =>
        {
            if (filling || list.SelectedIndex < 0 || d.Select is null) return;
            int idx = list.SelectedIndex;
            try
            {
                d.Select(idx);
            }
            catch (Exception ex)
            {
                if (r.Owner is not null) PluginManager.Instance.ReportFailure(r.Owner, "list panel " + r.LocalId, ex);
            }
            // The plugin may refuse or the host may move elsewhere: show what is really selected.
            int now = -1;
            try { now = d.Selected?.Invoke() ?? idx; } catch { }
            if (now != idx) Fill();
        };

        r.D.OnRefresh = Fill;
        list.Unloaded += (_, _) => { if (r.D.OnRefresh == (Action)Fill) r.D.OnRefresh = null; };
        list.Loaded += (_, _) => { r.D.OnRefresh = Fill; Fill(); };
        Fill();

        var body = new StackPanel();
        if (!string.IsNullOrWhiteSpace(d.Description))
            body.Children.Add(new TextBlock { Text = d.Description, Foreground = muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        body.Children.Add(list);

        string sub = string.Equals(d.Label, r.PluginName, StringComparison.CurrentCultureIgnoreCase) ? "" : r.PluginName;
        return MakeCollapsibleSection("list:" + r.FullId, "PluginList_" + r.FullId + "_Section", d.Label, sub.Length > 0 ? sub : null,
            d.Description, new[] { (UIElement)count }, body, active: false, defaultOpen: true, out _,
            headerAutomationId: "PluginListHeader_" + r.FullId);
    }
}
