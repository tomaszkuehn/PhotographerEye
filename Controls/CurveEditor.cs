using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ImgViewer.Plugins;

namespace ImgViewer.Controls;

/// <summary>
/// Small tone-curve editor: click to add a point, drag to move, right-click or drag off the box to remove.
/// Value format matches <see cref="ToneCurve"/>: "x,y;x,y;..." with 0..1 coordinates.
/// </summary>
public sealed class CurveEditor : Canvas
{
    private List<Point> _pts = new() { new Point(0, 0), new Point(1, 1) };
    private int _drag = -1;

    /// <summary>Raised while dragging (final = false) and on release (final = true).</summary>
    public event Action<string, bool>? CurveChanged;

    public CurveEditor()
    {
        Height = 140;
        Background = new SolidColorBrush(Color.FromArgb(255, 30, 32, 38));
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonDown += OnRight;
        SizeChanged += (_, _) => Redraw();
    }

    public string Value
    {
        get => Format(_pts);
        set
        {
            var c = ToneCurve.Parse(value ?? "");
            _pts = c.X.Select((x, i) => new Point(x, c.Y[i])).ToList();
            if (_pts.Count < 2) _pts = new() { new Point(0, 0), new Point(1, 1) };
            Redraw();
        }
    }

    private static string Format(List<Point> pts) => string.Join(";", pts.OrderBy(p => p.X)
        .Select(p => p.X.ToString("0.###", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.###", CultureInfo.InvariantCulture)));

    private Point ToView(Point p) => new(p.X * ActualWidth, (1 - p.Y) * ActualHeight);

    private Point ToCurve(Point v) => new(Math.Clamp(v.X / Math.Max(1, ActualWidth), 0, 1), Math.Clamp(1 - v.Y / Math.Max(1, ActualHeight), 0, 1));

    private int Hit(Point v)
    {
        for (int i = 0; i < _pts.Count; i++)
            if ((ToView(_pts[i]) - v).Length < 8) return i;
        return -1;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var v = e.GetPosition(this);
        _drag = Hit(v);
        if (_drag < 0)
        {
            var c = ToCurve(v);
            _pts.Add(c);
            _pts = _pts.OrderBy(p => p.X).ToList();
            _drag = _pts.IndexOf(c);
        }
        CaptureMouse();
        e.Handled = true;
        Redraw();
        CurveChanged?.Invoke(Value, false);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag < 0) return;
        var c = ToCurve(e.GetPosition(this));
        bool end = _drag == 0 || _drag == _pts.Count - 1;
        double lo = _drag > 0 ? _pts[_drag - 1].X + 0.01 : 0;
        double hi = _drag < _pts.Count - 1 ? _pts[_drag + 1].X - 0.01 : 1;
        if (end && _pts.Count > 1) c.X = _drag == 0 ? Math.Min(c.X, hi) : Math.Max(c.X, lo);
        else c.X = Math.Clamp(c.X, lo, Math.Max(lo, hi));
        _pts[_drag] = c;
        Redraw();
        CurveChanged?.Invoke(Value, false);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_drag < 0) return;
        var v = e.GetPosition(this);
        bool outside = v.X < -12 || v.Y < -12 || v.X > ActualWidth + 12 || v.Y > ActualHeight + 12;
        if (outside && _pts.Count > 2) _pts.RemoveAt(_drag);
        _drag = -1;
        Redraw();
        CurveChanged?.Invoke(Value, true);
    }

    private void OnRight(object sender, MouseButtonEventArgs e)
    {
        int i = Hit(e.GetPosition(this));
        e.Handled = true;
        if (i < 0 || _pts.Count <= 2) return;
        _pts.RemoveAt(i);
        Redraw();
        CurveChanged?.Invoke(Value, true);
    }

    public void Reset()
    {
        _pts = new() { new Point(0, 0), new Point(1, 1) };
        Redraw();
    }

    private void Redraw()
    {
        Children.Clear();
        if (ActualWidth < 4 || ActualHeight < 4) return;
        var grid = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        for (int i = 1; i < 4; i++)
        {
            Children.Add(new Line { X1 = ActualWidth * i / 4, X2 = ActualWidth * i / 4, Y1 = 0, Y2 = ActualHeight, Stroke = grid, StrokeThickness = 1 });
            Children.Add(new Line { Y1 = ActualHeight * i / 4, Y2 = ActualHeight * i / 4, X1 = 0, X2 = ActualWidth, Stroke = grid, StrokeThickness = 1 });
        }
        Children.Add(new Line { X1 = 0, Y1 = ActualHeight, X2 = ActualWidth, Y2 = 0, Stroke = grid, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
        var curve = ToneCurve.Parse(Value);
        var pts = new PointCollection();
        int n = Math.Max(16, (int)ActualWidth / 2);
        for (int i = 0; i <= n; i++)
        {
            double x = i / (double)n;
            pts.Add(ToView(new Point(x, Math.Clamp(curve.Evaluate(x), 0, 1))));
        }
        Children.Add(new Polyline { Points = pts, Stroke = Brushes.WhiteSmoke, StrokeThickness = 1.6 });
        foreach (var p in _pts)
        {
            var v = ToView(p);
            var e = new Ellipse { Width = 9, Height = 9, Fill = Brushes.WhiteSmoke, Stroke = Brushes.Black, StrokeThickness = 1 };
            SetLeft(e, v.X - 4.5);
            SetTop(e, v.Y - 4.5);
            Children.Add(e);
        }
    }
}
