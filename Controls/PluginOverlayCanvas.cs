using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ImgViewer.Imaging;
using ImgViewer.Plugins;

namespace ImgViewer.Controls;

/// <summary>
/// Draws plugin overlays (<see cref="OverlayContent"/>) over the photo: shapes in fractions of the shown image or of
/// the source photo (mapped through crop and rotation) and RGBA rasters stretched over the shown image.
/// Never takes the mouse.
/// </summary>
public sealed class PluginOverlayCanvas : Canvas
{
    private ImageViewer? _viewer;
    private GeoState _geo = GeoState.Default;
    private double _srcW = 1, _srcH = 1;
    private IReadOnlyList<OverlayContent> _layers = Array.Empty<OverlayContent>();
    private readonly ConditionalWeakTable<OverlayBitmap, BitmapSource> _bitmaps = new();
    private static readonly DoubleCollection Dash = FreezeDash();

    public PluginOverlayCanvas()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        SizeChanged += (_, _) => Redraw();
    }

    public int LayerCount => _layers.Count;

    /// <summary>Number of WPF elements drawn (for tests).</summary>
    public int ElementCount => Children.Count;

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
    }

    public void SetLayers(IReadOnlyList<OverlayContent> layers)
    {
        _layers = layers ?? Array.Empty<OverlayContent>();
        Redraw();
    }

    public void Redraw()
    {
        Children.Clear();
        if (_layers.Count == 0 || _viewer is null) return;
        var f = _viewer.ImageRect;
        if (f.IsEmpty || f.Width < 1 || f.Height < 1) return;
        foreach (var layer in _layers)
        {
            if (layer is null) continue;
            if (layer.Bitmap is { } ob && ToBitmap(ob) is { } bs)
            {
                var img = new Image { Source = bs, Width = f.Width, Height = f.Height, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                SetLeft(img, f.X);
                SetTop(img, f.Y);
                Children.Add(img);
            }
            if (layer.Shapes is null) continue;
            bool source = layer.Space == OverlaySpace.Source;
            Point Map(double x, double y)
            {
                if (source)
                {
                    var o = PhotoGeo.MapToOutput(_geo, _srcW, _srcH, new Point(x, y));
                    x = o.X;
                    y = o.Y;
                }
                return new Point(f.X + x * f.Width, f.Y + y * f.Height);
            }
            foreach (var s in layer.Shapes)
            {
                if (s?.Points is not { } p) continue;
                try
                {
                    if (Build(s, p, Map, source) is { } el) Children.Add(el);
                }
                catch (ArgumentException)
                {
                    // malformed shape: skipped
                }
            }
        }
    }

    private static UIElement? Build(OverlayShape s, double[] p, Func<double, double, Point> map, bool source)
    {
        var stroke = s.Stroke == 0 ? null : Brush(s.Stroke);
        var fill = s.Fill == 0 ? null : Brush(s.Fill);
        double th = double.IsFinite(s.Thickness) ? Math.Clamp(s.Thickness, 0, 50) : 1.5;
        switch (s.Kind)
        {
            case OverlayShapeKind.Line:
                if (p.Length < 4 || !Finite(p, 4)) return null;
                return Styled(new Line
                {
                    X1 = map(p[0], p[1]).X, Y1 = map(p[0], p[1]).Y,
                    X2 = map(p[2], p[3]).X, Y2 = map(p[2], p[3]).Y
                }, stroke, null, th, s.Dashed);
            case OverlayShapeKind.Rectangle:
            case OverlayShapeKind.Ellipse:
            {
                if (p.Length < 4 || !Finite(p, 4)) return null;
                double x = p[0], y = p[1], w = p[2], h = p[3];
                var pts = new PointCollection();
                if (s.Kind == OverlayShapeKind.Rectangle)
                {
                    pts.Add(map(x, y)); pts.Add(map(x + w, y)); pts.Add(map(x + w, y + h)); pts.Add(map(x, y + h));
                }
                else
                {
                    const int n = 64;
                    for (int i = 0; i < n; i++)
                    {
                        double a = 2 * Math.PI * i / n;
                        pts.Add(map(x + w / 2 * (1 + Math.Cos(a)), y + h / 2 * (1 + Math.Sin(a))));
                    }
                }
                return Styled(new Polygon { Points = pts }, stroke, fill, th, s.Dashed);
            }
            case OverlayShapeKind.Polyline:
            case OverlayShapeKind.Polygon:
            {
                int n = p.Length / 2;
                if (n < 2 || !Finite(p, n * 2)) return null;
                var pts = new PointCollection(n);
                for (int i = 0; i < n; i++) pts.Add(map(p[2 * i], p[2 * i + 1]));
                return s.Kind == OverlayShapeKind.Polygon
                    ? Styled(new Polygon { Points = pts }, stroke, fill, th, s.Dashed)
                    : Styled(new Polyline { Points = pts }, stroke, null, th, s.Dashed);
            }
            case OverlayShapeKind.Text:
            {
                if (p.Length < 2 || !Finite(p, 2) || string.IsNullOrEmpty(s.Text)) return null;
                var at = map(p[0], p[1]);
                var tb = new TextBlock
                {
                    Text = s.Text,
                    Foreground = stroke ?? Brushes.White,
                    Background = fill,
                    FontSize = double.IsFinite(s.FontSize) ? Math.Clamp(s.FontSize, 4, 200) : 12,
                    Padding = fill is null ? default : new Thickness(3, 1, 3, 1)
                };
                if (fill is null) tb.Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 1, BlurRadius = 2, Opacity = 0.8, Color = Colors.Black };
                SetLeft(tb, at.X);
                SetTop(tb, at.Y);
                return tb;
            }
            default:
                return null;
        }
    }

    private static Shape Styled(Shape sh, Brush? stroke, Brush? fill, double th, bool dashed)
    {
        sh.Stroke = stroke;
        sh.Fill = fill;
        sh.StrokeThickness = th;
        sh.StrokeLineJoin = PenLineJoin.Round;
        if (dashed) sh.StrokeDashArray = Dash;
        return sh;
    }

    private BitmapSource? ToBitmap(OverlayBitmap ob)
    {
        if (_bitmaps.TryGetValue(ob, out var cached)) return cached;
        int w = ob.Width, h = ob.Height;
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192 || ob.Rgba is null || ob.Rgba.Length < (long)w * h * 4) return null;
        var bgra = new byte[w * h * 4];
        var src = ob.Rgba;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = src[i + 2];
            bgra[i + 1] = src[i + 1];
            bgra[i + 2] = src[i];
            bgra[i + 3] = src[i + 3];
        }
        var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bs.Freeze();
        _bitmaps.AddOrUpdate(ob, bs);
        return bs;
    }

    private static bool Finite(double[] p, int n)
    {
        for (int i = 0; i < n; i++)
            if (!double.IsFinite(p[i])) return false;
        return true;
    }

    private static Brush Brush(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    private static DoubleCollection FreezeDash()
    {
        var d = new DoubleCollection { 4, 3 };
        d.Freeze();
        return d;
    }
}
