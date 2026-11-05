using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ImgViewer;

/// <summary>Icon buttons for the mask scope (whole photo / gradient / radial / brush / luminance / plugin masks) and the histogram.</summary>
public partial class MainWindow
{
    private ToggleButton? _adjHistToggle;
    private bool _histToggleHooked;

    /// <summary>Square icon toggle in the tool style; the icon follows the button's foreground (accent when checked).</summary>
    private static ToggleButton IconToggle(UIElement icon, string tip, string automationId, bool isChecked)
    {
        var b = new ToggleButton
        {
            Content = icon,
            Width = 26,
            Height = 22,
            Margin = new Thickness(0, 0, 3, 3),
            ToolTip = tip,
            IsChecked = isChecked
        };
        b.SetResourceReference(StyleProperty, "ToolToggle");
        AutomationProperties.SetAutomationId(b, automationId);
        AutomationProperties.SetName(b, tip);
        return b;
    }

    private static Binding ButtonForeground() =>
        new("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1) };

    private static Shape Fg(Shape s, bool fill)
    {
        s.SetBinding(fill ? Shape.FillProperty : Shape.StrokeProperty, ButtonForeground());
        return s;
    }

    private static Canvas IconCanvas(params UIElement[] parts)
    {
        var c = new Canvas { Width = 16, Height = 16, SnapsToDevicePixels = true };
        foreach (var p in parts) c.Children.Add(p);
        return c;
    }

    private static Shape Frame() => Fg(new Rectangle { Width = 15, Height = 12, StrokeThickness = 1.2, RadiusX = 1.5, RadiusY = 1.5 }.At(0.5, 2), false);


    private static UIElement HistogramIcon() =>
        IconCanvas(Fg(new Path
        {
            Data = Geometry.Parse("M1,14 V10 H3 V14 Z M4,14 V6 H6 V14 Z M7,14 V2.5 H9 V14 Z M10,14 V5 H12 V14 Z M13,14 V9 H15 V14 Z")
        }, true));

    /// <summary>Histogram icon on the Adjustments panel header bar: show or hide the histogram (it hides on every photo change).</summary>
    private ToggleButton BuildHistogramToggle()
    {
        var b = IconToggle(HistogramIcon(), "Histogram — show or hide", "BtnAdjHistogram", HistHost.Visibility == Visibility.Visible);
        b.Click += (_, _) =>
        {
            if (b.IsChecked == true) ShowHistogram();
            else OnHistCloseClick(b, new RoutedEventArgs());
        };
        _adjHistToggle = b;
        if (!_histToggleHooked)
        {
            _histToggleHooked = true;
            HistHost.IsVisibleChanged += (_, _) =>
            {
                if (_adjHistToggle is { } t) t.IsChecked = HistHost.Visibility == Visibility.Visible;
            };
        }
        return b;
    }

    private void ShowHistogram()
    {
        _showHistogram = true;
        if (RightPanel.Visibility == Visibility.Visible && !_presenting) HistHost.Visibility = Visibility.Visible;
        if (_currentPath is null) SetStatus("Histogram — open a photo first", error: false);
    }
}

internal static class IconPlacement
{
    public static T At<T>(this T e, double x, double y) where T : UIElement
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }
}
