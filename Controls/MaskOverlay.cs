using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ImgViewer.Imaging;
using ImgViewer.Services;

namespace ImgViewer.Controls;

/// <summary>
/// On-image editor for local masks (linear gradient, radial, brush) and a tinted mask preview.
/// Mask coordinates are fractions of the un-rotated, un-cropped photo; the overlay maps them through the
/// current display geometry so handles stay on the content.
/// </summary>
public sealed class MaskOverlay : Canvas
{
    private const double HandleR = 6;

    private ImageViewer? _viewer;
    private GeoState _geo = GeoState.Default;
    private double _srcW = 1, _srcH = 1;
    private MaskSpec? _spec;
    private BitmapSource? _preview;
    private int _drag = -1;
    private Point _dragStartSrc;
    private double[]? _dragStartP;
    private List<double>? _stroke;
    private Point _mouse;
    private bool _mouseInside;

    /// <summary>Raised while dragging (final = false) and on mouse up (final = true).</summary>
    public event Action<MaskSpec, bool>? SpecChanged;

    /// <summary>Brush radius as a fraction of the photo width.</summary>
    public double BrushRadius { get; set; } = 0.04;
    public bool BrushErase { get; set; }
    /// <summary>When false only the preview is shown and the mouse passes through to the viewer.</summary>
    public bool Editing { get; private set; }
    public bool IsInteracting => _drag >= 0 || _stroke is not null;

    public MaskOverlay()
    {
        Focusable = false;
        ClipToBounds = true;
        IsHitTestVisible = false;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseLeave += (_, _) => { _mouseInside = false; Redraw(); };
        MouseWheel += (_, e) =>
        {
            if (_viewer is null) return;
            e.Handled = true;
            _viewer.WheelFrom(e.GetPosition(this), e.Delta);
        };
        SizeChanged += (_, _) => Redraw();
    }

    public void Attach(ImageViewer viewer)
    {
        _viewer = viewer;
        viewer.ViewChanged += (_, _) => { if (Visibility == Visibility.Visible) Redraw(); };
    }

    public void SetMapping(GeoState displayGeo, double srcW, double srcH)
    {
        _geo = displayGeo;
        _srcW = Math.Max(1, srcW);
        _srcH = Math.Max(1, srcH);
        Redraw();
    }

    public void SetSpec(MaskSpec? spec, bool editing)
    {
        _spec = spec;
        Editing = editing && spec is not null;
        IsHitTestVisible = Editing;
        Background = Editing ? Brushes.Transparent : null;
        Cursor = Editing && spec?.Kind == "brush" ? Cursors.None : Cursors.Arrow;
        Redraw();
    }

    /// <summary>Tinted mask preview, already in display geometry (same size ratio as the displayed image).</summary>
    public void SetPreview(BitmapSource? preview)
    {
        _preview = preview;
        Redraw();
    }

    public void Clear()
    {
        _spec = null;
        _preview = null;
        Editing = false;
        IsHitTestVisible = false;
        Background = null;
        _drag = -1;
        _stroke = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Redraw();
    }

    // ------------------------------------------------------------------
    //  Mapping
    // ------------------------------------------------------------------

    private Rect Frame => _viewer?.ImageRect ?? Rect.Empty;

    private Point ToView(double u, double v)
    {
        var f = Frame;
        var o = PhotoGeo.MapToOutput(_geo, _srcW, _srcH, new Point(u, v));
        return new Point(f.X + o.X * f.Width, f.Y + o.Y * f.Height);
    }

    private Point ToSrc(Point view)
    {
        var f = Frame;
        if (f.Width < 1 || f.Height < 1) return new Point(0.5, 0.5);
        return PhotoGeo.MapFromOutput(_geo, _srcW, _srcH, new Point((view.X - f.X) / f.Width, (view.Y - f.Y) / f.Height));
    }

    /// <summary>DIPs per photo-width fraction (for brush radius display).</summary>
    private double DipPerSrcWidth
    {
        get
        {
            var f = Frame;
            double outW = PhotoGeo.OutputWidth(_geo, _srcW, _srcH);
            return outW > 0 ? f.Width / outW * _srcW : 0;
        }
    }

    private static double P(MaskSpec s, int i, double def) => s.P is not null && s.P.Length > i ? s.P[i] : def;

    private double[] Params(int n, params double[] defs)
    {
        var p = new double[n];
        for (int i = 0; i < n; i++) p[i] = _spec is null ? defs[i] : P(_spec, i, defs[i]);
        return p;
    }

