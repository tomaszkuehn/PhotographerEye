using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ImgViewer.Plugins;

namespace ImgViewer;

/// <summary>
/// "Order…" window under PLUGINS: the plugin filters grouped by the pipeline stage they run in, with up/down only
/// inside a stage that has two or more of them (the built-in adjustments always run at a fixed place in between).
/// </summary>
public partial class MainWindow
{
    private static readonly (FilterStage Stage, string Title, string Hint)[] OrderStages =
    {
        (FilterStage.Base, "First (before exposure and white balance)", "the decoded photo, before any of your corrections"),
        (FilterStage.SceneLinear, "Before tone (scene, linear light)", "after exposure and white balance, before contrast, tone, colour, sharpening and noise reduction"),
        (FilterStage.Display, "After tone and colour", "after all STANDARD and DETAIL adjustments, before crop and rotation"),
        (FilterStage.Output, "After crop", "on the cropped and rotated photo")
    };

    private bool _orderShowAll;

    /// <summary>
    /// Filters of a stage as the order window lists them: only those used on this photo (order matters only between
    /// them), or all with "Show all filters" — repeat groups then show only their used items (or the first).
    /// </summary>
    private List<Registered<FilterDescriptor>> OrderListFor(FilterStage stage)
    {
        var all = _pluginState.Ordered(_pluginReg, stage);
        if (!_orderShowAll) return all.Where(_pluginState.IsFilterActive).ToList();
        var shown = new List<Registered<FilterDescriptor>>();
        foreach (var f in all)
        {
            if (f.D.RepeatGroup is not { Length: > 0 } rg) { shown.Add(f); continue; }
            var group = all.Where(o => o.PluginId == f.PluginId && o.D.RepeatGroup == rg).ToList();
            bool anyUsed = group.Any(_pluginState.IsFilterActive);
            if (anyUsed ? _pluginState.IsFilterActive(f) : group[0] == f) shown.Add(f);
        }
        return shown;
    }

    private string OrderLabel(Registered<FilterDescriptor> f)
    {
        string name = SectionName(f);
        if (f.D.RepeatGroup is { Length: > 0 } rg)
        {
            var inst = RepeatInstances(f);
            return inst.Count > 1 && inst.Any(_pluginState.IsFilterActive) ? $"{name} — {rg} {inst.IndexOf(f) + 1}" : name;
        }
        return string.Equals(f.D.Label, name, StringComparison.CurrentCultureIgnoreCase) ? name : $"{name} — {f.D.Label}";
    }

    /// <summary>Moves a listed filter one place up/down among the listed filters of its stage (hidden items keep their slots).</summary>
    private void MoveInOrderList(FilterStage stage, Registered<FilterDescriptor> f, int delta)
    {
        var shown = OrderListFor(stage);
        int i = shown.IndexOf(f), j = i + delta;
        if (i < 0 || j < 0 || j >= shown.Count) return;
        (shown[i], shown[j]) = (shown[j], shown[i]);
        var full = _pluginState.Ordered(_pluginReg, stage);
        var shownIds = new HashSet<string>(shown.Select(s => s.FullId), StringComparer.Ordinal);
        int k = 0;
        var ids = full.Select(x => shownIds.Contains(x.FullId) ? shown[k++].FullId : x.FullId).ToList();
        _pluginState.SetStageOrder(_pluginReg, stage, ids);
        DeferRebuild();
        OnPluginUiChanged();
    }

