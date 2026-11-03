using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ImgViewer.Imaging;

namespace ImgViewer.Controls;

/// <summary>
/// Markers for plugin Point parameters (e.g. heal spots): a circle or cross per point, a dashed circle with an arrow
/// for a linked point (source → target) and a small label. Points are fractions of the un-rotated, un-cropped photo
/// and are mapped through the current display geometry. Only the markers themselves take the mouse (drag to move);
/// everywhere else the mouse passes through to the viewer.
/// </summary>
public sealed class PointMarkerOverlay : Canvas
{
    /// <param name="Key">Identity of the point (reported back by <see cref="MarkerMoved"/>).</param>
    /// <param name="P">Point, fraction of the source photo.</param>
    /// <param name="Radius">Radius as a fraction of the photo's long side (0 = cross).</param>
    /// <param name="Dashed">Secondary point (drawn dashed).</param>
    /// <param name="LinkKey">Key of the marker this one points at with an arrow.</param>
    /// <param name="Selected">Drawn in the accent colour (the item being edited in the panel).</param>
    public sealed record Marker(string Key, Point P, double Radius, bool Dashed, string Label, string? LinkKey = null, bool Selected = false);

    private const double MinHitR = 9;

    private ImageViewer? _viewer;
    private GeoState _geo = GeoState.Default;
    private double _srcW = 1, _srcH = 1;
    private List<Marker> _markers = new();
    private string? _dragKey;
    private Vector _dragOffset;
    private bool _dragMoved;
    private bool _interactive = true;

