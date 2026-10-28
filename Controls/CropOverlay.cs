using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ImgViewer.Controls;

public sealed class CropOverlay : Canvas
{
    private ImageViewer? _viewer;
    private readonly Rectangle _shadeTop;
    private readonly Rectangle _shadeBottom;
    private readonly Rectangle _shadeLeft;
    private readonly Rectangle _shadeRight;
    private readonly Rectangle[] _thirds;
    private readonly Rectangle[] _fine;
    private readonly Rectangle _border;
    private readonly Rectangle[] _handles;

    private Rect _frame = Rect.Empty;
    private double _sfx;
    private double _sfy;
    private double _sfw;
    private double _sfh;
    private Rect _startCrop;
    private double _fx;
    private double _fy;
    private double _fw = 1.0;
    private double _fh = 1.0;
    private double _angle;
    private double _aspect = double.NaN;
    private double _startAtan;

    private enum Mode { None, Move, EdgeN, EdgeS, EdgeE, EdgeW, CornerNW, CornerNE, CornerSW, CornerSE, Rotate }
    private Mode _mode = Mode.None;
    private Point _startPt;
    private double _sAngle;

    private const double HandleSize = 10.0;
    private const double HandleHitPad = 6.0;
    private const double MinPx = 8.0;
    private const double MinFraction = 0.02;

    public event Action? GeometryChanged;
    public event Action? RotateChanged;
    public event Action? InteractionEnded;

    public double Angle => _angle;
    public double Aspect { get => _aspect; set => _aspect = value; }
    public bool IsInteracting => _mode != Mode.None;
    public double CropFx => _fx;
    public double CropFy => _fy;
    public double CropFw => _fw;
    public double CropFh => _fh;

    public CropOverlay()
    {
        Background = Brushes.Transparent;
        Focusable = false;

        _shadeTop = Shade();
        _shadeBottom = Shade();
        _shadeLeft = Shade();
        _shadeRight = Shade();
        _thirds = new[] { Line(), Line(), Line(), Line() };
        _fine = new Rectangle[14];
        for (int i = 0; i < _fine.Length; i++) _fine[i] = LineFine();
        _border = new Rectangle
        {
            Stroke = FrozenBrush(255, 233, 234, 238),
            StrokeThickness = 1.0,
            Fill = Brushes.Transparent
        };
        _handles = new Rectangle[8];
        for (int i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                RadiusX = 2,
                RadiusY = 2,
                Fill = FrozenBrush(255, 233, 234, 238),
                Stroke = FrozenBrush(255, 23, 26, 34),
                StrokeThickness = 1.0
            };
        }
        _handles[0].Cursor = Cursors.SizeNWSE;
        _handles[1].Cursor = Cursors.SizeWE;
        _handles[2].Cursor = Cursors.SizeNESW;
        _handles[3].Cursor = Cursors.SizeNS;
        _handles[4].Cursor = Cursors.SizeNESW;
        _handles[5].Cursor = Cursors.SizeWE;
        _handles[6].Cursor = Cursors.SizeNWSE;
        _handles[7].Cursor = Cursors.SizeNS;

