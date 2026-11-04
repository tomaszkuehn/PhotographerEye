using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ImgViewer.Services;
using Microsoft.Win32;

namespace ImgViewer;

// Settings flyout (icon tiles under the gear button), the viewer surround (plain colour) and the background picture
// (behind the adjustments panel and the welcome screen).
public partial class MainWindow
{
    private static readonly (string Id, string Label, Color Color)[] ViewerBackgrounds =
    {
        ("dark", "Dark (default)", Color.FromRgb(0x1A, 0x1B, 0x20)),
        ("black", "Black", Colors.Black),
        ("grey18", "Neutral grey 18 % (best for judging colour)", Color.FromRgb(0x76, 0x76, 0x76)),
        ("light", "Light grey", Color.FromRgb(0xC8, 0xC8, 0xC8)),
        ("white", "White (print / gallery wall)", Colors.White),
    };

    private static readonly (double Value, string Label)[] BackgroundDims =
    {
        (0.0, "Not darkened"),
        (0.3, "Darkened 30 %"),
        (0.6, "Darkened 60 %"),
        (0.8, "Darkened 80 %"),
    };

    private string _viewerBackground = "dark";
    private string? _viewerBackgroundImage;
    private bool _panelPicture = true;
    private bool _viewerBackgroundBlur = true;
    private double _viewerBackgroundDim = 0.6;

    private void InitAppearance(ViewerSettings settings)
    {
        _viewerBackground = settings.ViewerBackground ?? "dark";
        // Older builds offered the picture as the photo surround: it now goes behind the adjustments panel.
        if (_viewerBackground == "image") _viewerBackground = "dark";
        _viewerBackgroundImage = settings.ViewerBackgroundImage;
        _panelPicture = settings.PanelPicture ?? true;
        _viewerBackgroundBlur = settings.ViewerBackgroundBlur ?? true;
        _viewerBackgroundDim = Math.Clamp(settings.ViewerBackgroundDim ?? 0.6, 0, 0.9);
        SettingsFlyout.CustomPopupPlacementCallback = PlaceSettingsFlyout;
        ApplyAllBackgrounds();

        // Test hook (screenshots without mouse tests): PE_OPEN_SETTINGS=1 opens the flyout,
        // PE_OPEN_SETTINGS=<tile name> (e.g. TileBackground) opens that tile's menu,
        // "none" leaves the window as it is, "panel" opens the adjustments panel, "present" / "slideshow" enters presentation mode.
        var hook = Environment.GetEnvironmentVariable("PE_OPEN_SETTINGS");
        if (!string.IsNullOrEmpty(hook))
        {
            // Opened a few seconds after start, so a screenshot script can bring the window to the front first.
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                ContextMenu? menu = null;
                if (hook == "none") { }
                else if (hook == "panel") TogglePanel();
                else if (hook is "present" or "slideshow") SetPresentation(true, slideshow: hook == "slideshow");
                else if (hook != "1" && FindName(hook) is Button tile) { OpenTileMenu(tile); menu = tile.ContextMenu; }
                else OpenSettingsFlyout();
                // PE_SNAPSHOT=<file.png>: render the window (and the open flyout/menu) from inside the app and exit.
                var snap = Environment.GetEnvironmentVariable("PE_SNAPSHOT");
                if (string.IsNullOrEmpty(snap)) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SnapshotTo((FrameworkElement)Content, snap);
                    FrameworkElement? pop = menu is { IsOpen: true } ? menu : SettingsFlyout.IsOpen ? SettingsFlyoutBody : null;
                    if (pop is not null) SnapshotTo(pop, Path.ChangeExtension(snap, null) + "_popup.png");
                    Application.Current.Shutdown();
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
            Loaded += (_, _) => t.Start();
        }
    }

