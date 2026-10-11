using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Controls;

public sealed partial class ImageViewer : Grid
{
    private readonly Image _image;
    private Size _nativeSize;
    private double _decodedRatio = 1.0;
    private double _dipPerPx = 1.0;
    private DpiScale _dpi;
    private bool _isFit = true;
    private bool _dragging;
    private Point _lastMouse;

    public event EventHandler<double>? ZoomChanged;

    public ImageViewer()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;

        _image = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Fant);

        Children.Add(_image);

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseWheel += OnMouseWheel;
        SizeChanged += (_, _) =>
        {
            if (_image.Source is null) return;
            if (_isFit) FitToWindow();
            else ClampAndCenter();
        };
        Loaded += (_, _) => UpdateDpi();
    }

    public double PpiFactor
    {
        get
        {
            UpdateDpi();
            return _dpi.PixelsPerDip > 0 ? _dpi.PixelsPerDip : 1.0;
        }
    }

    public double Zoom
    {
        get
        {
            if (_nativeSize.Width <= 0 || _decodedRatio <= 0) return 1.0;
            return _dipPerPx * _decodedRatio * PpiFactor;
        }
    }

    public void SetSource(BitmapSource? source, Size nativeSize)
    {
        if (source is null)
        {
            Clear();
            return;
        }

        double oldRatio = _decodedRatio;
        double oldDipPerPx = _dipPerPx;
        _nativeSize = nativeSize;
        _decodedRatio = nativeSize.Width > 0 ? (double)source.PixelWidth / nativeSize.Width : 1.0;
        _image.Source = source;
        _image.Visibility = Visibility.Visible;

        if (_isFit || FitsViewport())
        {
            FitToWindow();
        }
        else
        {
            AdjustForNewRatio(oldRatio, oldDipPerPx);
        }

        RaiseZoomChanged();
    }

    public void Clear()
    {
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        _image.Width = 0;
        _image.Height = 0;
        _image.Margin = default;
        _nativeSize = default;
        _decodedRatio = 1.0;
        _dipPerPx = 1.0;
        _isFit = true;
        RaiseZoomChanged();
    }

    public void FitToWindow()
    {
        if (_image.Source is null || _nativeSize.Width <= 0 || _nativeSize.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double vpW = Math.Max(1.0, ActualWidth - 16);
        double vpH = Math.Max(1.0, ActualHeight - 16);
        double dipPerNative = Math.Min(vpW / _nativeSize.Width, vpH / _nativeSize.Height);
        double dipPerPx = dipPerNative / _decodedRatio;

        ApplyDipPerPx(dipPerPx);
        ClampAndCenter();
        _isFit = true;
        ImgViewer.Debug.Log($"FIT: vp={ActualWidth:0.#}x{ActualHeight:0.#} native={_nativeSize.Width}x{_nativeSize.Height} decR={_decodedRatio:0.###} ppi={_dpi.PixelsPerDip:0.###} dip/px={dipPerPx:0.#####} zoom={Zoom:0.####} disp={DisplayedWidth:0.#}x{DisplayedHeight:0.#}");
        RaiseZoomChanged();
    }

    public void SetZoom(double zoom, Point? anchor = null)
    {
        if (_image.Source is null || _decodedRatio <= 0) return;

        zoom = Math.Clamp(zoom, 0.05, 4.0);
        double ppi = PpiFactor;
        double dipPerPx = zoom / ppi / _decodedRatio;
        if (!double.IsFinite(dipPerPx) || dipPerPx <= 0) return;

        double cur = _dipPerPx;
        Point a = anchor ?? new Point(ActualWidth / 2.0, ActualHeight / 2.0);
        double ox = MarginLeft;
        double oy = MarginTop;
        double n = dipPerPx / cur;

        double newOx = a.X - (a.X - ox) * n;
        double newOy = a.Y - (a.Y - oy) * n;
        double newW = _image.Source is BitmapSource b ? b.PixelWidth * dipPerPx : 0;
        double newH = _image.Source is BitmapSource b2 ? b2.PixelHeight * dipPerPx : 0;

        _image.Width = newW;
        _image.Height = newH;
        _image.Margin = new Thickness(newOx, newOy, 0, 0);
        _dipPerPx = dipPerPx;
        _isFit = false;
        ClampAndCenter();
        RaiseZoomChanged();
    }

    public void ToggleFitNative()
    {
        if (_image.Source is null) return;
        double z = Zoom;
        if (z >= 0.97 && z <= 1.03)
        {
            FitToWindow();
        }
        else
        {
            SetZoom(1.0);
        }
        RaiseZoomChanged();
    }

    private void AdjustForNewRatio(double oldRatio, double oldDipPerPx)
    {
        if (oldRatio <= 0 || _decodedRatio <= 0 || oldDipPerPx <= 0) return;
        if (_image.Source is not BitmapSource bmp) return;

        double n = oldRatio / _decodedRatio;
        double dipPerPx = oldDipPerPx * n;

        _dipPerPx = dipPerPx;
        _image.Width = bmp.PixelWidth * dipPerPx;
        _image.Height = bmp.PixelHeight * dipPerPx;
        ClampAndCenter();
    }

    private void ClampAndCenter()
    {
        if (_image.Source is null) return;
        double vw = ActualWidth;
        double vh = ActualHeight;
        if (vw <= 0 || vh <= 0) return;

        double w = DisplayedWidth;
        double h = DisplayedHeight;
        double ox = w <= vw ? (vw - w) / 2.0 : Math.Clamp(MarginLeft, vw - w, 0.0);
        double oy = h <= vh ? (vh - h) / 2.0 : Math.Clamp(MarginTop, vh - h, 0.0);
        SetMargins(ox, oy);
        ImgViewer.Debug.Log($"CLAMP: vp={vw:0.#}x{vh:0.#} disp={w:0.#}x{h:0.#} margins=({ox:0.#},{oy:0.#})");
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_image.Source is null) return;
        if (e.ClickCount == 2)
        {
            ToggleFitNative();
            e.Handled = true;
            return;
        }
        _dragging = true;
        _lastMouse = e.GetPosition(this);
        Cursor = Cursors.ScrollAll;
        _ = CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Point p = e.GetPosition(this);
        double dx = p.X - _lastMouse.X;
        double dy = p.Y - _lastMouse.Y;
        _lastMouse = p;
        if (dx == 0 && dy == 0) return;
        Pan(dx, dy);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Cursor = Cursors.Arrow;
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_image.Source is null) return;
        e.Handled = true;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Pan(e.Delta * 0.4, 0);
        }
        else
        {
            double factor = Math.Pow(1.2, e.Delta / 120.0);
            SetZoom(Zoom * factor, e.GetPosition(this));
        }
    }

    private void Pan(double dx, double dy)
    {
        if (_image.Source is null) return;
        SetMargins(MarginLeft + dx, MarginTop + dy);
        ClampAndCenter();
        _isFit = false;
    }

    private void ApplyDipPerPx(double dipPerPx)
    {
        if (_image.Source is not BitmapSource bmp)
        {
            _dipPerPx = 1.0;
            return;
        }
        _dipPerPx = dipPerPx;
        _image.Width = bmp.PixelWidth * dipPerPx;
        _image.Height = bmp.PixelHeight * dipPerPx;
    }

    private double DisplayedWidth => _image.Width;
    private double DisplayedHeight => _image.Height;
    private double MarginLeft => _image.Margin.Left;
    private double MarginTop => _image.Margin.Top;
    private bool FitsViewport() => DisplayedWidth <= ActualWidth + 0.5 && DisplayedHeight <= ActualHeight + 0.5 && ActualWidth > 0;

    private void SetMargins(double ox, double oy) => _image.Margin = new Thickness(ox, oy, 0, 0);

    private void UpdateDpi()
    {
        try
        {
            var d = VisualTreeHelper.GetDpi(this);
            if (d.PixelsPerDip > 0)
            {
                _dpi = d;
            }
        }
        catch (Exception)
        {
        }
    }

    private void RaiseZoomChanged()
    {
        ZoomChanged?.Invoke(this, Zoom);
    }
}