        Children.Add(_shadeTop);
        Children.Add(_shadeBottom);
        Children.Add(_shadeLeft);
        Children.Add(_shadeRight);
        foreach (var t in _thirds) Children.Add(t);
        foreach (var f in _fine) Children.Add(f);
        Children.Add(_border);
        foreach (var h in _handles) Children.Add(h);

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseWheel += OnMouseWheel;
        SizeChanged += (_, _) => Sync();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewer is null || _viewer.ImageRect.IsEmpty) return;
        e.Handled = true;
        _viewer.WheelFrom(e.GetPosition(this), e.Delta);
    }

    public void Attach(ImageViewer viewer) => _viewer = viewer;

    public void SetGeometry(double fx, double fy, double fw, double fh, double angle)
    {
        _fw = Math.Clamp(fw, MinFraction, 1.0);
        _fh = Math.Clamp(fh, MinFraction, 1.0);
        _fx = Math.Clamp(Math.Clamp(fx, 0.0, 1.0), 0.0, 1.0 - _fw);
        _fy = Math.Clamp(Math.Clamp(fy, 0.0, 1.0), 0.0, 1.0 - _fh);
        _angle = Math.Clamp(angle, -45.0, 45.0);
        Sync();
    }

    public void Sync()
    {
        var img = _viewer?.ImageRect ?? Rect.Empty;
        if (img.IsEmpty || img.Width < 2 || img.Height < 2)
        {
            _frame = Rect.Empty;
            Redraw();
            return;
        }
        _frame = img;
        Redraw();
    }

    public void Hide()
    {
        _frame = Rect.Empty;
        _mode = Mode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        Redraw();
    }

    private static SolidColorBrush FrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Rectangle Shade() => new()
    {
        Fill = FrozenBrush(150, 0, 0, 0),
        StrokeThickness = 0
    };

    private static Rectangle Line() => new()
    {
        Fill = FrozenBrush(40, 255, 255, 255),
        StrokeThickness = 0
    };

    private static Rectangle LineFine() => new()
    {
        Fill = FrozenBrush(52, 255, 255, 255),
        StrokeThickness = 0
    };

    private Rect FrameCropRect() => new(
        _frame.X + _fx * _frame.Width,
        _frame.Y + _fy * _frame.Height,
        _fw * _frame.Width,
        _fh * _frame.Height);

    private void UpdateClip()
    {
        var bounds = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        var vis = _frame.IsEmpty ? Rect.Empty : _frame;
        if (!vis.IsEmpty)
        {
            vis.Intersect(bounds);
            if (!vis.IsEmpty)
            {
                vis.Inflate(HandleSize / 2 + 1, HandleSize / 2 + 1);
                vis.Intersect(bounds);
            }
        }
        var geo = new RectangleGeometry(vis.Width > 0 && vis.Height > 0 ? vis : Rect.Empty);
        if (geo.CanFreeze) geo.Freeze();
        Clip = geo;
    }

    private void Redraw()
    {
        UpdateClip();
        if (_frame.IsEmpty || _frame.Width < 4 || _frame.Height < 4)
        {
            SetRect(_shadeTop, 0, 0, 0, 0);
            SetRect(_shadeBottom, 0, 0, 0, 0);
            SetRect(_shadeLeft, 0, 0, 0, 0);
            SetRect(_shadeRight, 0, 0, 0, 0);
            foreach (var t in _thirds) SetRect(t, 0, 0, 0, 0);
            foreach (var f in _fine) SetRect(f, 0, 0, 0, 0);
            SetRect(_border, 0, 0, 0, 0);
            foreach (var hd in _handles) SetRect(hd, 0, 0, 0, 0);
            return;
        }

        Rect crop = FrameCropRect();
        double cl = Math.Max(crop.Left, 0);
        double ct = Math.Max(crop.Top, 0);
        double cr = Math.Min(crop.Right, ActualWidth);
        double cb = Math.Min(crop.Bottom, ActualHeight);
        double fl = Math.Max(_frame.Left, 0);
        double ft = Math.Max(_frame.Top, 0);
        double fr = Math.Min(_frame.Right, ActualWidth);
        double fb = Math.Min(_frame.Bottom, ActualHeight);

        SetRect(_shadeTop, fl, ft, fr - fl, ct - ft);
        SetRect(_shadeBottom, fl, cb, fr - fl, fb - cb);
        SetRect(_shadeLeft, fl, ct, Math.Max(0, cl - fl), Math.Max(0, cb - ct));
        SetRect(_shadeRight, cr, ct, Math.Max(0, fr - cr), Math.Max(0, cb - ct));

        double w = crop.Width;
        double h = crop.Height;
        SetRect(_thirds[0], crop.Left + w / 3 - 0.5, crop.Top, 1.0, h);
        SetRect(_thirds[1], crop.Left + w * 2 / 3 - 0.5, crop.Top, 1.0, h);
        SetRect(_thirds[2], crop.Left, crop.Top + h / 3 - 0.5, w, 1.0);
        SetRect(_thirds[3], crop.Left, crop.Top + h * 2 / 3 - 0.5, w, 1.0);

        bool rotating = _mode == Mode.Rotate;
        for (int i = 0; i < _fine.Length; i++)
        {
            var r = _fine[i];
            if (!rotating) { SetRect(r, 0, 0, 0, 0); continue; }
            if (i < 7)
            {
                double x = crop.Left + w * (i + 1) / 8.0;
                SetRect(r, x - 0.5, crop.Top, 1.0, h);
            }
            else
            {
                double y = crop.Top + h * (i - 6) / 8.0;
                SetRect(r, crop.Left, y - 0.5, w, 1.0);
            }
        }

        SetRect(_border, crop.Left, crop.Top, w, h);

        double hs = HandleSize / 2.0;
        SetHandle(_handles[0], crop.Left, crop.Top, hs);
        SetHandle(_handles[1], crop.Right, (crop.Top + crop.Bottom) / 2, hs);
        SetHandle(_handles[2], crop.Right, crop.Top, hs);
        SetHandle(_handles[3], (crop.Left + crop.Right) / 2, crop.Bottom, hs);
        SetHandle(_handles[4], crop.Right, crop.Bottom, hs);
        SetHandle(_handles[5], crop.Left, (crop.Top + crop.Bottom) / 2, hs);
        SetHandle(_handles[6], crop.Left, crop.Bottom, hs);
        SetHandle(_handles[7], (crop.Left + crop.Right) / 2, crop.Top, hs);
    }

    private static void SetRect(Rectangle rect, double x, double y, double w, double h)
    {
        SetLeft(rect, x);
        SetTop(rect, y);
        rect.Width = Math.Max(0, w);
        rect.Height = Math.Max(0, h);
    }

    private static void SetHandle(Rectangle handle, double x, double y, double half)
    {
        SetLeft(handle, x - half);
        SetTop(handle, y - half);
    }

    private Mode HitTest(Point p)
    {
        Rect crop = FrameCropRect();
        double pad = HandleHitPad + HandleSize / 2;
        bool inX = p.X >= crop.Left - pad && p.X <= crop.Right + pad;
        bool inY = p.Y >= crop.Top - pad && p.Y <= crop.Bottom + pad;
        bool nearTop = Math.Abs(p.Y - crop.Top) <= pad && inX;
        bool nearBottom = Math.Abs(p.Y - crop.Bottom) <= pad && inX;
        bool nearLeft = Math.Abs(p.X - crop.Left) <= pad && inY;
        bool nearRight = Math.Abs(p.X - crop.Right) <= pad && inY;
        if (nearTop && nearLeft) return Mode.CornerNW;
        if (nearTop && nearRight) return Mode.CornerNE;
        if (nearBottom && nearLeft) return Mode.CornerSW;
        if (nearBottom && nearRight) return Mode.CornerSE;
        if (nearTop) return Mode.EdgeN;
        if (nearBottom) return Mode.EdgeS;
        if (nearLeft) return Mode.EdgeW;
        if (nearRight) return Mode.EdgeE;
        if (inX && inY && crop.Contains(p))
            return CtrlHeld() ? Mode.Rotate : Mode.Move;
        return Mode.Rotate;
    }

    private static bool CtrlHeld() =>
        (System.Windows.Input.Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

    private static Cursor CursorFor(Mode mode) => mode switch
    {
        Mode.Move => Cursors.SizeAll,
        Mode.EdgeN or Mode.EdgeS => Cursors.SizeNS,
        Mode.EdgeE or Mode.EdgeW => Cursors.SizeWE,
        Mode.CornerNW or Mode.CornerSE => Cursors.SizeNWSE,
        Mode.CornerNE or Mode.CornerSW => Cursors.SizeNESW,
        Mode.Rotate => Cursors.SizeAll,
        _ => Cursors.Arrow
    };

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_frame.IsEmpty || _frame.Width < 4 || _frame.Height < 4) return;
        var p = e.GetPosition(this);
        _mode = HitTest(p);
        _startPt = p;
        _startCrop = FrameCropRect();
        _sfx = _fx;
        _sfy = _fy;
        _sfw = _fw;
        _sfh = _fh;
        _sAngle = _angle;
        Point center = Center(_frame);
        _startAtan = Math.Atan2(p.Y - center.Y, p.X - center.X);
        Cursor = CursorFor(_mode);
        Redraw();
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_mode == Mode.None)
        {
            if (!_frame.IsEmpty && IsMouseOver) Cursor = CursorFor(HitTest(p));
            return;
        }
        if (_frame.IsEmpty) return;
        switch (_mode)
        {
            case Mode.Move:
            {
                double dx = (p.X - _startPt.X) / _frame.Width;
                double dy = (p.Y - _startPt.Y) / _frame.Height;
                _fx = Math.Clamp(_sfx + dx, 0.0, 1.0 - _sfw);
                _fy = Math.Clamp(_sfy + dy, 0.0, 1.0 - _sfh);
                Redraw();
                GeometryChanged?.Invoke();
                break;
            }
            case Mode.Rotate:
            {
                Point center = Center(_frame);
                double a = Math.Atan2(p.Y - center.Y, p.X - center.X);
                double deg = _sAngle + (a - _startAtan) * 180.0 / Math.PI;
                double nv = Math.Round(Math.Clamp(deg, -45.0, 45.0) * 10.0) / 10.0;
                if (Math.Abs(nv - _angle) > 0.01)
                {
                    _angle = nv;
                    RotateChanged?.Invoke();
                }
                break;
            }
            default:
            {
                ApplyResize(p);
                Redraw();
                GeometryChanged?.Invoke();
                break;
            }
        }
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == Mode.None) return;
        _mode = Mode.None;
        Cursor = Cursors.Arrow;
        Redraw();
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
        InteractionEnded?.Invoke();
    }

    private void ApplyResize(Point p)
    {
        double minW = Math.Max(MinPx, MinFraction * _frame.Width);
        double minH = Math.Max(MinPx, MinFraction * _frame.Height);
        double fl = _frame.Left;
        double ft = _frame.Top;
        double fr = _frame.Right;
        double fb = _frame.Bottom;
        bool locked = !double.IsNaN(_aspect) && _aspect > 0;
        double aspect = locked ? _aspect : double.NaN;

        bool freeX = _mode is Mode.EdgeE or Mode.EdgeW or Mode.CornerNE or Mode.CornerNW or Mode.CornerSE or Mode.CornerSW;
        bool freeY = _mode is Mode.EdgeN or Mode.EdgeS or Mode.CornerNE or Mode.CornerNW or Mode.CornerSE or Mode.CornerSW;

        bool anchorRight = _mode is Mode.EdgeW or Mode.CornerNW or Mode.CornerSW;
        bool anchorBottom = _mode is Mode.EdgeN or Mode.CornerNW or Mode.CornerNE;

        double anchorX = anchorRight ? _startCrop.Right : _startCrop.Left;
        double anchorY = anchorBottom ? _startCrop.Bottom : _startCrop.Top;
        double maxW = anchorRight ? anchorX - fl : fr - anchorX;
        double maxH = anchorBottom ? anchorY - ft : fb - anchorY;

        double w = _startCrop.Width;
        double h = _startCrop.Height;

        if (freeX)
        {
            w = Math.Clamp(Math.Abs(p.X - anchorX), minW, Math.Max(minW, maxW));
            if (locked)
            {
                h = w / aspect;
                if (h > maxH || h < minH)
                {
                    h = Math.Clamp(h, minH, Math.Max(minH, maxH));
                    w = Math.Min(h * aspect, Math.Max(minW, maxW));
                    h = w / aspect;
                }
            }
            else if (!freeY)
            {
                h = _startCrop.Height;
            }
        }
        if (freeY)
        {
            h = Math.Clamp(Math.Abs(p.Y - anchorY), minH, Math.Max(minH, maxH));
            if (locked)
            {
                if (!freeX)
                {
                    w = Math.Min(h * aspect, Math.Max(minW, maxW));
                    h = Math.Clamp(w / aspect, minH, Math.Max(minH, maxH));
                    w = h * aspect;
                }
            }
            else if (!freeX)
            {
                w = _startCrop.Width;
            }
        }

        double x = anchorRight ? anchorX - w : anchorX;
        double y = anchorBottom ? anchorY - h : anchorY;
        if (_mode == Mode.EdgeE || _mode == Mode.EdgeW) y = _startCrop.Top;
        if (_mode == Mode.EdgeN || _mode == Mode.EdgeS) x = _startCrop.Left;
        if (_mode == Mode.EdgeN && !locked) { x = _startCrop.Left; w = _startCrop.Width; }
        if (_mode == Mode.EdgeS && !locked) { x = _startCrop.Left; w = _startCrop.Width; }
        if (_mode == Mode.EdgeE && !locked) { y = _startCrop.Top; h = _startCrop.Height; }
        if (_mode == Mode.EdgeW && !locked) { y = _startCrop.Top; h = _startCrop.Height; }

        x = Math.Clamp(x, fl, Math.Max(fl, fr - w));
        double yMax = Math.Max(ft, fb - h);
        y = Math.Clamp(y, ft, yMax);

        double fw = Math.Max(1.0, _frame.Width);
        double fh = Math.Max(1.0, _frame.Height);
        double minFx = Math.Max(MinFraction, minW / fw);
        double minFy = Math.Max(MinFraction, minH / fh);
        _fw = Math.Clamp(w / fw, minFx, 1.0);
        _fh = Math.Clamp(h / fh, minFy, 1.0);
        _fx = Math.Clamp((x - fl) / fw, 0.0, 1.0 - _fw);
        _fy = Math.Clamp((y - ft) / fh, 0.0, 1.0 - _fh);
    }

    private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
}