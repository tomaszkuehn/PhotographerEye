using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using ImgViewer.Services;

namespace ImgViewer;

// Presentation ("lights out") mode with slideshow, and the welcome screen shown while no photo is open.
public partial class MainWindow
{
    private static readonly double[] SlideIntervals = { 2, 3, 5, 8, 12, 20, 30 };
    private const int MaxRecentFolders = 8;

    private bool _presenting;
    private Rect _prePresentBounds;
    private WindowState _prePresentState;
    private ResizeMode _prePresentResize;
    private double _prePresentCaption;
    private bool _prePresentPanel;
    private Visibility _prePresentHist;
    private Visibility _prePresentEdges;
    private DispatcherTimer? _slideTimer;
    private DispatcherTimer? _presentHintTimer;
    private DispatcherTimer? _cursorTimer;
    private double _slideInterval = 5;

    // ---------------------------------------------------------------- presentation

    private void OnPresentationClick(object sender, RoutedEventArgs e)
    {
        CloseSettingsFlyout();
        SetPresentation(!_presenting);
    }

    private void SetPresentation(bool on, bool slideshow = false)
    {
        if (on == _presenting)
        {
            if (on && slideshow) SetSlideshow(true);
            return;
        }
        if (on && _currentPath is null)
        {
            SetStatus("Presentation — open a photo first", error: true);
            return;
        }
        _presenting = on;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (on)
        {
            if (_cropMode) SetCropMode(false);
            CloseSettingsFlyout();
            _prePresentState = WindowState;
            _prePresentBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _prePresentResize = ResizeMode;
            _prePresentCaption = chrome?.CaptionHeight ?? 26;
            _prePresentPanel = RightPanel.Visibility == Visibility.Visible;
            _prePresentHist = HistHost.Visibility;
            _prePresentEdges = BtnEdgeLeft.Visibility;

            TitleBar.Visibility = Visibility.Collapsed;
            ToolbarBar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            RightPanel.Visibility = Visibility.Collapsed;
            HistHost.Visibility = Visibility.Hidden;
            BtnEdgeLeft.Visibility = Visibility.Collapsed;
            BtnEdgeRight.Visibility = Visibility.Collapsed;
            ApplyViewerBackground();

            // Cover the whole monitor (taskbar included): normal state with the monitor's bounds,
            // no resize border and no caption strip (otherwise the top 26 px would drag the window).
            var screen = MonitorBounds();
            if (chrome is not null) chrome.CaptionHeight = 0;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Left = screen.Left; Top = screen.Top; Width = screen.Width; Height = screen.Height;
            Activate();
            Keyboard.Focus(this);
            ShowPresentHint("Presentation   ←/→ photos · P slideshow · ↑/↓ speed · Esc exit");
            StartCursorHide();
        }
        else
        {
            SetSlideshow(false, quiet: true);
            StopCursorHide();
            PresentHint.Visibility = Visibility.Collapsed;
            if (chrome is not null) chrome.CaptionHeight = _prePresentCaption;
            ResizeMode = _prePresentResize;
            Left = _prePresentBounds.Left; Top = _prePresentBounds.Top;
            Width = _prePresentBounds.Width; Height = _prePresentBounds.Height;
            WindowState = _prePresentState;

            TitleBar.Visibility = Visibility.Visible;
            ToolbarBar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            RightPanel.Visibility = _prePresentPanel ? Visibility.Visible : Visibility.Collapsed;
            if (_prePresentPanel || _showHistogram) HistHost.Visibility = _prePresentHist;
            BtnEdgeLeft.Visibility = _files.Count > 0 ? _prePresentEdges : Visibility.Collapsed;
            BtnEdgeRight.Visibility = BtnEdgeLeft.Visibility;
            ApplyViewerBackground();
            SetStatus("Presentation — closed", error: false);
        }
        BtnPresent.Foreground = (Brush)FindResource(on ? "AccentBrush" : "ForegroundBrush");
        Dispatcher.BeginInvoke(new Action(() => Viewer.FitToWindow()), DispatcherPriority.Loaded);
    }

