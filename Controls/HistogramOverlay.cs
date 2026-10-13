using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ImgViewer.Imaging;

namespace ImgViewer.Controls;

public sealed class HistogramOverlay : FrameworkElement
{
    private const double Pad = 7.0;

    private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0x14, 0x14, 0x14)));
    private static readonly Brush BorderBrushH = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush RedFill = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0x3D, 0x3D)));
    private static readonly Brush GreenFill = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0x45, 0xFF, 0x45)));
    private static readonly Brush BlueFill = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0x4D, 0x5C, 0xFF)));
    private static readonly Brush LumStroke = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0xEC, 0xEC, 0xEC)));
    private static readonly Brush KnotFill = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush KnotBorder = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x33, 0x44)));

    private static readonly Pen BorderPen = Freeze(new Pen(BorderBrushH, 1.0));
    private static readonly Pen LumPen = Freeze(new Pen(LumStroke, 1.2));
    private static readonly Pen KnotPen = Freeze(new Pen(KnotBorder, 1.5));

    private int[]? _lum;
    private int[]? _r;
    private int[]? _g;
    private int[]? _b;

    private readonly ManualStretchMap _manual = new();
    private Func<double, double>? _displayToEv;
    private Func<double, double>? _evToDisplay;
    private int _grabIdx = -1;
    private bool _moved;
    private long _lastRaiseTicks;

    public event EventHandler? StretchEdited;

    public ManualStretchMap Snapshot() => _manual.Clone();

    public bool HasManualEffect => _manual.HasEffect;

    public bool IsDragging => _grabIdx >= 0 && IsMouseCaptured;

    public void SetEvConverters(Func<double, double>? displayToEv, Func<double, double>? evToDisplay)
    {
        _displayToEv = displayToEv;
        _evToDisplay = evToDisplay;
        InvalidateVisual();
    }

    private double DisplayToEv(double u) =>
        _displayToEv?.Invoke(u) ?? PhotoOps.DisplayFractionToEv(u, 1.0);

    private double EvToDisplay(double ev) =>
        _evToDisplay?.Invoke(ev) ?? PhotoOps.EvToDisplayFraction(ev, 1.0);

    public void ClearManualStretch()
    {
        if (_manual.Count == 0) return;
        _manual.Clear();
        InvalidateVisual();
    }

    public void RestoreManualStretch(IEnumerable<StretchKnot> knots)
    {
        _manual.Clear();
        foreach (var k in knots)
            _manual.Upsert(k.In, k.Out);
        InvalidateVisual();
    }

    public void SetHistogram(int[] lum, int[] r, int[] g, int[] b)
    {
        _lum = lum;
        _r = r;
        _g = g;
        _b = b;
        InvalidateVisual();
    }

    public HistogramOverlay()
    {
        SnapsToDevicePixels = true;
        ToolTip = "Drag a point on the histogram — the stretch is converted to Exposure/Highlights/Shadows/Whites/Blacks sliders (double-click — remove points)";
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.UIElementAutomationPeer(this);

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static Pen Freeze(Pen p)
    {
        p.Freeze();
        return p;
    }

    protected override void OnMouseEnter(MouseEventArgs e) => Cursor = Cursors.Hand;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_lum is null) return;
        double u = OutAt(e.GetPosition(this));

        if (e.ClickCount == 2)
        {
            if (_manual.Count > 0)
            {
                _manual.Clear();
                _grabIdx = -1;
                InvalidateVisual();
                StretchEdited?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
            return;
        }

        int hit = -1;
        double best = 0.02;
        for (int i = 0; i < _manual.Count; i++)
        {
            double d = Math.Abs(EvToDisplay(_manual.Knots[i].Out) - u);
            if (d < best)
            {
                best = d;
                hit = i;
            }
        }
        if (hit >= 0)
        {
            _grabIdx = hit;
        }
        else
        {
            double evOut = DisplayToEv(u);
            double evIn = _manual.Inverse(evOut);
            _grabIdx = _manual.Upsert(evIn, evOut);
            _moved = false;
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_grabIdx < 0 || !IsMouseCaptured) return;
        if (_lum is null)
        {
            _grabIdx = -1;
            return;
        }
        if (_grabIdx >= _manual.Count)
        {
            _grabIdx = -1;
            return;
        }
        double u = OutAt(e.GetPosition(this));
        var knots = _manual.Knots;
        double lo = _grabIdx > 0 ? knots[_grabIdx - 1].Out : PhotoOps.ToneEvMin;
        double hi = _grabIdx < knots.Count - 1 ? knots[_grabIdx + 1].Out : PhotoOps.ToneEvMax;
        double newOut = Math.Clamp(DisplayToEv(u), lo, hi);
        var k = knots[_grabIdx];
        if (Math.Abs(newOut - k.Out) < 1e-4) return;
        _moved = true;
        _manual.SetOut(_grabIdx, newOut);
        InvalidateVisual();
        RaiseThrottled();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_grabIdx < 0) return;
        _grabIdx = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_moved)
        {
            _moved = false;
            StretchEdited?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private void RaiseThrottled()
    {
        long now = Environment.TickCount64;
        if (now - _lastRaiseTicks < 30) return;
        _lastRaiseTicks = now;
        StretchEdited?.Invoke(this, EventArgs.Empty);
    }

    private double OutAt(Point p)
    {
        double plotW = Math.Max(1.0, ActualWidth - Pad - Pad);
        double u = (p.X - Pad) / plotW;
        return Math.Clamp(u, 0.0, 1.0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 4 || h <= 4) return;

        var rect = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRoundedRectangle(BgBrush, BorderPen, rect, 8, 8);

        if (_lum is null || _r is null || _g is null || _b is null) return;

        double pl = Pad, pt = 7, pb = 7, pr = Pad;
        double plotW = w - pl - pr;
        double plotH = h - pt - pb;
        double yb = h - pb;

        double max = MaxScale(_lum, _r, _g, _b);

        DrawFill(dc, _b, max, pl, plotW, plotH, yb, BlueFill);
        DrawFill(dc, _g, max, pl, plotW, plotH, yb, GreenFill);
        DrawFill(dc, _r, max, pl, plotW, plotH, yb, RedFill);
        dc.DrawGeometry(null, LumPen, Poly(_lum, max, pl, plotW, plotH, yb, closed: false));

        foreach (var k in _manual.Knots)
        {
            double mx = pl + EvToDisplay(k.Out) * plotW;
            dc.DrawEllipse(KnotFill, KnotPen, new Point(mx, yb - 2), 4, 4);
        }
    }

    private static double MaxScale(params int[][] arrays)
    {
        double best = 1;
        foreach (var a in arrays)
        {
            var sorted = (int[])a.Clone();
            Array.Sort(sorted);
            int idx = (int)Math.Floor(0.99 * (sorted.Length - 1));
            int cap = Math.Max(sorted[idx], 1);
            if (cap > best) best = cap;
        }
        return best;
    }

    private static void DrawFill(DrawingContext dc, int[] data, double max, double x0, double plotW, double plotH, double yb, Brush brush)
    {
        dc.DrawGeometry(brush, null, Poly(data, max, x0, plotW, plotH, yb, closed: true));
    }

    private static StreamGeometry Poly(int[] data, double max, double x0, double plotW, double plotH, double yb, bool closed)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            int n = data.Length;
            double step = plotW / (n - 1);
            double Y(int v) => yb - Math.Min(1.0, v / max) * plotH;
            if (closed)
            {
                ctx.BeginFigure(new Point(x0, yb), true, true);
                for (int i = 0; i < n; i++)
                    ctx.LineTo(new Point(x0 + i * step, Y(data[i])), true, false);
                ctx.LineTo(new Point(x0 + plotW, yb), true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(x0, Y(data[0])), false, false);
                for (int i = 1; i < n; i++)
                    ctx.LineTo(new Point(x0 + i * step, Y(data[i])), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }
}