    private static void SnapshotTo(FrameworkElement el, string file)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(el);
            int w = (int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY);
            if (w <= 0 || h <= 0) return;
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(file);
            enc.Save(fs);
        }
        catch (Exception ex)
        {
            Debug.Log($"SNAPSHOT: {file} failed [{ex.GetType().Name}] {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- settings flyout

    private void OpenSettingsFlyout()
    {
        // Plugin menus may be empty (no plugin of that kind): hide their tiles.
        TileExport.Visibility = Vis(MnuExport.Items.Count > 0);
        TileCommands.Visibility = Vis(MnuCommands.Items.Count > 0);
        TileTools.Visibility = Vis(MnuPluginTools.Items.Count > 0);
        TilePluginSettings.Visibility = Vis(MnuPluginSettings.Items.Count > 0);
        TileOverlays.Visibility = Vis(MnuPluginOverlays.Items.Count > 0);
        SettingsFlyout.IsOpen = true;
        TilePlugins.Focus();

        static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseSettingsFlyout() => SettingsFlyout.IsOpen = false;

    /// <summary>Right-aligns the flyout under the gear button (the button sits at the right end of the toolbar).</summary>
    private CustomPopupPlacement[] PlaceSettingsFlyout(Size popupSize, Size targetSize, Point offset)
    {
        // The flyout body has a 10 px side margin for its shadow: line its right edge up with the button's.
        var p = new Point(targetSize.Width - popupSize.Width + 10, targetSize.Height + 2);
        return new[] { new CustomPopupPlacement(p, PopupPrimaryAxis.Horizontal) };
    }

    /// <summary>A tile with a menu: closes the flyout and opens the menu under the gear button.</summary>
    private void OnTileMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) OpenTileMenu(b);
    }

    private void OpenTileMenu(Button tile)
    {
        if (tile.ContextMenu is not ContextMenu menu) return;
        CloseSettingsFlyout();
        menu.PlacementTarget = BtnSettings;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- backgrounds

    private void OnBackgroundMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = MnuBackground;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "Photo surround", IsEnabled = false });
        foreach (var (id, label, color) in ViewerBackgrounds)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(color),
                BorderBrush = (Brush)FindResource("MutedBrush"),
                BorderThickness = new Thickness(1)
            });
            header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var it = new MenuItem { Header = header, IsCheckable = true, IsChecked = _viewerBackground == id };
            it.Click += (_, _) => SetViewerBackground(id);
            menu.Items.Add(it);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Adjustments panel", IsEnabled = false });

        bool picture = _panelPicture && HasBackgroundImage;
        var pick = new MenuItem
        {
            Header = HasBackgroundImage ? "Picture: " + Path.GetFileName(_viewerBackgroundImage) : "Picture…",
            IsCheckable = true,
            IsChecked = picture,
            ToolTip = "Your own picture behind the adjustments panel and the welcome screen (blurred and darkened so the sliders stay readable)"
        };
        pick.Click += (_, _) =>
        {
            if (HasBackgroundImage) { _panelPicture = !picture; SaveAppearance(); ApplyAllBackgrounds(); }
            else PickBackgroundImage();
        };
        menu.Items.Add(pick);
        if (HasBackgroundImage)
        {
            var change = new MenuItem { Header = "Choose another picture…" };
            change.Click += (_, _) => PickBackgroundImage();
            menu.Items.Add(change);
        }
        var blur = new MenuItem { Header = "Blur picture", IsCheckable = true, IsChecked = _viewerBackgroundBlur, IsEnabled = HasBackgroundImage };
        blur.Click += (_, _) =>
        {
            _viewerBackgroundBlur = !_viewerBackgroundBlur;
            SaveAppearance();
            ApplyAllBackgrounds();
        };
        menu.Items.Add(blur);
        var dim = new MenuItem { Header = "Darken picture", IsEnabled = HasBackgroundImage };
        foreach (var (value, label) in BackgroundDims)
        {
            var it = new MenuItem { Header = label, IsCheckable = true, IsChecked = Math.Abs(_viewerBackgroundDim - value) < 0.01 };
            it.Click += (_, _) =>
            {
                _viewerBackgroundDim = value;
                SaveAppearance();
                ApplyAllBackgrounds();
            };
            dim.Items.Add(it);
        }
        menu.Items.Add(dim);
    }

    private bool HasBackgroundImage => !string.IsNullOrEmpty(_viewerBackgroundImage) && File.Exists(_viewerBackgroundImage);

    private void PickBackgroundImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Background Picture",
            Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp|All files|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;
        _viewerBackgroundImage = dlg.FileName;
        _panelPicture = true;
        SaveAppearance();
        ApplyAllBackgrounds();
    }

    private void SetViewerBackground(string id)
    {
        _viewerBackground = id;
        SaveAppearance();
        ApplyViewerBackground();
    }

    private void SaveAppearance() =>
        SettingsStore.Save(SettingsStore.Load() with
        {
            ViewerBackground = _viewerBackground,
            ViewerBackgroundImage = _viewerBackgroundImage,
            PanelPicture = _panelPicture,
            ViewerBackgroundBlur = _viewerBackgroundBlur,
            ViewerBackgroundDim = _viewerBackgroundDim
        });

    private void ApplyAllBackgrounds()
    {
        ApplyViewerBackground();
        ApplyPanelBackground();
    }

    /// <summary>Background picture decoded to the given height; null when there is none or it fails to load.</summary>
    private BitmapImage? LoadBackgroundPicture(int decodeHeight)
    {
        if (!HasBackgroundImage) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(_viewerBackgroundImage!);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelHeight = decodeHeight;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Debug.Log($"APPEARANCE: background picture failed [{ex.GetType().Name}] {ex.Message}");
            SetStatus("Background picture could not be loaded: " + ex.Message, error: true);
            return null;
        }
    }

    private void ShowPicture(Image image, System.Windows.Shapes.Rectangle dim, BitmapImage? bmp)
    {
        image.Source = bmp;
        image.Visibility = bmp is null ? Visibility.Collapsed : Visibility.Visible;
        image.Effect = bmp is not null && _viewerBackgroundBlur
            ? new BlurEffect { Radius = 28, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
            : null;
        // Blur pulls the transparent outside into the edges: overscan a little so the borders stay solid.
        image.Margin = _viewerBackgroundBlur ? new Thickness(-40) : new Thickness(0);
        dim.Opacity = _viewerBackgroundDim;
        dim.Visibility = bmp is not null && _viewerBackgroundDim > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Surround of the photo: a plain colour (black while presenting); the picture only on the welcome screen.</summary>
    private void ApplyViewerBackground()
    {
        bool welcome = !_presenting && HintPanel.Visibility == Visibility.Visible;
        var bmp = welcome ? LoadBackgroundPicture(_viewerBackgroundBlur ? 480 : 1600) : null;
        ShowPicture(BgImage, BgDim, bmp);
        if (bmp is not null || _presenting)
        {
            ViewerHost.Background = Brushes.Black;
            return;
        }
        var color = ViewerBackgrounds[0].Color;
        foreach (var b in ViewerBackgrounds)
            if (b.Id == _viewerBackground) color = b.Color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        ViewerHost.Background = brush;
    }

    /// <summary>Picture behind the adjustments panel (tall and narrow: decoded by height).</summary>
    private void ApplyPanelBackground()
    {
        var bmp = _panelPicture ? LoadBackgroundPicture(_viewerBackgroundBlur ? 500 : 1600) : null;
        ShowPicture(PanelBgImage, PanelBgDim, bmp);
        // Over a picture the section headers and buttons let it show through a little.
        if (bmp is not null)
        {
            RightPanel.Resources["ControlBrush"] = Frozen(Color.FromArgb(0xB0, 0x35, 0x36, 0x3E));
            RightPanel.Resources["ControlHoverBrush"] = Frozen(Color.FromArgb(0xD0, 0x41, 0x42, 0x4B));
        }
        else
        {
            RightPanel.Resources.Remove("ControlBrush");
            RightPanel.Resources.Remove("ControlHoverBrush");
        }
        RightPanel.Background = bmp is not null ? Brushes.Black : (Brush)FindResource("PanelBrush");

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