    /// <summary>Keys of presentation mode; F / F11 / P also work outside it. True when the key was handled.</summary>
    private bool HandlePresentationKey(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool plain = Keyboard.Modifiers == ModifierKeys.None;
        if (key == Key.F11 || (plain && key == Key.F) || (_presenting && key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            SetPresentation(!_presenting);
            e.Handled = true;
            return true;
        }
        if (plain && key == Key.P)
        {
            if (!_presenting) SetPresentation(true, slideshow: true);
            else SetSlideshow(_slideTimer is not { IsEnabled: true });
            e.Handled = true;
            return true;
        }
        if (!_presenting) return false;
        StartCursorHide();
        switch (key)
        {
            case Key.Escape:
                SetPresentation(false);
                e.Handled = true;
                return true;
            case Key.Up or Key.Down when plain:
                ChangeSlideInterval(key == Key.Up ? -1 : 1);
                e.Handled = true;
                return true;
            case Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End:
                // Manual navigation: the slideshow waits a full interval before the next photo.
                if (_slideTimer is { IsEnabled: true }) { _slideTimer.Stop(); _slideTimer.Start(); }
                return false;
            case Key.E or Key.C:
                // Editing UI stays hidden while presenting.
                e.Handled = true;
                return true;
        }
        return false;
    }

    private void SetSlideshow(bool on, bool quiet = false)
    {
        if (on && _files.Count < 2)
        {
            ShowPresentHint("Slideshow — only one photo in this folder");
            return;
        }
        if (_slideTimer is null)
        {
            _slideInterval = Math.Clamp(SettingsStore.Load().SlideInterval ?? 5, 1, 120);
            _slideTimer = new DispatcherTimer();
            _slideTimer.Tick += (_, _) => Navigate(1);
        }
        _slideTimer.Interval = TimeSpan.FromSeconds(_slideInterval);
        if (on) _slideTimer.Start(); else _slideTimer.Stop();
        if (!quiet) ShowPresentHint(on ? $"▶  Slideshow — every {_slideInterval:0} s   (P pause · ↑/↓ speed)" : "❚❚  Slideshow paused   (P continue)");
    }

    private void ChangeSlideInterval(int dir)
    {
        if (_slideTimer is null) SetSlideshow(false, quiet: true);
        int i = Array.FindIndex(SlideIntervals, v => v >= _slideInterval - 0.01);
        if (i < 0) i = SlideIntervals.Length - 1;
        i = Math.Clamp(i + dir, 0, SlideIntervals.Length - 1);
        _slideInterval = SlideIntervals[i];
        SettingsStore.Save(SettingsStore.Load() with { SlideInterval = _slideInterval });
        bool running = _slideTimer is { IsEnabled: true };
        _slideTimer!.Interval = TimeSpan.FromSeconds(_slideInterval);
        ShowPresentHint($"Slideshow interval — {_slideInterval:0} s" + (running ? "" : "   (P starts)"));
    }

    private void ShowPresentHint(string text)
    {
        PresentHintText.Text = text;
        PresentHint.Visibility = Visibility.Visible;
        PresentHint.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _presentHintTimer ??= CreateHintTimer();
        _presentHintTimer.Stop();
        _presentHintTimer.Start();
    }

    private DispatcherTimer CreateHintTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            PresentHint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)));
        };
        return t;
    }

    // The mouse pointer disappears after a moment of stillness while presenting.
    private void StartCursorHide()
    {
        if (_cursorTimer is null)
        {
            _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _cursorTimer.Tick += (_, _) =>
            {
                _cursorTimer.Stop();
                if (_presenting) Mouse.OverrideCursor = Cursors.None;
            };
            PreviewMouseMove += (_, _) =>
            {
                if (!_presenting) return;
                if (Mouse.OverrideCursor == Cursors.None) Mouse.OverrideCursor = null;
                _cursorTimer.Stop();
                _cursorTimer.Start();
            };
        }
        _cursorTimer.Stop();
        _cursorTimer.Start();
    }

    private void StopCursorHide()
    {
        _cursorTimer?.Stop();
        if (Mouse.OverrideCursor == Cursors.None) Mouse.OverrideCursor = null;
    }

    /// <summary>Bounds (in WPF units) of the monitor the window is on.</summary>
    private Rect MonitorBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref info))
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var dpi = VisualTreeHelper.GetDpi(this);
        var r = info.Monitor;
        return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY,
            (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // ---------------------------------------------------------------- welcome screen

    private bool WelcomeVisible => HintPanel.Visibility == Visibility.Visible;

    private void SetWelcomeVisible(bool on)
    {
        if (on) BuildRecentList();
        if (WelcomeVisible == on && !on) return;
        HintPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        BtnWelcomePicture.Content = HasBackgroundImage ? "Change background picture…" : "Choose a background picture…";
        ApplyViewerBackground();
        if (on) BtnWelcomeOpen.Focus();
    }

    private void BuildRecentList()
    {
        RecentList.Children.Clear();
        var recent = (SettingsStore.Load().RecentFolders ?? new List<string>()).Where(Directory.Exists).Take(6).ToList();
        RecentSection.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var dir in recent)
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = dir;
            var row = new DockPanel();
            var icon = new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
                Foreground = (Brush)FindResource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            texts.Children.Add(new TextBlock
            {
                Text = dir, FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0)
            });
            row.Children.Add(texts);
            var b = new Button { Content = row, Style = (Style)FindResource("RecentRow"), ToolTip = dir };
            b.Click += (_, _) => OpenPath(dir);
            RecentList.Children.Add(b);
        }
    }

    private static void AddRecentFolder(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { dir = Path.GetFullPath(dir); } catch { return; }
        var s = SettingsStore.Load();
        var list = (s.RecentFolders ?? new List<string>())
            .Where(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, dir);
        if (list.Count > MaxRecentFolders) list.RemoveRange(MaxRecentFolders, list.Count - MaxRecentFolders);
        SettingsStore.Save(s with { RecentFolders = list });
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Open Folder" };
        if (dlg.ShowDialog(this) == true) OpenPath(dlg.FolderName);
    }

    private void OnWelcomePictureClick(object sender, RoutedEventArgs e)
    {
        PickBackgroundImage();
        BtnWelcomePicture.Content = HasBackgroundImage ? "Change background picture…" : "Choose a background picture…";
    }

    private void SetDropHighlight(bool on)
    {
        if (!WelcomeVisible) return;
        DropZone.Stroke = on ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    }
}
