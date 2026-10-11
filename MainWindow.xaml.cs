using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImgViewer.Controls;
using ImgViewer.Imaging;
using ImgViewer.Services;
using Microsoft.Win32;

namespace ImgViewer;

public partial class MainWindow : Window
{
    private string? _dir;
    private List<string> _files = new();
    private int _index = -1;
    private string? _currentPath;

    private readonly ConcurrentDictionary<string, BitmapSource> _previewCache = new();
    private readonly List<string> _cacheOrder = new();
    private readonly object _cacheOrderLock = new();

    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _applyCts;

    private BitmapSource? _baseBitmap;
    private ImageInfo? _baseInfo;
    private bool _baseIsFull;
    private Size _fullDecodeBox;
    private int _redecodeSeq;
    private CancellationTokenSource? _redecodeCts;
    private bool _suppressZoomUi;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _adjustTimer;
    private readonly DispatcherTimer _redecodeTimer;

    public MainWindow()
    {
        InitializeComponent();

        Viewer.ZoomChanged += OnViewerZoomChanged;
        ZoomSlider.ValueChanged += OnZoomSliderValueChanged;
        ShadowSlider.ValueChanged += OnAdjustmentChanged;
        GammaSlider.ValueChanged += OnAdjustmentChanged;
        ShadowSlider.PreviewMouseLeftButtonDown += OnAdjustSliderDown;
        GammaSlider.PreviewMouseLeftButtonDown += OnAdjustSliderDown;

        var settings = SettingsStore.Load();
        ShadowSlider.Value = Math.Clamp(settings.ShadowLift, ShadowSlider.Minimum, ShadowSlider.Maximum);
        GammaSlider.Value = Math.Clamp(settings.Gamma, GammaSlider.Minimum, GammaSlider.Maximum);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SettingsStore.Save(new ViewerSettings
            {
                ShadowLift = ShadowSlider.Value,
                Gamma = GammaSlider.Value
            });
        };

        _adjustTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _adjustTimer.Tick += (_, _) =>
        {
            _adjustTimer.Stop();
            ReapplyAdjustments();
        };