    private void ShowFilterOrderWindow()
    {
        var w = new Window
        {
            Title = "Plugin Filter Order",
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(300, ActualHeight - 80),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        w.SetResourceReference(BackgroundProperty, "PanelBrush");
        w.SetResourceReference(ForegroundProperty, "ForegroundBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var border = (Brush)FindResource("BorderBrushCol");

        var root = new DockPanel { Margin = new Thickness(14) };
        var frame = new Border { Child = root };
        frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        var intro = new TextBlock
        {
            Text = "Filters run from top to bottom; each one works on the result of the one above. " +
                   "The order does not depend on when you used them. Only filters in the same stage can be reordered — " +
                   "the built-in adjustments always run between the stages. The order is saved with the photo's edits.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = muted,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var reset = new Button { Content = "Default order", Padding = new Thickness(10, 3, 10, 3), ToolTip = "Forget the custom order (all stages)" };
        reset.SetResourceReference(StyleProperty, "ToolButton");
        var close = new Button { Content = "Close", Padding = new Thickness(14, 3, 14, 3), IsCancel = true, IsDefault = true };
        close.SetResourceReference(StyleProperty, "ToolButton");
        close.Click += (_, _) => w.Close();
        DockPanel.SetDock(close, Dock.Right);
        var showAll = new CheckBox
        {
            Content = "Show all filters",
            IsChecked = _orderShowAll,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            ToolTip = "Also list the filters not used on this photo (to set their place before using them)"
        };
        DockPanel.SetDock(showAll, Dock.Left);
        buttons.Children.Add(reset);
        buttons.Children.Add(showAll);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        var list = new StackPanel();
        AutomationProperties.SetAutomationId(list, "FilterOrderList");
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        void Fill()
        {
            list.Children.Clear();
            bool firstStage = true, any = false;
            foreach (var (stage, title, hint) in OrderStages)
            {
                var items = OrderListFor(stage);
                if (items.Count == 0) continue;
                any = true;
                var head = new StackPanel { Margin = new Thickness(0, firstStage ? 0 : 12, 0, 4) };
                firstStage = false;
                head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
                head.Children.Add(new TextBlock { Text = hint, Foreground = muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                list.Children.Add(head);
                for (int i = 0; i < items.Count; i++)
                {
                    var f = items[i];
                    bool used = _pluginState.IsFilterActive(f);
                    var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), LastChildFill = true };
                    var rowBorder = new Border
                    {
                        Child = row,
                        Padding = new Thickness(8, 3, 4, 3),
                        CornerRadius = new CornerRadius(4),
                        BorderBrush = border,
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(0, 1, 0, 1)
                    };
                    AutomationProperties.SetAutomationId(rowBorder, "FilterOrder_" + f.FullId);
                    if (items.Count > 1)
                    {
                        var tools = new StackPanel { Orientation = Orientation.Horizontal };
                        var up = SmallButton("", "Run earlier");
                        var down = SmallButton("", "Run later");
                        up.IsEnabled = i > 0;
                        down.IsEnabled = i < items.Count - 1;
                        var ff = f;
                        up.Click += (_, _) => { MoveInOrderList(stage, ff, -1); Fill(); };
                        down.Click += (_, _) => { MoveInOrderList(stage, ff, +1); Fill(); };
                        tools.Children.Add(up);
                        tools.Children.Add(down);
                        DockPanel.SetDock(tools, Dock.Right);
                        row.Children.Add(tools);
                    }
                    var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                    text.Inlines.Add(new System.Windows.Documents.Run(items.Count > 1 ? $"{i + 1}.  " : "") { Foreground = muted });
                    text.Inlines.Add(new System.Windows.Documents.Run(OrderLabel(f)));
                    if (!used) text.Inlines.Add(new System.Windows.Documents.Run("   not used on this photo") { Foreground = muted, FontSize = 11 });
                    row.Children.Add(text);
                    list.Children.Add(rowBorder);
                }
            }
            if (!any)
                list.Children.Add(new TextBlock
                {
                    Text = "No plugin filter is used on this photo yet. Tick \"Show all filters\" to set the order in advance.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = muted,
                    Margin = new Thickness(0, 4, 0, 4)
                });
            reset.IsEnabled = _pluginState.Order.Count > 0;
        }

        showAll.Click += (_, _) =>
        {
            _orderShowAll = showAll.IsChecked == true;
            Fill();
        };

        reset.Click += (_, _) =>
        {
            _pluginState.Order.Clear();
            DeferRebuild();
            OnPluginUiChanged();
            Fill();
        };
        Fill();
        w.Content = frame;
        if (Environment.GetEnvironmentVariable("PE_OPEN_SETTINGS") == "order" && Environment.GetEnvironmentVariable("PE_SNAPSHOT") is { Length: > 0 } snap)
            w.ContentRendered += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                SnapshotTo(frame, snap);
                Application.Current.Shutdown();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        w.ShowDialog();
    }
}