    private List<Point> Handles()
    {
        var list = new List<Point>();
        if (_spec is null) return list;
        switch (_spec.Kind)
        {
            case "linear":
            {
                var p = Params(4, 0.5, 0.25, 0.5, 0.6);
                list.Add(ToView(p[0], p[1]));
                list.Add(ToView(p[2], p[3]));
                break;
            }
            case "radial":
            {
                var p = Params(5, 0.5, 0.5, 0.3, 0.3, 0.5);
                list.Add(ToView(p[0], p[1]));
                list.Add(ToView(p[0] + p[2], p[1]));
                list.Add(ToView(p[0], p[1] + p[3]));
                break;
            }
        }
        return list;
    }

    // ------------------------------------------------------------------
    //  Drawing
    // ------------------------------------------------------------------

    private static readonly Brush Light = Frozen(Color.FromArgb(235, 240, 240, 245));
    private static readonly Brush Dark = Frozen(Color.FromArgb(200, 20, 22, 28));
    private static readonly Brush Accent = Frozen(Color.FromArgb(255, 255, 120, 90));
    private static readonly Brush StrokeFill = Frozen(Color.FromArgb(70, 255, 80, 60));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void Redraw()
    {
        Children.Clear();
        var f = Frame;
        if (f.IsEmpty || f.Width < 2) return;

        if (_preview is not null)
        {
            var img = new Image { Source = _preview, Width = f.Width, Height = f.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
            SetLeft(img, f.X);
            SetTop(img, f.Y);
            Children.Add(img);
        }
        if (_spec is null || !Editing) return;

        switch (_spec.Kind)
        {
            case "linear":
                DrawLinear();
                break;
            case "radial":
                DrawRadial();
                break;
            case "brush":
                DrawBrush();
                break;
        }
        foreach (var h in Handles()) AddHandle(h);
    }

    private void AddLine(Point a, Point b, Brush stroke, double thickness, bool dashed = false)
    {
        var l = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = stroke, StrokeThickness = thickness, IsHitTestVisible = false };
        if (dashed) l.StrokeDashArray = new DoubleCollection { 4, 3 };
        Children.Add(l);
    }

    private void AddHandle(Point p)
    {
        var e = new Ellipse { Width = HandleR * 2, Height = HandleR * 2, Fill = Light, Stroke = Dark, StrokeThickness = 1.5, IsHitTestVisible = false };
        SetLeft(e, p.X - HandleR);
        SetTop(e, p.Y - HandleR);
        Children.Add(e);
    }