        _redecodeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _redecodeTimer.Tick += (_, _) =>
        {
            _redecodeTimer.Stop();
            OnRedecodeTimer();
        };

        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            _loadCts?.Cancel();
            _applyCts?.Cancel();
            SettingsStore.Save(new ViewerSettings
            {
                ShadowLift = ShadowSlider.Value,
                Gamma = GammaSlider.Value
            });
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Debug.Log($"Tier={RenderCapability.Tier >> 16}");
        var target = Environment.GetCommandLineArgs().Skip(1)
            .FirstOrDefault(a => !a.StartsWith('-') && (File.Exists(a) || Directory.Exists(a)));
        if (target is not null)
        {
            OpenPath(target);
        }
    }

    private void OpenPath(string path)
    {
        _dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (string.IsNullOrEmpty(_dir)) return;

        _previewCache.Clear();
        lock (_cacheOrderLock) _cacheOrder.Clear();
        RefreshFiles();

        if (_files.Count == 0)
        {
            SetStatus("Folder nie zawiera obrazów (jpg, jpeg, png)", error: true);
            CounterText.Text = "– / –";
            _index = -1;
            return;
        }

        int idx = _files.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        SwitchTo(idx >= 0 ? idx : 0);
    }

    private void RefreshFiles()
    {
        _files = _dir is null ? new List<string>() : ImageFolder.List(_dir);
    }

    private void SwitchTo(int idx)
    {
        if (_files.Count == 0) return;
        idx = Math.Clamp(idx, 0, _files.Count - 1);

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _index = idx;
        string path = _files[idx];
        _currentPath = path;
        _baseBitmap = null;
        _baseInfo = null;
        _fullDecodeBox = default;
        _redecodeSeq++;
        _redecodeCts?.Cancel();

        CounterText.Text = $"{idx + 1} / {_files.Count}";
        Title = $"{Path.GetFileName(path)} — {idx + 1}/{_files.Count} — ImgViewer";
        HintPanel.Visibility = Visibility.Collapsed;
        SetStatus("Wczytywanie…", error: false);

        Size previewBox = GetViewportBox(1);
        Size fullBox = GetViewportBox(FullMultiplier());
        _ = Task.Run(() => LoadWorkerAsync(path, ct, previewBox, fullBox));
    }

    private async Task LoadWorkerAsync(string path, CancellationToken ct, Size previewBox, Size fullBox)
    {
        string key = path;
        try
        {
            await _decodeGate.WaitAsync(ct);
            try
            {
                var info = ImageLoader.GetInfo(path);
                await Dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrent(path)) return;
                    if (_previewCache.TryGetValue(key, out var cachedPreview))
                    {
                        SetBase(path, info, cachedPreview, false, previewBox);
                    }
                });

                if (!_previewCache.ContainsKey(key))
                {
                    var preview = ImageLoader.LoadScaled(path, info,
                        Math.Max(1, (int)previewBox.Width), Math.Max(1, (int)previewBox.Height));
                    _previewCache[key] = preview;
                    TouchCache(key);
                    EvictCache(key);
                    bool posted = false;
                    await Dispatcher.BeginInvoke(() =>
                    {
                        if (IsCurrent(path))
                        {
                            SetBase(path, info, preview, false, previewBox);
                            SetStatus("Podgląd — wczytywanie pełnej rozdzielczości…", error: false);
                            posted = true;
                        }
                    });
                    if (!posted) return;
                }

                ct.ThrowIfCancellationRequested();

                var full = ImageLoader.LoadScaled(path, info,
                    Math.Max(1, (int)fullBox.Width), Math.Max(1, (int)fullBox.Height));

                ct.ThrowIfCancellationRequested();

                await Dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrent(path)) return;
                    SetBase(path, info, full, true, fullBox);
                });

                PrefetchNeighbors(path, previewBox);
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.BeginInvoke(() =>
            {
                if (!IsCurrent(path)) return;
                SetStatus($"Błąd: {ex.Message}", error: true);
            });
        }
    }

    private void SetBase(string path, ImageInfo info, BitmapSource raw, bool isFull, Size requestedBox)
    {
        if (!IsCurrent(path)) return;
        _baseBitmap = raw;
        _baseInfo = info;
        _baseIsFull = isFull;

        if (isFull)
        {
            _fullDecodeBox = requestedBox;
            string decoded = raw.PixelWidth < info.Width ? $" • dekoder: {raw.PixelWidth}×{raw.PixelHeight} px" : "";
            SetStatus($"{Path.GetFileName(path)}  •  {info.Width}×{info.Height} px{decoded}", error: false);
        }

        ReapplyAdjustments(path, info);
    }

    private void ReapplyAdjustments()
    {
        if (_baseBitmap is not null && _currentPath is not null && _baseInfo is ImageInfo info)
        {
            ReapplyAdjustments(_currentPath, info);
        }
    }

    private double FullMultiplier() => Math.Clamp(Viewer.Zoom * 1.25, 1.0, 4.0);

    private void OnRedecodeTimer()
    {
        var baseBitmap = _baseBitmap;
        if (_currentPath is null || baseBitmap is null || _baseInfo is not ImageInfo info) return;
        if (!_baseIsFull) return;

        var box = GetViewportBox(FullMultiplier());
        int neededW = Math.Min(info.Width, Math.Max(1, (int)box.Width));
        int neededH = Math.Min(info.Height, Math.Max(1, (int)box.Height));
        if (neededW <= _fullDecodeBox.Width + 64 && neededH <= _fullDecodeBox.Height + 64) return;

        int seq = ++_redecodeSeq;
        string path = _currentPath;
        _redecodeCts?.Cancel();
        _redecodeCts?.Dispose();
        _redecodeCts = new CancellationTokenSource();
        var ct = _redecodeCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await _decodeGate.WaitAsync(ct);
                try
                {
                    Debug.Log($"REDECODE seq={seq} needed={neededW}x{neededH} prevBox={_fullDecodeBox.Width:0}x{_fullDecodeBox.Height:0}");
                    var bmp = ImageLoader.LoadScaled(path, info, neededW, neededH);
                    if (ct.IsCancellationRequested) return;
                    await Dispatcher.BeginInvoke(() =>
                    {
                        if (_redecodeSeq != seq || !IsCurrent(path)) return;
                        SetBase(path, info, bmp, true, new Size(neededW, neededH));
                    });
                }
                finally
                {
                    _decodeGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrent(path)) SetStatus($"Błąd: {ex.Message}", error: true);
                });
            }
        });
    }

    private void ReapplyAdjustments(string path, ImageInfo info)
    {
        var raw = _baseBitmap;
        if (raw is null) return;

        double s = ShadowSlider.Value / 100.0;
        double g = GammaSlider.Value;

        if (AdjustmentsProcessor.IsNeutral(s, g))
        {
            _applyCts?.Cancel();
            Viewer.SetSource(raw, info.DisplaySize);
            return;
        }

        _applyCts?.Cancel();
        _applyCts?.Dispose();
        _applyCts = new CancellationTokenSource();
        var ct = _applyCts.Token;

        _ = Task.Run(() =>
        {
            try
            {
                var processed = AdjustmentsProcessor.Apply(raw, s, g);
                if (processed is null || ct.IsCancellationRequested) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrent(path) || !ReferenceEquals(_baseBitmap, raw)) return;
                    Viewer.SetSource(processed, info.DisplaySize);
                });
            }
            catch (Exception)
            {
            }
        });
    }

    private void PrefetchNeighbors(string path, Size previewBox)
    {
        if (_dir is null) return;
        int baseIdx = _files.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (baseIdx < 0) return;

        foreach (var off in new[] { -1, 1 })
        {
            int n = ((baseIdx + off) % _files.Count + _files.Count) % _files.Count;
            string np = _files[n];
            if (_previewCache.ContainsKey(np)) continue;
            try
            {
                var info = ImageLoader.GetInfo(np);
                var preview = ImageLoader.LoadScaled(np, info,
                    Math.Max(1, (int)previewBox.Width), Math.Max(1, (int)previewBox.Height));
                _previewCache[np] = preview;
                TouchCache(np);
                EvictCache(np);
            }
            catch (Exception)
            {
            }
        }
    }

    private bool IsCurrent(string path) =>
        string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase) && _index >= 0;

    private Size GetViewportBox(double multiplier)
    {
        double dpi = Viewer.PpiFactor;
        double w = Viewer.ActualWidth * dpi * multiplier;
        double h = Viewer.ActualHeight * dpi * multiplier;
        if (w < 320) w = 320;
        if (h < 240) h = 240;
        if (w > 16384) w = 16384;
        if (h > 16384) h = 16384;
        if (Environment.GetEnvironmentVariable("IMGVIEWER_DEBUG") == "1")
        {
            Debug.Log($"box x{multiplier}: viewer={Viewer.ActualWidth:0.#}x{Viewer.ActualHeight:0.#} ppi={dpi:0.###} -> {Math.Ceiling(w):0}x{Math.Ceiling(h):0}");
        }
        return new Size(Math.Ceiling(w), Math.Ceiling(h));
    }

    private void TouchCache(string key)
    {
        lock (_cacheOrderLock)
        {
            _cacheOrder.Remove(key);
            _cacheOrder.Add(key);
        }
    }

    private void EvictCache(string currentKey)
    {
        lock (_cacheOrderLock)
        {
            while (_cacheOrder.Count > 6)
            {
                string oldest = _cacheOrder[0];
                if (oldest == currentKey)
                {
                    if (_cacheOrder.Count < 7) break;
                    oldest = _cacheOrder[1];
                    _cacheOrder.RemoveAt(1);
                }
                else
                {
                    _cacheOrder.RemoveAt(0);
                }
                _previewCache.TryRemove(oldest, out _);
            }
        }
    }

    private void SetStatus(string text, bool error)
    {
        StatusLeft.Text = text;
        StatusLeft.Foreground = (Brush)FindResource(error ? "ErrorBrush" : "MutedBrush");
    }

    private void Navigate(int delta)
    {
        if (_files.Count == 0) return;
        int n = ((_index + delta) % _files.Count + _files.Count) % _files.Count;
        if (n != _index) SwitchTo(n);
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => Navigate(-1);
    private void OnNextClick(object sender, RoutedEventArgs e) => Navigate(1);
    private void OnZoomInClick(object sender, RoutedEventArgs e) => Viewer.SetZoom(Viewer.Zoom * 1.25);
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Viewer.SetZoom(Viewer.Zoom / 1.25);
    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        Debug.Log("OnFitClick");
        Viewer.FitToWindow();
    }
    private void On100Click(object sender, RoutedEventArgs e) => Viewer.SetZoom(1.0);

    private void OnOpenClick(object sender, RoutedEventArgs e) => ShowOpenDialog();

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Otwórz obraz",
            Filter = "Obrazy|*.jpg;*.jpeg;*.png|Wszystkie pliki|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            OpenPath(dlg.FileName);
        }
    }

    private void OnViewerZoomChanged(object? sender, double zoom)
    {
        Debug.Log($"ZoomChanged: zoom={zoom:0.####} suppressed={_suppressZoomUi}");
        if (_suppressZoomUi) return;
        _suppressZoomUi = true;
        try
        {
            ZoomSlider.Minimum = Math.Min(0.1, zoom);
            ZoomSlider.Value = zoom;
            ZoomLabel.Text = $"{Math.Round(zoom * 100)} %";
        }
        finally
        {
            _suppressZoomUi = false;
        }
        _redecodeTimer?.Stop();
        _redecodeTimer?.Start();
    }

    private void OnZoomSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressZoomUi) return;
        Viewer.SetZoom(e.NewValue);
    }

    private void OnAdjustmentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ShadowValue.Text = $"{Math.Round(ShadowSlider.Value)} %";
        GammaValue.Text = GammaSlider.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        if (_adjustTimer is not null)
        {
            _adjustTimer.Stop();
            _adjustTimer.Start();
        }
        if (_saveTimer is not null)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => ResetAdjustments();

    private void ResetAdjustments()
    {
        ShadowSlider.Value = 0;
        GammaSlider.Value = 1.0;
        SetStatus("Korekty przywrócone do wartości domyślnych (Cienie 0, Gamma 1.00)", error: false);
    }

    private void OnAdjustSliderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (ReferenceEquals(sender, ShadowSlider)) ShadowSlider.Value = 0;
        else if (ReferenceEquals(sender, GammaSlider)) GammaSlider.Value = 1.0;
        e.Handled = true;
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            e.Handled = true;
            OpenPath(paths[0]);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) return;

        if (Keyboard.FocusedElement is Slider) return;

        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowOpenDialog();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                Navigate(-1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.PageDown:
                Navigate(1);
                e.Handled = true;
                break;
            case Key.Home:
                SwitchTo(0);
                e.Handled = true;
                break;
            case Key.End:
                SwitchTo(_files.Count - 1);
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                Viewer.SetZoom(Viewer.Zoom * 1.25);
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                Viewer.SetZoom(Viewer.Zoom / 1.25);
                e.Handled = true;
                break;
            case Key.D1 or Key.NumPad1:
                Viewer.SetZoom(1.0);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                Viewer.FitToWindow();
                e.Handled = true;
                break;
            case Key.R:
                ResetAdjustments();
                e.Handled = true;
                break;
        }
    }
}