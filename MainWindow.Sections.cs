using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ImgViewer;

/// <summary>Collapsible sections of the Adjustments panel (STANDARD, DETAIL and one per plugin).</summary>
public partial class MainWindow
{
    private const string StandardSectionKey = "panel:standard";
    private const string DetailSectionKey = "panel:detail";

    /// <summary>
    /// Builds a collapsible section: header (chevron, title, muted subtitle, "changes this photo" dot, tools)
    /// over <paramref name="body"/>. Expanded sections are tinted with the accent color.
    /// The expanded state is remembered per <paramref name="key"/>.
    /// </summary>
    private Border MakeCollapsibleSection(string key, string automationId, string title, string? subtitle, object? tooltip,
        IEnumerable<UIElement> tools, FrameworkElement body, bool active, bool defaultOpen, out FrameworkElement badge,
        string? headerAutomationId = null)
    {
        var muted = (Brush)FindResource("MutedBrush");
        var border = (Brush)FindResource("BorderBrushCol");
        bool expanded = IsSectionExpanded(key, active, defaultOpen);

        var root = new StackPanel();
        // Outer frame: transparent when collapsed, light accent (blue) tint when expanded.
        var frame = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Child = root
        };
        AutomationProperties.SetAutomationId(frame, automationId);
        var accent = (TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x6C, 0xB2, 0xFF);
        Brush Tint(byte alpha)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
            b.Freeze();
            return b;
        }
        Brush frameBg = Tint(0x1A), frameBorder = Tint(0x70), headOpen = Tint(0x38), headOpenHover = Tint(0x4C);

        var header = new Border
        {
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 4, 4, 4),
            Cursor = Cursors.Hand,
            Focusable = true,
            ToolTip = tooltip
        };
        bool isOpen = false, hover = false;
        void PaintHeader()
        {
            if (isOpen)
            {
                header.Background = hover ? headOpenHover : headOpen;
                header.BorderBrush = Brushes.Transparent;
            }
            else
            {
                header.SetResourceReference(Border.BackgroundProperty, hover ? "ControlHoverBrush" : "ControlBrush");
                header.BorderBrush = border;
            }
        }
        header.MouseEnter += (_, _) => { hover = true; PaintHeader(); };
        header.MouseLeave += (_, _) => { hover = false; PaintHeader(); };
        AutomationProperties.SetAutomationId(header, headerAutomationId ?? automationId + "_Header");
        AutomationProperties.SetName(header, title);

        var dock = new DockPanel { LastChildFill = true };
        var chevron = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 9,
            Width = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = muted
        };
        DockPanel.SetDock(chevron, Dock.Left);
        dock.Children.Add(chevron);

        var toolPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var t in tools) toolPanel.Children.Add(t);
        DockPanel.SetDock(toolPanel, Dock.Right);
        dock.Children.Add(toolPanel);

        var dot = new TextBlock
        {
            Text = "●",
            FontSize = 9,
            Margin = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Changes this photo",
            Visibility = active ? Visibility.Visible : Visibility.Hidden
        };
        dot.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        DockPanel.SetDock(dot, Dock.Right);
        dock.Children.Add(dot);
        badge = dot;

        var titleBlock = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        titleBlock.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(subtitle))
            titleBlock.Inlines.Add(new System.Windows.Documents.Run("  " + subtitle) { Foreground = muted, FontSize = 10 });
        dock.Children.Add(titleBlock);
        header.Child = dock;
        root.Children.Add(header);

        body.Margin = new Thickness(8, 0, 6, 8);
        root.Children.Add(body);

        void Apply(bool open)
        {
            isOpen = open;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            frame.Background = open ? frameBg : Brushes.Transparent;
            frame.BorderBrush = open ? frameBorder : Brushes.Transparent;
            PaintHeader();
            chevron.Text = open ? "" : "";
            AutomationProperties.SetHelpText(header, open ? "expanded" : "collapsed");
        }
        Apply(expanded);
        void Toggle()
        {
            bool open = body.Visibility != Visibility.Visible;
            Apply(open);
            SetSectionExpanded(key, open);
        }
        header.MouseLeftButtonUp += (_, e) => { Toggle(); e.Handled = true; };
        header.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) { Toggle(); e.Handled = true; }
        };
        return frame;
    }

    /// <summary>Wraps the built-in slider groups (STANDARD, DETAIL) of the Adjustments panel into collapsible sections.</summary>
    private void BuildCoreSections()
    {
        WrapCoreSection(StdSectionBody, StandardSectionKey, "STANDARD",
            "Exposure, tone, white balance, clarity and saturation\nClick to expand or collapse");
        WrapCoreSection(DetailSectionBody, DetailSectionKey, "DETAIL",
            "Sharpening, dehaze and noise reduction\nClick to expand or collapse");
    }

    private void WrapCoreSection(StackPanel body, string key, string title, string tooltip)
    {
        if (body.Parent is not Panel host) return;
        var sliders = FindSliders(body).ToList();
        bool Active() => sliders.Any(s => Math.Abs(s.Value) > 1e-9);

        var reset = SmallButton("", $"Reset {title.ToLowerInvariant()} adjustments");
        reset.Click += (_, _) =>
        {
            foreach (var s in sliders) s.Value = 0;
        };

        int index = host.Children.IndexOf(body);
        host.Children.Remove(body);
        var frame = MakeCollapsibleSection(key, "CoreSection_" + title, title, null, tooltip,
            new UIElement[] { reset }, body, Active(), defaultOpen: true, out var badge);
        host.Children.Insert(index, frame);
        if (index == 0) frame.Margin = new Thickness(0);

        void Refresh() => badge.Visibility = Active() ? Visibility.Visible : Visibility.Hidden;
        foreach (var s in sliders) s.ValueChanged += (_, _) => Refresh();
    }

    private static IEnumerable<Slider> FindSliders(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Slider s) yield return s;
            foreach (var d in FindSliders(child)) yield return d;
        }
    }
}
