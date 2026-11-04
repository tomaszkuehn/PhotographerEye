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
    private static readonly Brush RedSolid = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x5A, 0x5A)));
    private static readonly Brush GreenSolid = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x5A, 0xF0, 0x5A)));
    private static readonly Brush BlueSolid = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x6E, 0x80, 0xFF)));
    private static readonly Brush RedActive = Freeze(new SolidColorBrush(Color.FromArgb(0x9A, 0xFF, 0x3D, 0x3D)));
    private static readonly Brush GreenActive = Freeze(new SolidColorBrush(Color.FromArgb(0x9A, 0x45, 0xFF, 0x45)));
    private static readonly Brush BlueActive = Freeze(new SolidColorBrush(Color.FromArgb(0x9A, 0x4D, 0x5C, 0xFF)));
    private static readonly Brush DimFill = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen LumDimPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0xEC, 0xEC, 0xEC))), 1.0));
    private static readonly Pen DiagPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))), 1.0) { DashStyle = DashStyles.Dash });
    private static readonly Pen RedCurvePen = Freeze(new Pen(RedSolid, 1.6));
    private static readonly Pen GreenCurvePen = Freeze(new Pen(GreenSolid, 1.6));
    private static readonly Pen BlueCurvePen = Freeze(new Pen(BlueSolid, 1.6));
    private static readonly Pen RedThinPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x5A, 0x5A))), 1.0));
    private static readonly Pen GreenThinPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0x5A, 0xF0, 0x5A))), 1.0));
    private static readonly Pen BlueThinPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0x6E, 0x80, 0xFF))), 1.0));

    private int[]? _lum;
    private int[]? _r;
    private int[]? _g;
    private int[]? _b;

    private readonly ManualStretchMap _manual = new();
    private Func<double, double>? _displayToEv;
    private Func<double, double>? _evToDisplay;
    private int _grabIdx = -1;
    private bool _grabLevels;
    private bool _moved;
    private long _lastRaiseTicks;

    private readonly RgbCurves _curves = new();
    private HistChannel _channel = HistChannel.Rgb;

    /// <summary>Luma stretch edited (RGB mode).</summary>
    public event EventHandler? StretchEdited;

    /// <summary>One of the R/G/B curves edited.</summary>
    public event EventHandler? CurvesEdited;

    /// <summary>Channel the mouse edits: RGB = luma stretch, R/G/B = that channel's curve.</summary>
    public HistChannel Channel
    {
        get => _channel;
        set
        {
            if (_channel == value) return;
            _channel = value;
            _grabIdx = -1;
            if (IsMouseCaptured) ReleaseMouseCapture();
            ToolTip = value == HistChannel.Rgb ? LumaTip : CurveTip;
            InvalidateVisual();
        }
    }

    private const string LumaTip = "Drag a point on the histogram — the stretch is converted to Exposure/Highlights/Shadows/Whites/Blacks sliders (double-click — remove points)";
    private const string CurveTip = "Channel curve: click to add a point, drag it up/down. Bottom edge: drag points left/right to stretch this channel's histogram (right-click — remove point, double-click — reset curve or bottom-edge points)";

    /// <summary>Copy of the R/G/B curves, null when none has an effect.</summary>
    public RgbCurves? CurvesSnapshot() => _curves.HasEffect ? _curves.Clone() : null;

    public bool HasCurvesEffect => _curves.HasEffect;

    public double[][] CurvePairs(HistChannel ch) => _curves[ch].ToPairs();

    public double[][] LevelPairs(HistChannel ch) => _curves[ch].LevelPairs();

    public void RestoreCurves(double[][]? r, double[][]? g, double[][]? b,
        double[][]? lr = null, double[][]? lg = null, double[][]? lb = null)
    {
        _curves.R.FromPairs(r);
        _curves.G.FromPairs(g);
        _curves.B.FromPairs(b);
        _curves.R.FromLevelPairs(lr);
        _curves.G.FromLevelPairs(lg);
        _curves.B.FromLevelPairs(lb);
        InvalidateVisual();
    }

    public void ClearCurves()
    {
        if (_curves.R.Count + _curves.G.Count + _curves.B.Count
            + _curves.R.LevelCount + _curves.G.LevelCount + _curves.B.LevelCount == 0) return;
        _curves.Clear();
        InvalidateVisual();
    }

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

    /// <summary>Last histogram set (luma, R, G, B; 256 bins each), or null.</summary>
    public (int[] Lum, int[] R, int[] G, int[] B)? Histogram =>
        _lum is null || _r is null || _g is null || _b is null ? null : (_lum, _r, _g, _b);

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
        ToolTip = LumaTip;
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
        if (_channel != HistChannel.Rgb)
        {
            CurveDown(e);
            return;
        }
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
        if (_channel != HistChannel.Rgb)
        {
            CurveMove(e);
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
            if (_channel == HistChannel.Rgb) StretchEdited?.Invoke(this, EventArgs.Empty);
            else CurvesEdited?.Invoke(this, EventArgs.Empty);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_channel == HistChannel.Rgb || _lum is null) return;
        var c = _curves[_channel];
        var p = e.GetPosition(this);
        int hit = HitCurveKnot(c, p);
        if (hit >= 0)
        {
            c.RemoveAt(hit);
        }
        else
        {
            int lh = InLevelsZone(p) ? HitLevelKnot(c, OutAt(p)) : -1;
            if (lh < 0) return;
            c.RemoveLevelAt(lh);
        }
        _grabIdx = -1;
        InvalidateVisual();
        CurvesEdited?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void RaiseThrottled()
    {
        long now = Environment.TickCount64;
        if (now - _lastRaiseTicks < 30) return;
        _lastRaiseTicks = now;
        if (_channel == HistChannel.Rgb) StretchEdited?.Invoke(this, EventArgs.Empty);
        else CurvesEdited?.Invoke(this, EventArgs.Empty);
    }

    // ---- R/G/B curve editing: x = input level, y = output level (both display space 0..1) ----

    private (double L, double T, double W, double H) PlotRect()
    {
        double w = Math.Max(1.0, ActualWidth - Pad - Pad);
        double h = Math.Max(1.0, ActualHeight - Pad - Pad);
        return (Pad, Pad, w, h);
    }

    private Point CurvePoint(double x, double y)
    {
        var (l, t, w, h) = PlotRect();
        return new Point(l + x * w, t + (1.0 - y) * h);
    }

    private (double X, double Y) CurveCoords(Point p)
    {
        var (l, t, w, h) = PlotRect();
        return (Math.Clamp((p.X - l) / w, 0.0, 1.0), Math.Clamp(1.0 - (p.Y - t) / h, 0.0, 1.0));
    }

    private int HitCurveKnot(ChannelCurve c, Point p)
    {
        int hit = -1;
        double best = 7.0;
        for (int i = 0; i < c.Count; i++)
        {
            var kp = CurvePoint(c.Knots[i].In, c.MapLevels(c.Knots[i].Out));
            double d = Math.Sqrt((kp.X - p.X) * (kp.X - p.X) + (kp.Y - p.Y) * (kp.Y - p.Y));
            if (d < best)
            {
                best = d;
                hit = i;
            }
        }
        return hit;
    }

    /// <summary>Bottom strip of the plot where the levels points live.</summary>
    private bool InLevelsZone(Point p) => p.Y >= ActualHeight - Pad - 10;

    private static int HitLevelKnot(ChannelCurve c, double u)
    {
        int hit = -1;
        double best = 0.02;
        for (int i = 0; i < c.LevelCount; i++)
        {
            double d = Math.Abs(c.Levels[i].Out - u);
            if (d < best)
            {
                best = d;
                hit = i;
            }
        }
        return hit;
    }

    private void CurveDown(MouseButtonEventArgs e)
    {
        var c = _curves[_channel];
        var p = e.GetPosition(this);
        bool levels = InLevelsZone(p) && HitCurveKnot(c, p) < 0;
        if (e.ClickCount == 2)
        {
            if (levels ? c.LevelCount > 0 : c.Count > 0)
            {
                if (levels) c.ClearLevels();
                else c.ClearCurve();
                _grabIdx = -1;
                InvalidateVisual();
                CurvesEdited?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
            return;
        }
        _grabLevels = levels;
        if (levels)
        {
            double u = OutAt(p);
            int lh = HitLevelKnot(c, u);
            _grabIdx = lh >= 0 ? lh : c.UpsertLevel(c.InverseLevels(u), u);
            _moved = false;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        int hit = HitCurveKnot(c, p);
        if (hit >= 0)
        {
            _grabIdx = hit;
        }
        else
        {
            var (x, _) = CurveCoords(p);
            _grabIdx = c.Upsert(x, c.MapCurve(x));
        }
        _moved = false;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    private void CurveMove(MouseEventArgs e)
    {
        var c = _curves[_channel];
        if (_grabLevels)
        {
            if (_grabIdx >= c.LevelCount)
            {
                _grabIdx = -1;
                return;
            }
            double lb = c.Levels[_grabIdx].Out;
            c.SetLevelOut(_grabIdx, OutAt(e.GetPosition(this)));
            if (Math.Abs(c.Levels[_grabIdx].Out - lb) < 1e-4) return;
            _moved = true;
            InvalidateVisual();
            RaiseThrottled();
            e.Handled = true;
            return;
        }
        if (_grabIdx >= c.Count)
        {
            _grabIdx = -1;
            return;
        }
        var (_, y) = CurveCoords(e.GetPosition(this));
        double before = c.Knots[_grabIdx].Out;
        c.SetOut(_grabIdx, c.InverseLevels(y));
        if (Math.Abs(c.Knots[_grabIdx].Out - before) < 1e-4) return;
        _moved = true;
        InvalidateVisual();
        RaiseThrottled();
        e.Handled = true;
    }

    private StreamGeometry CurveGeometry(ChannelCurve c)
    {
        var t = c.Table(97);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(CurvePoint(0, t[0]), false, false);
            for (int i = 1; i < t.Length; i++)
                ctx.LineTo(CurvePoint(i / (double)(t.Length - 1), t[i]), true, false);
        }
        geo.Freeze();
        return geo;
    }

    private static Pen CurvePen(HistChannel ch, bool active) => ch switch
    {
        HistChannel.R => active ? RedCurvePen : RedThinPen,
        HistChannel.G => active ? GreenCurvePen : GreenThinPen,
        _ => active ? BlueCurvePen : BlueThinPen
    };

    private static Brush SolidOf(HistChannel ch) => ch switch
    {
        HistChannel.R => RedSolid,
        HistChannel.G => GreenSolid,
        _ => BlueSolid
    };

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

        if (_channel == HistChannel.Rgb)
        {
            DrawFill(dc, _b, max, pl, plotW, plotH, yb, BlueFill);
            DrawFill(dc, _g, max, pl, plotW, plotH, yb, GreenFill);
            DrawFill(dc, _r, max, pl, plotW, plotH, yb, RedFill);
            dc.DrawGeometry(null, LumPen, Poly(_lum, max, pl, plotW, plotH, yb, closed: false));

            // Channel curves in use stay visible (thin) while editing the luma stretch.
            foreach (var ch in new[] { HistChannel.R, HistChannel.G, HistChannel.B })
                if (_curves[ch].HasEffect) dc.DrawGeometry(null, CurvePen(ch, false), CurveGeometry(_curves[ch]));

            foreach (var k in _manual.Knots)
            {
                double mx = pl + EvToDisplay(k.Out) * plotW;
                dc.DrawEllipse(KnotFill, KnotPen, new Point(mx, yb - 2), 4, 4);
            }
            return;
        }

        // Channel mode: the selected channel's histogram stands out, the others are dimmed.
        var (data, fill) = _channel switch
        {
            HistChannel.R => (_r, RedActive),
            HistChannel.G => (_g, GreenActive),
            _ => (_b, BlueActive)
        };
        foreach (var other in new[] { _r, _g, _b })
            if (!ReferenceEquals(other, data)) DrawFill(dc, other, max, pl, plotW, plotH, yb, DimFill);
        DrawFill(dc, data, max, pl, plotW, plotH, yb, fill);
        dc.DrawGeometry(null, LumDimPen, Poly(_lum, max, pl, plotW, plotH, yb, closed: false));

        dc.DrawLine(DiagPen, CurvePoint(0, 0), CurvePoint(1, 1));
        foreach (var ch in new[] { HistChannel.R, HistChannel.G, HistChannel.B })
            if (ch != _channel && _curves[ch].HasEffect) dc.DrawGeometry(null, CurvePen(ch, false), CurveGeometry(_curves[ch]));
        var cur = _curves[_channel];
        dc.DrawGeometry(null, CurvePen(_channel, true), CurveGeometry(cur));
        var kb = SolidOf(_channel);
        for (int i = 0; i < cur.Count; i++)
        {
            var k = cur.Knots[i];
            dc.DrawEllipse(i == _grabIdx && !_grabLevels ? KnotFill : kb, KnotPen, CurvePoint(k.In, cur.MapLevels(k.Out)), 4, 4);
        }
        for (int i = 0; i < cur.LevelCount; i++)
        {
            double mx = pl + cur.Levels[i].Out * plotW;
            dc.DrawEllipse(i == _grabIdx && _grabLevels ? KnotFill : kb, KnotPen, new Point(mx, yb - 2), 4, 4);
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