    private static readonly Brush Light = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Shadow = Freeze(new SolidColorBrush(Color.FromArgb(0xA0, 0x00, 0x00, 0x00)));
    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromArgb(0xF8, 0xFF, 0xC8, 0x50)));
    private static readonly Brush HoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly DoubleCollection Dash = FreezeDash(new DoubleCollection { 4, 3 });

    /// <summary>Raised while a marker is dragged (final = false) and when it is released (final = true). Point in source fractions.</summary>
    public event Action<string, Point, bool>? MarkerMoved;

    /// <summary>Raised when a marker is grabbed with the mouse (before any movement).</summary>
    public event Action<string>? MarkerGrabbed;

    public PointMarkerOverlay()
    {
        Focusable = false;
        ClipToBounds = true;
        Background = null; // the canvas itself is not hit-testable, only the marker shapes
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) => EndDrag(commit: true);
        MouseWheel += (_, e) =>
        {
            if (_viewer is null) return;
            e.Handled = true;
            _viewer.WheelFrom(e.GetPosition(_viewer), e.Delta);
        };
        SizeChanged += (_, _) => Redraw();
    }

    public int Count => _markers.Count;
    public bool IsDragging => _dragKey is not null;

    /// <summary>When false the markers are only drawn (e.g. while a point is being picked on the photo).</summary>
    public bool Interactive
    {
        get => _interactive;
        set
        {
            if (_interactive == value) return;
            _interactive = value;
            if (!value && IsDragging) EndDrag(commit: true);
            Redraw();
        }
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

    public void SetMarkers(IReadOnlyList<Marker> markers)
    {
        var list = markers.ToList();
        // While dragging keep the dragged point where the mouse is (the host may still hold the previous value).
        if (_dragKey is not null && _markers.FirstOrDefault(m => m.Key == _dragKey) is { } cur)
        {
            int i = list.FindIndex(m => m.Key == _dragKey);
            if (i >= 0) list[i] = list[i] with { P = cur.P };
        }
        _markers = list;
        Redraw();
    }

    public Marker? Find(string key) => _markers.FirstOrDefault(m => m.Key == key);

    // ------------------------------------------------------------------
    //  Mapping
    // ------------------------------------------------------------------

    private Rect Frame => _viewer?.ImageRect ?? Rect.Empty;

    public Point ToView(Point src)
    {
        var f = Frame;
        var o = PhotoGeo.MapToOutput(_geo, _srcW, _srcH, src);
        return new Point(f.X + o.X * f.Width, f.Y + o.Y * f.Height);
    }

    public Point ToSrc(Point view)
    {
        var f = Frame;
        if (f.Width < 1 || f.Height < 1) return new Point(0.5, 0.5);
        return PhotoGeo.MapFromOutput(_geo, _srcW, _srcH, new Point((view.X - f.X) / f.Width, (view.Y - f.Y) / f.Height));
    }

    /// <summary>Radius in DIPs: maps a horizontal offset of the radius (rotation keeps lengths).</summary>
    private double ViewRadius(Point src, double radiusLong)
    {
        if (radiusLong <= 0) return 0;
        double dx = radiusLong * Math.Max(_srcW, _srcH) / _srcW;
        var a = ToView(src);
        var b = ToView(new Point(src.X + dx, src.Y));
        return (b - a).Length;
    }

    /// <summary>Key of the marker under <paramref name="view"/> (the dragged one first, then the topmost).</summary>
    public string? HitTestMarker(Point view)
    {
        for (int i = _markers.Count - 1; i >= 0; i--)
        {
            var m = _markers[i];
            var c = ToView(m.P);
            if ((view - c).Length <= Math.Max(ViewRadius(m.P, m.Radius), MinHitR)) return m.Key;
        }
        return null;
    }

    // ------------------------------------------------------------------
    //  Dragging
    // ------------------------------------------------------------------

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || !BeginDragAt(e.GetPosition(this))) return;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragKey is null) return;
        e.Handled = true;
        DragTo(e.GetPosition(this));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragKey is null) return;
        e.Handled = true;
        EndDrag(commit: true);
    }

    /// <summary>Starts dragging the marker under <paramref name="view"/> (overlay coordinates).</summary>
    public bool BeginDragAt(Point view)
    {
        if (!_interactive || HitTestMarker(view) is not { } key || Find(key) is not { } m) return false;
        _dragKey = key;
        _dragOffset = view - ToView(m.P);
        _dragMoved = false;
        MarkerGrabbed?.Invoke(key);
        Redraw();
        return true;
    }

    /// <summary>Moves the dragged marker so the grab point follows <paramref name="view"/>.</summary>
    public void DragTo(Point view)
    {
        if (_dragKey is null) return;
        var src = ToSrc(view - _dragOffset);
        src = new Point(Math.Clamp(src.X, 0, 1), Math.Clamp(src.Y, 0, 1));
        int i = _markers.FindIndex(m => m.Key == _dragKey);
        if (i < 0) return;
        if ((ToView(src) - ToView(_markers[i].P)).Length < 0.25) return;
        _markers[i] = _markers[i] with { P = src };
        _dragMoved = true;
        Redraw();
        MarkerMoved?.Invoke(_dragKey, src, false);
    }

    public void EndDrag(bool commit = true)
    {
        if (_dragKey is not { } key) return;
        _dragKey = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (commit && _dragMoved && Find(key) is { } m) MarkerMoved?.Invoke(key, m.P, true);
        _dragMoved = false;
        Redraw();
    }

    // ------------------------------------------------------------------
    //  Drawing
    // ------------------------------------------------------------------

    private void Redraw()
    {
        Children.Clear();
        if (Visibility != Visibility.Visible || _markers.Count == 0) return;
        var f = Frame;
        if (f.Width < 1 || f.Height < 1) return;
        foreach (var m in _markers)
        {
            var c = ToView(m.P);
            double r = ViewRadius(m.P, m.Radius);
            if (m.LinkKey is { } lk && Find(lk) is { } t)
                DrawArrow(c, ToView(t.P), Math.Max(r, 6), Math.Max(ViewRadius(t.P, t.Radius), 6), m.Selected ? Accent : Light);
            bool active = m.Key == _dragKey;
            var stroke = m.Selected ? Accent : Light;
            if (r >= 3) DrawCircle(c, r, m.Dashed, active || m.Selected, stroke);
            else DrawCross(c, m.Dashed, stroke);
            if (m.Label.Length > 0) DrawLabel(new Point(c.X + Math.Max(r, 7) * 0.72 + 3, c.Y - Math.Max(r, 7) * 0.72 - 14), m.Label);
            if (_interactive) AddHitArea(c, Math.Max(r, MinHitR));
        }
    }

    /// <summary>Transparent disc that takes the mouse for a marker (the drawn strokes do not).</summary>
    private void AddHitArea(Point c, double r)
    {
        var hit = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "Drag to move" };
        hit.MouseEnter += (_, _) => { if (_dragKey is null) hit.Fill = HoverFill; };
        hit.MouseLeave += (_, _) => hit.Fill = Brushes.Transparent;
        SetLeft(hit, c.X - r);
        SetTop(hit, c.Y - r);
        Children.Add(hit);
    }

    private void DrawCircle(Point c, double r, bool dashed, bool active, Brush stroke)
    {
        foreach (var (brush, w) in new[] { (Shadow, 3.0), (stroke, active ? 2.0 : 1.3) })
        {
            var e = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = brush, StrokeThickness = w, IsHitTestVisible = false };
            if (dashed) e.StrokeDashArray = Dash;
            SetLeft(e, c.X - r);
            SetTop(e, c.Y - r);
            Children.Add(e);
        }
    }

    private void DrawCross(Point c, bool dashed, Brush stroke)
    {
        const double s = 7;
        foreach (var (brush, w) in new[] { (Shadow, 3.0), (stroke, 1.3) })
        {
            var g = new GeometryGroup();
            g.Children.Add(new LineGeometry(new Point(c.X - s, c.Y), new Point(c.X + s, c.Y)));
            g.Children.Add(new LineGeometry(new Point(c.X, c.Y - s), new Point(c.X, c.Y + s)));
            var p = new Path { Data = g, Stroke = brush, StrokeThickness = w, IsHitTestVisible = false };
            if (dashed) p.StrokeDashArray = Dash;
            Children.Add(p);
        }
    }

    /// <summary>Line from the edge of the source circle to the edge of the target circle, arrow head at the target.</summary>
    private void DrawArrow(Point from, Point to, double rFrom, double rTo, Brush stroke)
    {
        var v = to - from;
        double len = v.Length;
        if (len <= rFrom + rTo + 4) return;
        v /= len;
        var a = from + v * rFrom;
        var b = to - v * rTo;
        var n = new Vector(-v.Y, v.X);
        const double head = 8;
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(a, b));
        g.Children.Add(new LineGeometry(b, b - v * head + n * head * 0.55));
        g.Children.Add(new LineGeometry(b, b - v * head - n * head * 0.55));
        foreach (var (brush, w) in new[] { (Shadow, 3.0), (stroke, 1.2) })
            Children.Add(new Path
            {
                Data = g, Stroke = brush, StrokeThickness = w, IsHitTestVisible = false,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
    }

    private void DrawLabel(Point at, string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = Brushes.White,
            Background = Shadow,
            Padding = new Thickness(3, 0, 3, 1),
            IsHitTestVisible = false
        };
        SetLeft(tb, at.X);
        SetTop(tb, at.Y);
        Children.Add(tb);
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static DoubleCollection FreezeDash(DoubleCollection d) { d.Freeze(); return d; }
}
