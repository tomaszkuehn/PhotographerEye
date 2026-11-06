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
    private MaskSpec? _highlightSpec;
    private BitmapSource? _highlight;
    private int _drag = -1;
    private Point _dragStartSrc;
    private double[]? _dragStartP;
    private List<double>? _stroke;
    private Point _mouse;
    private bool _mouseInside;

    /// <summary>Raised while dragging (final = false) and on mouse up (final = true).</summary>
    public event Action<MaskSpec, bool>? SpecChanged;

    /// <summary>Marker of a mask on the photo (<see cref="SetPins"/>); Src in source fractions.</summary>
    public sealed record MaskPin(string Key, string Label, MaskSpec Spec, Point Src);

    /// <summary>Clicked marker: the masks under it (several when markers overlap) and the click position in the overlay.</summary>
    public event Action<IReadOnlyList<MaskPin>, Point>? PinClicked;

    private const double PinR = 7;
    private const double PinMerge = 16;
    private List<MaskPin> _pins = new();
    private bool _pinsVisible;
    private int _hoverPin = -1;
    private bool _hoverOuter;
    private List<(Point At, List<MaskPin> Pins)> _clusters = new();

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
        MouseLeave += (_, _) => { _mouseInside = false; _hoverPin = -1; _hoverOuter = false; UpdateCursor(); Redraw(); };
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
        UpdateHitTest();
        Redraw();
    }

    /// <summary>False while the mask colour is off (the O toggle): the edited brush strokes draw as thin centre lines
    /// instead of the thick area marks, so the photo shows the result clean — the outline stays traceable, like the
    /// thin ellipse of an edited radial mask.</summary>
    public bool ShowEditFill { get; set; } = true;

    /// <summary>
    /// Markers of the photo's other masks; clicking one raises <see cref="PinClicked"/>. With no mask being edited
    /// only the markers take the mouse, the rest of the photo still pans and zooms.
    /// </summary>
    public void SetPins(IReadOnlyList<MaskPin> pins, bool visible)
    {
        _pins = pins.ToList();
        _pinsVisible = visible;
        if (!PinsShown) _hoverPin = -1;
        UpdateHitTest();
        Redraw();
    }

    /// <summary>Number of markers drawn now (test hook and status).</summary>
    public int PinCount => PinsShown ? _pins.Count : 0;

    private bool PinsShown => _pinsVisible && _pins.Count > 0;

    private void UpdateHitTest()
    {
        IsHitTestVisible = Editing || PinsShown;
        Background = Editing ? Brushes.Transparent : null;
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        Cursor = _hoverPin >= 0 ? Cursors.Hand
            : _mouseInside && _hoverOuter ? Cursors.Hand
            : Editing && _spec?.Kind == "brush" ? Cursors.None : Cursors.Arrow;
    }

    /// <summary>Without a background only the markers are hit (the photo under the rest keeps panning).</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        if (Editing) return base.HitTestCore(hitTestParameters);
        return PinAt(hitTestParameters.HitPoint) >= 0 ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;
    }

    /// <summary>Markers grouped in view coordinates: ones closer than <see cref="PinMerge"/> share one marker.</summary>
    private List<(Point At, List<MaskPin> Pins)> BuildClusters()
    {
        var list = new List<(Point At, List<MaskPin> Pins)>();
        if (!PinsShown || Frame.IsEmpty) return list;
        foreach (var pin in _pins)
        {
            var v = ToView(pin.Src.X, pin.Src.Y);
            int hit = list.FindIndex(c => (c.At - v).Length < PinMerge);
            if (hit >= 0) list[hit].Pins.Add(pin);
            else list.Add((v, new List<MaskPin> { pin }));
        }
        return list;
    }

    private int PinAt(Point view)
    {
        for (int i = _clusters.Count - 1; i >= 0; i--)
            if ((_clusters[i].At - view).Length <= PinR + 3) return i;
        return -1;
    }

    /// <summary>Tinted mask preview, already in display geometry (same size ratio as the displayed image).</summary>
    public void SetPreview(BitmapSource? preview)
    {
        _preview = preview;
        Redraw();
    }

    /// <summary>
    /// Another mask shown on the photo for a moment (e.g. the mask under the pointer in the link menu): its outline and,
    /// when given, its tinted area (display geometry, like <see cref="SetPreview"/>). Null spec hides it.
    /// </summary>
    public void SetHighlight(MaskSpec? spec, BitmapSource? tint)
    {
        _highlightSpec = spec;
        _highlight = spec is null ? null : tint;
        Redraw();
    }

    public bool HasHighlight => _highlightSpec is not null;

    public void Clear()
    {
        _spec = null;
        _preview = null;
        Editing = false;
        _hoverPin = -1;
        UpdateHitTest();
        _drag = -1;
        _stroke = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Redraw();
    }

    // ------------------------------------------------------------------
    //  Mapping
    // ------------------------------------------------------------------

    private Rect Frame => _viewer?.ImageRect ?? Rect.Empty;

    /// <summary>
    /// Centre of the part of the photo visible in the viewer, as source fractions (0..1); the photo centre when
    /// the whole photo is visible or the view is not laid out yet. Call <see cref="SetMapping"/> first.
    /// </summary>
    public Point VisibleCenterSrc()
    {
        var f = Frame;
        if (_viewer is null || f.Width < 1 || f.Height < 1) return new Point(0.5, 0.5);
        var vis = Rect.Intersect(f, new Rect(0, 0, _viewer.ActualWidth, _viewer.ActualHeight));
        if (vis.IsEmpty || vis.Width < 1 || vis.Height < 1) return new Point(0.5, 0.5);
        var c = ToSrc(new Point(vis.X + vis.Width / 2, vis.Y + vis.Height / 2));
        return new Point(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1));
    }

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

    private const double RotateGap = 22;
    private const int RotateHandle = 3;
    /// <summary>Drag of the outer outline of a radial mask: tunes the feather, the inner ellipse stays fixed.</summary>
    private const int FeatherDrag = 101;
    private const double OutlineGrab = 7;

    private double[] RadialParams() => Params(6, 0.5, 0.5, 0.3, 0.3, 0.5, 0);

    /// <summary>Scale of the inner (dashed, fully covered) ellipse of a radial mask: 1 - feather, kept positive.</summary>
    private static double RadialInner(double[] p) => Math.Max(0.001, 1 - Math.Clamp(p[4], 0.0, 1.0));

    /// <summary>Point of the radial ellipse at (a, b) along its own axes (1, 0 = end of the first radius), in view coordinates.</summary>
    private Point RadialPoint(double[] p, double a, double b)
    {
        double th = p[5] * Math.PI / 180, cos = Math.Cos(th), sin = Math.Sin(th);
        double ux = a * p[2] * _srcW, vy = b * p[3] * _srcH;
        double px = ux * cos - vy * sin, py = ux * sin + vy * cos;
        return ToView(p[0] + px / _srcW, p[1] + py / _srcH);
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
                // Center, the two radius handles on the (rotated) axes of the inner ellipse — they size the covered
                // core, the outer outline is the feather grip — and the rotation handle past the top of the outer edge.
                var p = RadialParams();
                double inner = RadialInner(p);
                var c = ToView(p[0], p[1]);
                list.Add(c);
                list.Add(RadialPoint(p, inner, 0));
                list.Add(RadialPoint(p, 0, inner));
                var top = RadialPoint(p, 0, -1);
                var dir = top - c;
                if (dir.Length < 1e-3) dir = new Vector(0, -1);
                dir.Normalize();
                list.Add(top + dir * RotateGap);
                break;
            }
        }
        return list;
    }

    /// <summary>True when the view point lies on the outer (solid) outline of the radial mask being edited — the feather grip.</summary>
    private bool NearRadialOuter(Point view)
    {
        if (_spec is null || _spec.Kind != "radial") return false;
        var p = RadialParams();
        Point prev = RadialPoint(p, 1, 0);
        for (int i = 1; i <= 72; i++)
        {
            double a = i * Math.PI * 2 / 72;
            var q = RadialPoint(p, Math.Cos(a), Math.Sin(a));
            if (DistToSeg(view, prev, q) <= OutlineGrab) return true;
            prev = q;
        }
        return false;
    }

    private static double DistToSeg(Point pt, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return (pt - a).Length;
        double t = Math.Clamp(((pt.X - a.X) * dx + (pt.Y - a.Y) * dy) / len2, 0, 1);
        double x = a.X + t * dx - pt.X, y = a.Y + t * dy - pt.Y;
        return Math.Sqrt(x * x + y * y);
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
        if (_highlight is not null)
        {
            var img = new Image { Source = _highlight, Width = f.Width, Height = f.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
            SetLeft(img, f.X);
            SetTop(img, f.Y);
            Children.Add(img);
        }
        if (_highlightSpec is not null) DrawOutline(_highlightSpec);
        _clusters = BuildClusters();
        if (_hoverPin >= _clusters.Count) _hoverPin = -1;
        if (_spec is not null && Editing)
        {
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
            var hs = Handles();
            for (int i = 0; i < hs.Count; i++) AddHandle(hs[i], _spec.Kind == "radial" && i == RotateHandle);
        }
        DrawPins();
    }

    private static readonly Brush PinFill = Frozen(Color.FromArgb(215, 30, 32, 38));
    private static readonly Brush PinHoverFill = Frozen(Color.FromArgb(255, 255, 120, 90));
    private static readonly Brush OutlineBrush = Frozen(Color.FromArgb(230, 255, 200, 120));
    private static readonly Brush OutlinePaint = Frozen(Color.FromArgb(70, 255, 200, 120));

    private void DrawPins()
    {
        if (_hoverPin >= 0)
            foreach (var pin in _clusters[_hoverPin].Pins) DrawOutline(pin.Spec);
        for (int i = 0; i < _clusters.Count; i++)
        {
            var (at, pins) = _clusters[i];
            bool hover = i == _hoverPin;
            var e = new Ellipse { Width = PinR * 2, Height = PinR * 2, Fill = hover ? PinHoverFill : PinFill, Stroke = Light, StrokeThickness = 2, IsHitTestVisible = false };
            SetLeft(e, at.X - PinR);
            SetTop(e, at.Y - PinR);
            Children.Add(e);
            if (pins.Count > 1)
            {
                var n = new TextBlock { Text = pins.Count.ToString(), Foreground = Light, FontSize = 9, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
                n.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                SetLeft(n, at.X - n.DesiredSize.Width / 2);
                SetTop(n, at.Y - n.DesiredSize.Height / 2);
                Children.Add(n);
            }
            else
            {
                var d = new Ellipse { Width = 4, Height = 4, Fill = Light, IsHitTestVisible = false };
                SetLeft(d, at.X - 2);
                SetTop(d, at.Y - 2);
                Children.Add(d);
            }
        }
        if (_hoverPin < 0) return;
        var (pa, list) = _clusters[_hoverPin];
        var text = string.Join("\n", list.Select(p => p.Label)) + (list.Count > 1 ? "\nClick to choose which one to edit" : "\nClick to edit this mask");
        var label = new Border
        {
            Background = PinFill,
            BorderBrush = Light,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = text, Foreground = Light, FontSize = 11 }
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lx = pa.X + PinR + 6, ly = pa.Y - label.DesiredSize.Height / 2;
        if (lx + label.DesiredSize.Width > ActualWidth - 4) lx = pa.X - PinR - 6 - label.DesiredSize.Width;
        ly = Math.Clamp(ly, 4, Math.Max(4, ActualHeight - label.DesiredSize.Height - 4));
        SetLeft(label, lx);
        SetTop(label, ly);
        Children.Add(label);
    }

    /// <summary>Thin outline of another mask (shown while its marker is hovered).</summary>
    private void DrawOutline(MaskSpec spec)
    {
        double P(int i, double def) => spec.P is not null && spec.P.Length > i && double.IsFinite(spec.P[i]) ? spec.P[i] : def;
        switch (spec.Kind)
        {
            case "radial":
            {
                var p = new[] { P(0, 0.5), P(1, 0.5), P(2, 0.3), P(3, 0.3), P(4, 0.5), P(5, 0) };
                var pts = new PointCollection();
                for (int i = 0; i <= 72; i++)
                {
                    double a = i * Math.PI * 2 / 72;
                    pts.Add(RadialPoint(p, Math.Cos(a), Math.Sin(a)));
                }
                Children.Add(new Polyline { Points = pts, Stroke = Dark, StrokeThickness = 3, IsHitTestVisible = false });
                Children.Add(new Polyline { Points = pts, Stroke = OutlineBrush, StrokeThickness = 1.4, IsHitTestVisible = false });
                break;
            }
            case "linear":
            {
                Point a = ToView(P(0, 0.5), P(1, 0.25)), b = ToView(P(2, 0.5), P(3, 0.6));
                double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-3) return;
                double nx = -dy / len, ny = dx / len, L = Math.Max(ActualWidth, ActualHeight) * 2;
                foreach (var (q, dash) in new[] { (a, false), (b, true) })
                {
                    var q1 = new Point(q.X - nx * L, q.Y - ny * L);
                    var q2 = new Point(q.X + nx * L, q.Y + ny * L);
                    AddLine(q1, q2, Dark, 3, dash);
                    AddLine(q1, q2, OutlineBrush, 1.4, dash);
                }
                break;
            }
            case "brush":
            {
                double k = DipPerSrcWidth;
                foreach (var st in spec.Strokes ?? new List<double[]>())
                {
                    if (st is null || st.Length < 4 || st[0] < 0.5) continue;
                    var pts = new PointCollection();
                    for (int i = 2; i + 1 < st.Length; i += 2) pts.Add(ToView(st[i], st[i + 1]));
                    if (pts.Count == 1) pts.Add(new Point(pts[0].X + 0.01, pts[0].Y));
                    Children.Add(new Polyline
                    {
                        Points = pts, Stroke = OutlinePaint, StrokeThickness = Math.Max(1, st[1] * 2 * k),
                        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                        IsHitTestVisible = false
                    });
                }
                break;
            }
        }
    }

    private void AddLine(Point a, Point b, Brush stroke, double thickness, bool dashed = false)
    {
        var l = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = stroke, StrokeThickness = thickness, IsHitTestVisible = false };
        if (dashed) l.StrokeDashArray = new DoubleCollection { 4, 3 };
        Children.Add(l);
    }

    private void AddHandle(Point p, bool rotate = false)
    {
        var e = new Ellipse { Width = HandleR * 2, Height = HandleR * 2, Fill = rotate ? Accent : Light, Stroke = Dark, StrokeThickness = 1.5, IsHitTestVisible = false };
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
        var p = RadialParams();
        foreach (var (scale, dash) in new[] { (1.0, false), (Math.Max(0.0, 1 - p[4]), true) })
        {
            if (scale <= 0.001) continue;
            var pts = new PointCollection();
            for (int i = 0; i <= 72; i++)
            {
                double a = i * Math.PI * 2 / 72;
                pts.Add(RadialPoint(p, Math.Cos(a) * scale, Math.Sin(a) * scale));
            }
            Children.Add(new Polyline { Points = pts, Stroke = Dark, StrokeThickness = 3, IsHitTestVisible = false });
            var pl = new Polyline { Points = pts, Stroke = Light, StrokeThickness = 1.2, IsHitTestVisible = false };
            if (dash) pl.StrokeDashArray = new DoubleCollection { 4, 3 };
            Children.Add(pl);
        }
        var h = Handles();
        AddLine(RadialPoint(p, 0, -1), h[RotateHandle], Dark, 3);
        AddLine(RadialPoint(p, 0, -1), h[RotateHandle], Accent, 1.2);
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
            // colour on: the strokes mark the painted area; colour off: a thin centreline only, like the outline of
            // a radial mask — a clean photo with the result, the shape still traceable for painting and erasing.
            if (ShowEditFill)
                AddStroke(pts, paint ? StrokeFill : Frozen(Color.FromArgb(60, 80, 160, 255)), Math.Max(1, st[1] * 2 * k));
            else
            {
                var line = paint ? Light : Frozen(Color.FromArgb(255, 120, 180, 255));
                AddStroke(pts, Dark, 3);
                AddStroke(pts, line, 1.2);
            }
        }
        if (_mouseInside && _hoverPin < 0)
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

    private void AddStroke(PointCollection pts, Brush stroke, double thickness) =>
        Children.Add(new Polyline
        {
            Points = pts,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        });

    /// <summary>Draws now — e.g. the brush circle after its size was changed by a key while the pointer rests.</summary>
    public void Refresh() => Redraw();

    // ------------------------------------------------------------------
    //  Mouse
    // ------------------------------------------------------------------

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        int pin = PinAt(pos);
        // A marker wins over painting and moving the shape, but not over a handle of the mask being edited nor its outer outline (the feather grip).
        if (pin >= 0 && !(Editing && _spec is not null && _spec.Kind != "brush" &&
                          (Handles().Any(h => (h - pos).Length <= HandleR + 4) || (_spec.Kind == "radial" && NearRadialOuter(pos)))))
        {
            e.Handled = true;
            PinClicked?.Invoke(_clusters[pin].Pins.ToList(), pos);
            return;
        }
        if (!Editing || _spec is null) return;
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
        if (_drag < 0 && _spec.Kind == "radial" && NearRadialOuter(pos))
            _drag = FeatherDrag; // the outer outline: tuning the feather (the inner ellipse stays fixed)
        if (_drag < 0) _drag = 100; // move whole shape
        _dragStartSrc = ToSrc(pos);
        _dragStartP = _spec.Kind == "linear" ? Params(4, 0.5, 0.25, 0.5, 0.6) : RadialParams();
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        _mouseInside = true;
        if (_drag < 0 && _stroke is null)
        {
            int h = PinAt(_mouse);
            bool outer = _mouseInside && Editing && _spec?.Kind == "radial" && NearRadialOuter(_mouse);
            if (h != _hoverPin || outer != _hoverOuter)
            {
                bool pinChanged = h != _hoverPin;
                _hoverPin = h;
                _hoverOuter = outer;
                UpdateCursor();
                if (pinChanged) Redraw(); // the pin highlight follows, the cursor change needs no redraw
            }
        }
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
            // Offsets from the center in source pixels (the ellipse and its rotation live in pixel space).
            double ox = (cur.X - p[0]) * _srcW, oy = (cur.Y - p[1]) * _srcH;
            if (_drag == 0 || _drag == 100) { p[0] += dx; p[1] += dy; }
            else if (_drag == RotateHandle)
            {
                // The handle sits above the center at angle 0; Shift snaps to 15 degrees.
                double a = Math.Atan2(ox, -oy) * 180 / Math.PI;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) a = Math.Round(a / 15) * 15;
                if (a <= -180) a += 360;
                p[5] = Math.Round(a, 2);
            }
            else if (_drag == FeatherDrag)
            {
                // The outer outline follows the pointer and the inner (dashed) ellipse stays fixed:
                // on the inner ellipse = 0 feather, twice its diameter = 100 %.
                double th = _dragStartP[5] * Math.PI / 180, cos = Math.Cos(th), sin = Math.Sin(th);
                double nx = (ox * cos + oy * sin) / Math.Max(1e-6, _dragStartP[2] * _srcW);
                double ny = (oy * cos - ox * sin) / Math.Max(1e-6, _dragStartP[3] * _srcH);
                double m = Math.Sqrt(nx * nx + ny * ny); // in units of the outer ellipse
                double f0 = Math.Clamp(_dragStartP[4], 0, 1);
                var (band, scale) = MaskRender.RadialFeatherDrag(f0, m / Math.Max(1e-6, 1 - f0));
                p[2] = _dragStartP[2] * scale;
                p[3] = _dragStartP[3] * scale;
                p[4] = band;
            }
            else
            {
                // Radius handles on the inner ellipse: distance from the center, independent of the display and mask rotation.
                double inner = RadialInner(p);
                double dist = Math.Sqrt(ox * ox + oy * oy);
                if (_drag == 1) p[2] = Math.Clamp(dist / (inner * _srcW), 0.01, 2.0);
                else p[3] = Math.Clamp(dist / (inner * _srcH), 0.01, 2.0);
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