    private void DrawLinear()
    {
        var h = Handles();
        Point a = h[0], b = h[1];
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3) return;
        double nx = -dy / len, ny = dx / len;
        double L = Math.Max(ActualWidth, ActualHeight) * 2;
        foreach (var (p, dash) in new[] { (a, false), (b, true) })
        {
            var p1 = new Point(p.X - nx * L, p.Y - ny * L);
            var p2 = new Point(p.X + nx * L, p.Y + ny * L);
            AddLine(p1, p2, Dark, 3, dash);
            AddLine(p1, p2, Light, 1.2, dash);
        }
        AddLine(a, b, Accent, 1.5);
    }

    private void DrawRadial()
    {
        var p = Params(5, 0.5, 0.5, 0.3, 0.3, 0.5);
        foreach (var (scale, dash) in new[] { (1.0, false), (Math.Max(0.0, 1 - p[4]), true) })
        {
            if (scale <= 0.001) continue;
            var pts = new PointCollection();
            for (int i = 0; i <= 72; i++)
            {
                double a = i * Math.PI * 2 / 72;
                pts.Add(ToView(p[0] + Math.Cos(a) * p[2] * scale, p[1] + Math.Sin(a) * p[3] * scale));
            }
            Children.Add(new Polyline { Points = pts, Stroke = Dark, StrokeThickness = 3, IsHitTestVisible = false });
            var pl = new Polyline { Points = pts, Stroke = Light, StrokeThickness = 1.2, IsHitTestVisible = false };
            if (dash) pl.StrokeDashArray = new DoubleCollection { 4, 3 };
            Children.Add(pl);
        }
    }

    private void DrawBrush()
    {
        double k = DipPerSrcWidth;
        var strokes = (_spec?.Strokes ?? new List<double[]>()).ToList();
        if (_stroke is not null) strokes.Add(_stroke.ToArray());
        foreach (var st in strokes)
        {
            if (st is null || st.Length < 4) continue;
            var pts = new PointCollection();
            for (int i = 2; i + 1 < st.Length; i += 2) pts.Add(ToView(st[i], st[i + 1]));
            if (pts.Count == 1) pts.Add(new Point(pts[0].X + 0.01, pts[0].Y));
            bool paint = st[0] >= 0.5;
            Children.Add(new Polyline
            {
                Points = pts,
                Stroke = paint ? StrokeFill : Frozen(Color.FromArgb(60, 80, 160, 255)),
                StrokeThickness = Math.Max(1, st[1] * 2 * k),
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            });
        }
        if (_mouseInside)
        {
            double r = BrushRadius * k;
            foreach (var (brush, th) in new[] { (Dark, 3.0), (BrushErase ? Frozen(Color.FromArgb(255, 120, 180, 255)) : Light, 1.2) })
            {
                var c = new Ellipse { Width = r * 2, Height = r * 2, Stroke = brush, StrokeThickness = th, IsHitTestVisible = false };
                SetLeft(c, _mouse.X - r);
                SetTop(c, _mouse.Y - r);
                Children.Add(c);
            }
        }
    }

    // ------------------------------------------------------------------
    //  Mouse
    // ------------------------------------------------------------------

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (!Editing || _spec is null) return;
        var pos = e.GetPosition(this);
        e.Handled = true;
        if (_spec.Kind == "brush")
        {
            var s = ToSrc(pos);
            bool erase = BrushErase || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            _stroke = new List<double> { erase ? 0 : 1, BrushRadius, s.X, s.Y };
            CaptureMouse();
            Redraw();
            return;
        }
        var hs = Handles();
        _drag = -1;
        for (int i = 0; i < hs.Count; i++)
            if ((hs[i] - pos).Length <= HandleR + 4) { _drag = i; break; }
        if (_drag < 0) _drag = 100; // move whole shape
        _dragStartSrc = ToSrc(pos);
        _dragStartP = _spec.Kind == "linear" ? Params(4, 0.5, 0.25, 0.5, 0.6) : Params(5, 0.5, 0.5, 0.3, 0.3, 0.5);
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        _mouseInside = true;
        if (_spec is null || !Editing) return;
        if (_stroke is not null)
        {
            var s = ToSrc(_mouse);
            int n = _stroke.Count;
            double k = DipPerSrcWidth;
            double ddx = (s.X - _stroke[n - 2]) * k, ddy = (s.Y - _stroke[n - 1]) * k * _srcH / _srcW;
            if (ddx * ddx + ddy * ddy >= 4)
            {
                _stroke.Add(s.X);
                _stroke.Add(s.Y);
            }
            Redraw();
            return;
        }
        if (_spec.Kind == "brush")
        {
            Redraw();
            return;
        }
        if (_drag < 0 || _dragStartP is null) return;
        var cur = ToSrc(_mouse);
        double dx = cur.X - _dragStartSrc.X, dy = cur.Y - _dragStartSrc.Y;
        var p = (double[])_dragStartP.Clone();
        if (_spec.Kind == "linear")
        {
            if (_drag == 0) { p[0] = cur.X; p[1] = cur.Y; }
            else if (_drag == 1) { p[2] = cur.X; p[3] = cur.Y; }
            else { p[0] += dx; p[1] += dy; p[2] += dx; p[3] += dy; }
        }
        else
        {
            if (_drag == 0 || _drag == 100) { p[0] += dx; p[1] += dy; }
            else
            {
                // Radius handles: distance from the center in source fractions, independent of rotation.
                double rx = (cur.X - p[0]), ry = (cur.Y - p[1]);
                double dist = Math.Sqrt(rx * rx + ry * ry * (_srcH * _srcH) / (_srcW * _srcW));
                if (_drag == 1) p[2] = Math.Clamp(dist, 0.01, 2.0);
                else p[3] = Math.Clamp(dist * _srcW / _srcH, 0.01, 2.0);
            }
        }
        _spec = _spec with { P = p };
        Redraw();
        SpecChanged?.Invoke(_spec, false);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_spec is null) return;
        if (_stroke is not null)
        {
            var strokes = _spec.Strokes is null ? new List<double[]>() : new List<double[]>(_spec.Strokes);
            strokes.Add(_stroke.ToArray());
            _stroke = null;
            _spec = _spec with { Strokes = strokes };
            Redraw();
            SpecChanged?.Invoke(_spec, true);
            return;
        }
        if (_drag >= 0)
        {
            _drag = -1;
            _dragStartP = null;
            SpecChanged?.Invoke(_spec, true);
        }
    }
}
