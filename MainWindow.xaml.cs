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
    private readonly ConcurrentDictionary<string, EyeState> _session = new(StringComparer.OrdinalIgnoreCase);
    private List<EyeState> _hist = new();
    private int _histPos = -1;
    private bool _histSuppress;
    private DispatcherTimer? _histCommitTimer;
    private const int HistMax = 10;
    private readonly List<string> _cacheOrder = new();
    private readonly object _cacheOrderLock = new();

    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _applyCts;

    private BitmapSource? _baseBitmap;
    private Raw16Image? _baseRaw16;
    private double _baseEv;
    private ImageInfo? _baseInfo;
    private bool _baseIsFull;
    private bool _saving;
    private Size _fullDecodeBox;
    private int _redecodeSeq;
    private int _histSeq;
    private bool _showHistogram;
    private bool _compareOn;
    private MonoMode _monoMode = MonoMode.None;
    private int _monoSeed;
    private CancellationTokenSource? _redecodeCts;
    private bool _wbPicking;
    private bool _suppressZoomUi;
    private long _lastApplyTicks;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _adjustTimer;
    private readonly DispatcherTimer _redecodeTimer;

    public MainWindow()
    {
        InitializeComponent();

        var settings = SettingsStore.Load();
        ShadowSlider.Value = Math.Clamp(settings.ShadowLift, ShadowSlider.Minimum, ShadowSlider.Maximum);
        GammaSlider.Value = Math.Clamp(settings.Gamma, GammaSlider.Minimum, GammaSlider.Maximum);

        Viewer.ZoomChanged += OnViewerZoomChanged;
        ZoomSlider.ValueChanged += OnZoomSliderValueChanged;
        ShadowSlider.ValueChanged += OnAdjustmentChanged;
        GammaSlider.ValueChanged += OnAdjustmentChanged;
        HistogramPanel.StretchEdited += OnManualStretchChanged;
        ShadowSlider.PreviewMouseLeftButtonDown += OnAdjustSliderDown;
        GammaSlider.PreviewMouseLeftButtonDown += OnAdjustSliderDown;
        ExpSlider.ValueChanged += OnPanelSliderChanged;
        ContrastSlider.ValueChanged += OnPanelSliderChanged;
        TempSlider.ValueChanged += OnPanelSliderChanged;
        SatSlider.ValueChanged += OnPanelSliderChanged;
        ClaritySlider.ValueChanged += OnPanelSliderChanged;
        SharpenSlider.ValueChanged += OnPanelSliderChanged;
        HighlightsSlider.ValueChanged += OnPanelSliderChanged;
        WhitesSlider.ValueChanged += OnPanelSliderChanged;
        BlacksSlider.ValueChanged += OnPanelSliderChanged;
        PanelShadowSlider.ValueChanged += OnPanelSliderChanged;
        DehazeSlider.ValueChanged += OnPanelSliderChanged;
        DenoiseSlider.ValueChanged += OnPanelSliderChanged;
        ColorNoiseSlider.ValueChanged += OnPanelSliderChanged;
        foreach (var s in new[] { ExpSlider, ContrastSlider, TempSlider, SatSlider, ClaritySlider, SharpenSlider, HighlightsSlider, WhitesSlider, BlacksSlider, PanelShadowSlider, DehazeSlider, DenoiseSlider, ColorNoiseSlider })
        {
            s.PreviewMouseLeftButtonDown += OnAdjustSliderDown;
        }
        ShadowSlider.ValueChanged += OnToolbarShadowSynced;
        Viewer.PreviewMouseLeftButtonDown += OnViewerPickClick;
        StateChanged += (_, _) =>
        {
            if (BtnWinMax is not null)
                BtnWinMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE739";
        };

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

        _adjustTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
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
        Debug.Log($"GPU: {Adjust16.GpuStatus}");
        SetToolbarMinWidth();
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
            SetStatus("No images in folder (jpg, jpeg, png)", error: true);
            CounterText.Text = "– / –";
            _index = -1;
            BtnEdgeLeft.Visibility = Visibility.Collapsed;
            BtnEdgeRight.Visibility = Visibility.Collapsed;
            return;
        }
        BtnEdgeLeft.Visibility = Visibility.Visible;
        BtnEdgeRight.Visibility = Visibility.Visible;

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
        if (_currentPath is not null)
        {
            CommitHistNow();
            var cur = CaptureEyeState();
            _session[_currentPath] = cur with { History = new List<EyeState>(_hist), HistoryPos = _histPos };
        }
        _currentPath = path;
        _monoSeed = MonoSeed(path);
        _hist = new List<EyeState>();
        _histPos = -1;
        _baseBitmap = null;
        _baseRaw16 = null;
        _baseInfo = null;
        _fullDecodeBox = default;
        _redecodeSeq++;
        _redecodeCts?.Cancel();

        CounterText.Text = $"{idx + 1} / {_files.Count}";
        Title = $"{Path.GetFileName(path)} — {idx + 1}/{_files.Count} — PhotographerEye";
        ResetAdjustments(silent: true);
        if (_session.TryGetValue(path, out var remembered))
        {
            ApplyEyeStateSuppressed(remembered);
            InitHistory(remembered);
        }
        else if (EyeFile.TryLoad(path, out var st))
        {
            ApplyEyeStateSuppressed(st);
            InitHistory(st);
        }
        else
        {
            InitHistory(CaptureEyeState());
        }
        _showHistogram = false;
        HistHost.Visibility = Visibility.Hidden;
        HintPanel.Visibility = Visibility.Collapsed;
        SetStatus("Loading…", error: false);

        _ = Task.Run(() => LoadWorkerAsync(path, ct));
    }

    private async Task LoadWorkerAsync(string path, CancellationToken ct)
    {
        try
        {
            await _decodeGate.WaitAsync(ct);
            try
            {
                var info = ImageLoader.GetInfo(path);
                ct.ThrowIfCancellationRequested();

                bool raw = RawDecoder.IsRaw(path);
                BitmapSource full;
                if (!raw && _previewCache.TryGetValue(path, out var cached))
                {
                    full = cached;
                }
                else
                {
                    full = ImageLoader.LoadScaled(path, info, info.Width, info.Height);
                    if (!raw)
                    {
                        _previewCache[path] = full;
                        TouchCache(path);
                        EvictCache(path);
                    }
                }

                ct.ThrowIfCancellationRequested();

                var nativeSize = new Size(info.Width, info.Height);
                await Dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrent(path)) SetBase(path, info, full, true, nativeSize);
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
            Debug.Log($"LOAD ERROR: {Path.GetFileName(path)} [{ex.GetType().Name}] {ex.Message}\n{ex.StackTrace}");
            await Dispatcher.BeginInvoke(() =>
            {
                if (!IsCurrent(path)) return;
                SetStatus($"Error: {ex.Message}", error: true);
            });
        }
    }

    private void SetBase(string path, ImageInfo info, BitmapSource raw, bool isFull, Size requestedBox)
    {
        if (!IsCurrent(path)) return;
        _baseBitmap = raw;
        _baseInfo = info;
        _baseIsFull = isFull;
        _baseRaw16 = RawDecoder.IsRaw(path) ? RawDecoder.GetCached16(path, isFull) : null;
        _baseEv = _baseRaw16 is not null ? RawDecoder.GetBaseEv(path) : 0.0;

        if (isFull)
        {
            _fullDecodeBox = requestedBox;
            string decoded = raw.PixelWidth < info.Width ? $" • decoder: {raw.PixelWidth}×{raw.PixelHeight} px" : "";
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
                    if (IsCurrent(path)) SetStatus($"Error: {ex.Message}", error: true);
                });
            }
        });
    }

    private void ReapplyAdjustments(string path, ImageInfo info)
    {
        var raw = _baseBitmap;
        var raw16 = _baseRaw16;
        if (raw is null) return;

        _lastApplyTicks = Environment.TickCount64;
        double s = ShadowSlider.Value / 100.0;
        double g = GammaSlider.Value;
        HistogramPanel.SetEvConverters(
            u => PhotoOps.DisplayFractionToEv(u, g),
            ev => PhotoOps.EvToDisplayFraction(ev, g));
        var manual = HistogramPanel.Snapshot();

        bool neutralRaw = raw16 is not null && Math.Abs(s) <= 0.0001 && Math.Abs(g - 1.0) < 0.0005 &&
            !manual.HasEffect && _monoMode == MonoMode.None && PanelExtras() is null;
        if (raw16 is not null
                ? neutralRaw
                : AdjustmentsProcessor.IsNeutral(s, g) && !manual.HasEffect && _monoMode == MonoMode.None && PanelExtras() is null)
        {
            _applyCts?.Cancel();
            Viewer.SetSource(raw, info.DisplaySize);
            UpdateHistogram(path, raw);
            return;
        }

        var extra = PanelExtras();
        _applyCts?.Cancel();
        _applyCts?.Dispose();
        _applyCts = new CancellationTokenSource();
        var ct = _applyCts.Token;
        double baseEv = _baseEv;

        _ = Task.Run(() =>
        {
            try
            {
                if (raw16 is not null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var rendered = LinearRender.ApplyGain(raw16, baseEv + (extra?.Exposure ?? 0.0), extra?.Temperature ?? 0.0, ct);
                    var extra2 = StripExposureTemp(extra);
                    var corrected = Adjust16.TryApplyGpu(rendered, s, g, manual, out var hist, mono: _monoMode, grainSeed: _monoSeed, extra: extra2, ct: ct);
                    if (corrected is null)
                        corrected = Adjust16.Apply(rendered, s, g, manual, out hist, mono: _monoMode, grainSeed: _monoSeed, extra: extra2, ct: ct);
                    sw.Stop();
                    Debug.Log($"APPLY16: {sw.ElapsedMilliseconds} ms size={raw16.W}x{raw16.H} s={s:0.##} g={g:0.##} baseEv={baseEv:0.00} manual={manual} mono={_monoMode:D} extra={extra2 is not null}");
                    if (ct.IsCancellationRequested) return;
                    var disp = corrected.ToDisplay(dither: true);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!IsCurrent(path) || !ReferenceEquals(_baseRaw16, raw16)) return;
                        if (_compareOn) return;
                        Viewer.SetSource(disp, info.DisplaySize);
                        if (hist is not null) ApplyHistogram(path, hist);
                    });
                    return;
                }

                var sw8 = System.Diagnostics.Stopwatch.StartNew();
                var processed = AdjustmentsProcessor.Apply(raw, s, g, manual, out var hist8, mono: _monoMode, grainSeed: _monoSeed, extra: extra);
                sw8.Stop();
                Debug.Log($"APPLY: {sw8.ElapsedMilliseconds} ms size={raw.PixelWidth}x{raw.PixelHeight} s={s:0.##} g={g:0.##} manual={manual} mono={_monoMode:D} extra={extra is not null}");
                if (processed is null || ct.IsCancellationRequested) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrent(path) || !ReferenceEquals(_baseBitmap, raw)) return;
                    if (_compareOn)
                    {
                        return;
                    }
                    Viewer.SetSource(processed, info.DisplaySize);
                    if (hist8 is not null) ApplyHistogram(path, hist8);
                });
            }
            catch (Exception)
            {
            }
        });
    }

    private void UpdateHistogram(string path, BitmapSource src)
    {
        int seq = Interlocked.Increment(ref _histSeq);
        _ = Task.Run(() =>
        {
            var hist = AdjustmentsProcessor.ComputeHistogram(src);
            if (hist is null) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (seq != Volatile.Read(ref _histSeq) || !IsCurrent(path)) return;
                ApplyHistogram(path, hist);
            });
        });
    }

    private void ApplyHistogram(string path, HistogramData? hist)
    {
        if (!IsCurrent(path)) return;
        if (hist is not null) HistogramPanel.SetHistogram(hist.Lum, hist.R, hist.G, hist.B);
        HistHost.Visibility = hist is not null && _showHistogram && RightPanel.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Hidden;
    }

    private void OnHistCloseClick(object sender, RoutedEventArgs e)
    {
        _showHistogram = false;
        HistHost.Visibility = Visibility.Hidden;
    }

    private void OnHistResetClick(object sender, RoutedEventArgs e)
    {
        HistogramPanel.ClearManualStretch();
        UpdateSaveState();
        ReapplyAdjustments();
        SetStatus("Histogram stretch points removed", error: false);
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void UpdateUndoButtons()
    {
        if (BtnUndo is null || BtnRedo is null) return;
        BtnUndo.IsEnabled = _histPos > 0;
        BtnRedo.IsEnabled = _histPos < _hist.Count - 1;
    }

    private void SyncSessionHist()
    {
        if (_currentPath is null) return;
        var cur = CaptureEyeState();
        _session[_currentPath] = cur with { History = new List<EyeState>(_hist), HistoryPos = _histPos };
    }

    private void ScheduleHistCommit()
    {
        if (_histSuppress || _currentPath is null) return;
        if (_histCommitTimer is null)
        {
            _histCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _histCommitTimer.Tick += (s, e) => CommitHistNow();
        }
        _histCommitTimer.Stop();
        _histCommitTimer.Start();
    }

    private void CommitHistNow()
    {
        _histCommitTimer?.Stop();
        if (_histSuppress || _currentPath is null) return;
        var cur = CaptureEyeState() with { History = null, HistoryPos = null };
        bool eq = _histPos >= 0 && _histPos < _hist.Count && HistEqual(_hist[_histPos], cur);
        Debug.Log($"HIST: commit count={_hist.Count} pos={_histPos} exp={cur.Exposure:0.###} sh={cur.ShadowLift:0.#} knots={cur.StretchEv?.Length ?? 0} eq={eq}");
        if (eq)
        {
            UpdateUndoButtons();
            return;
        }
        if (_histPos < _hist.Count - 1) _hist.RemoveRange(_histPos + 1, _hist.Count - 1 - _histPos);
        _hist.Add(cur);
        if (_hist.Count > HistMax) _hist.RemoveAt(0);
        _histPos = _hist.Count - 1;
        SyncSessionHist();
        UpdateUndoButtons();
    }

    private static bool HistEqual(EyeState a, EyeState b)
    {
        if (a.Version != b.Version || a.Mono != b.Mono) return false;
        if (Math.Abs(a.ShadowLift - b.ShadowLift) > 1e-9 || Math.Abs(a.Gamma - b.Gamma) > 1e-9 ||
            Math.Abs(a.Exposure - b.Exposure) > 1e-9 || Math.Abs(a.Contrast - b.Contrast) > 1e-9 ||
            Math.Abs(a.Temperature - b.Temperature) > 1e-9 || Math.Abs(a.Saturation - b.Saturation) > 1e-9 ||
            Math.Abs(a.Highlights - b.Highlights) > 1e-9 || Math.Abs(a.Whites - b.Whites) > 1e-9 ||
            Math.Abs(a.Blacks - b.Blacks) > 1e-9 || Math.Abs(a.Clarity - b.Clarity) > 1e-9 ||
            Math.Abs(a.Sharpen - b.Sharpen) > 1e-9 || Math.Abs(a.Dehaze - b.Dehaze) > 1e-9 ||
            Math.Abs(a.Denoise - b.Denoise) > 1e-9 || Math.Abs(a.ColorNoise - b.ColorNoise) > 1e-9) return false;
        return KnotsEqual(a.StretchEv, b.StretchEv);
    }

    private static bool KnotsEqual(double[][]? x, double[][]? y)
    {
        if (x is null || y is null) return (x is null || x.Length == 0) && (y is null || y.Length == 0);
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i].Length != y[i].Length) return false;
            for (int j = 0; j < x[i].Length; j++)
                if (Math.Abs(x[i][j] - y[i][j]) > 1e-9) return false;
        }
        return true;
    }

    private void InitHistory(EyeState st)
    {
        if (st.History is { Count: > 0 } h)
        {
            _hist = new List<EyeState>(h);
            _histPos = Math.Clamp(st.HistoryPos ?? _hist.Count - 1, 0, _hist.Count - 1);
            _hist[_histPos] = st with { History = null, HistoryPos = null };
        }
        else
        {
            _hist = new List<EyeState> { st with { History = null, HistoryPos = null } };
            _histPos = 0;
        }
        Debug.Log($"HIST: init count={_hist.Count} pos={_histPos} exp={_hist[_histPos].Exposure:0.###} eye={st.History is { Count: > 0 }}");
        UpdateUndoButtons();
    }

    private void Undo()
    {
        CommitHistNow();
        if (_currentPath is null || _histPos <= 0) { SetStatus("Nothing to undo", error: false); return; }
        _histPos--;
        var st = _hist[_histPos];
        Debug.Log($"HIST: undo -> pos={_histPos} exp={st.Exposure:0.###} sh={st.ShadowLift:0.#} knots={st.StretchEv?.Length ?? 0}");
        ApplyEyeStateSuppressed(st);
        SyncSessionHist();
        UpdateUndoButtons();
        SetStatus($"Undo — {_hist.Count - 1 - _histPos} steps before this state", error: false);
    }

    private void Redo()
    {
        if (_currentPath is null || _histPos >= _hist.Count - 1) { SetStatus("Nothing to redo", error: false); return; }
        _histPos++;
        var st = _hist[_histPos];
        Debug.Log($"HIST: redo -> pos={_histPos} exp={st.Exposure:0.###}");
        ApplyEyeStateSuppressed(st);
        SyncSessionHist();
        UpdateUndoButtons();
        SetStatus("Redo", error: false);
    }

    private void ApplyEyeStateSuppressed(EyeState st)
    {
        _histSuppress = true;
        try
        {
            ApplyEyeState(st);
        }
        finally
        {
            _histSuppress = false;
        }
    }

    private void OnManualStretchChanged(object? sender, EventArgs e)
    {
        if (_currentPath is null) return;
        _showHistogram = true;
        HistHost.Visibility = RightPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Hidden;
        UpdateSaveState();

        var manual = HistogramPanel.Snapshot();
        bool bake = BtnHistBake.IsChecked == true;
        if (HistogramPanel.IsDragging || !manual.HasEffect || !bake)
        {
            ScheduleApplyWithThrottle();
            ScheduleHistCommit();
            return;
        }

        var (exp, hl, sh, wh, bl) = PhotoOps.KnotsToSliders(manual.Knots);

        _suppressSliderSync = true;
        try
        {
            ExpSlider.Value = Math.Clamp(exp, ExpSlider.Minimum, ExpSlider.Maximum);
            PanelShadowSlider.Value = Math.Clamp(sh, PanelShadowSlider.Minimum, PanelShadowSlider.Maximum);
            HighlightsSlider.Value = Math.Clamp(hl, HighlightsSlider.Minimum, HighlightsSlider.Maximum);
            WhitesSlider.Value = Math.Clamp(wh, WhitesSlider.Minimum, WhitesSlider.Maximum);
            BlacksSlider.Value = Math.Clamp(bl, BlacksSlider.Minimum, BlacksSlider.Maximum);
            ShadowValue.Text = $"{Math.Round(ShadowSlider.Value)}";
            UpdatePanelLabels();
        }
        finally
        {
            _suppressSliderSync = false;
        }

        HistogramPanel.ClearManualStretch();
        UpdateSaveState();
        SetStatus("Histogram stretch transferred to sliders", error: false);
        ReapplyAdjustments();
        ScheduleHistCommit();
    }

    private void ScheduleApplyWithThrottle()
    {
        if (_adjustTimer is null) return;
        long now = Environment.TickCount64;
        if (now - _lastApplyTicks >= 40)
        {
            _lastApplyTicks = now;
            _adjustTimer.Stop();
            ReapplyAdjustments();
        }
        else
        {
            _adjustTimer.Stop();
            _adjustTimer.Start();
        }
    }

    private void OnComparePress(object sender, MouseButtonEventArgs e)
    {
        var btn = (Button)sender;
        btn.CaptureMouse();
        SetCompare(true);
        e.Handled = true;
    }

    private void OnCompareRelease(object sender, MouseButtonEventArgs e)
    {
        ((Button)sender).ReleaseMouseCapture();
        SetCompare(false);
        e.Handled = true;
    }

    private void OnCompareLostCapture(object sender, MouseEventArgs e) => SetCompare(false);

    private void SetCompare(bool on)
    {
        if (_compareOn == on) return;
        _compareOn = on;
        ApplyCompare();
    }

    private void ApplyCompare()
    {
        if (_baseBitmap is null || _baseInfo is not ImageInfo info || _currentPath is null) return;
        if (_compareOn)
        {
            Viewer.SetSource(_baseBitmap, info.DisplaySize);
            UpdateHistogram(_currentPath, _baseBitmap);
            SetStatus("Compare — original without edits", error: false);
        }
        else
        {
            ReapplyAdjustments();
            SetStatus("Ready", error: false);
        }
    }

    private bool IsCurrent(string path) =>
        string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase) && _index >= 0;

    private static int MonoSeed(string path)
    {
        uint h = 2166136261u;
        foreach (char ch in path.ToUpperInvariant())
        {
            h ^= ch;
            h *= 16777619u;
        }
        return unchecked((int)h);
    }

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
            while (_cacheOrder.Count > 3)
            {
                string oldest = _cacheOrder[0];
                if (oldest == currentKey)
                {
                    if (_cacheOrder.Count < 4) break;
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

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveCurrentImage();

    private void UpdateSaveState()
    {
        if (BtnSave is null) return;
        bool modified = Math.Abs(ShadowSlider.Value) > 0.0001 ||
                        Math.Abs(GammaSlider.Value - 1.0) > 0.0005 ||
                        HistogramPanel.HasManualEffect ||
                        _monoMode != MonoMode.None ||
                        PanelExtras() is not null;
        BtnSave.IsEnabled = modified && _currentPath is not null;
    }

    private void SaveCurrentImage()
    {
        string? path = _currentPath;
        if (path is null || _files.Count == 0 || _saving) return;

        string dir = Path.GetDirectoryName(path)!;
        string baseName = Path.GetFileNameWithoutExtension(path);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = dir,
            FileName = $"{baseName}_eye",
            DefaultExt = "jpg",
            Filter = "JPG (quality 90%)|*.jpg|TIFF 16-bit|*.tif",
            Title = "Save Photo"
        };
        Debug.Log("SAVE-DIALOG: opening");
        var ok = dialog.ShowDialog(this);
        Debug.Log($"SAVE-DIALOG: result={ok} file={dialog.FileName}");
        if (ok != true) return;

        bool tiff = dialog.FilterIndex == 2;
        string dest = dialog.FileName;
        CommitHistNow();
        var eyeState = CaptureEyeState() with { History = new List<EyeState>(_hist), HistoryPos = _histPos };
        bool rawFile = RawDecoder.IsRaw(path);

        _saving = true;
        BtnSave.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        double s = ShadowSlider.Value / 100.0;
        double g = GammaSlider.Value;
        var manual = HistogramPanel.Snapshot();
        var extra = PanelExtras();

        _ = Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (rawFile)
                {
                    var base16 = RawDecoder.Decode16(path, halfSize: false);
                    var rendered = LinearRender.ApplyGain(base16, RawDecoder.GetBaseEv(path) + (extra?.Exposure ?? 0.0), extra?.Temperature ?? 0.0);
                    var corrected = Adjust16.TryApplyGpu(rendered, s, g, manual, out _, mono: _monoMode, grainSeed: _monoSeed, extra: StripExposureTemp(extra));
                    if (corrected is null)
                        corrected = Adjust16.Apply(rendered, s, g, manual, out _, mono: _monoMode, grainSeed: _monoSeed, extra: StripExposureTemp(extra));
                    SaveEncoded(corrected, dest, tiff);
                    Debug.Log($"SAVE: {dest} {sw.ElapsedMilliseconds} ms out={corrected.W}x{corrected.H} raw16 tiff16={tiff}");
                }
                else
                {
                    var info = ImageLoader.GetInfo(path);
                    var full = ImageLoader.LoadScaled(path, info, info.Width, info.Height);
                    var processed = AdjustmentsProcessor.Apply(full, s, g, manual, out _, mono: _monoMode, grainSeed: _monoSeed, extra: extra);
                    if (processed is null) throw new InvalidOperationException("Failed to process image");
                    SaveEncoded(processed, dest, tiff);
                    Debug.Log($"SAVE: {dest} {sw.ElapsedMilliseconds} ms out={processed.PixelWidth}x{processed.PixelHeight} manual={manual} mono={_monoMode:D} tiff16={tiff}");
                }
                try
                {
                    EyeFile.Save(path, eyeState);
                }
                catch (Exception eyeEx)
                {
                    Debug.Log($"EYE ERROR: {Path.GetFileName(path)} [{eyeEx.GetType().Name}] {eyeEx.Message}");
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrent(path)) SetStatus($"Zapisano: {Path.GetFileName(dest)} ({(tiff ? "TIFF 16-bit" : "JPG 90%")})", error: false);
                    Mouse.OverrideCursor = null;
                });
            }
            catch (Exception ex)
            {
                Debug.Log($"SAVE ERROR: [{ex.GetType().Name}] {ex.Message}\n{ex.StackTrace}");
                Dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrent(path)) SetStatus($"Save error: {ex.Message}", error: true);
                    Mouse.OverrideCursor = null;
                });
            }
            finally
            {
                _saving = false;
                Dispatcher.BeginInvoke(UpdateSaveState);
            }
        });
    }

    public static void SaveEncoded(Raw16Image image, string destPath, bool tiff16)
    {
        if (tiff16)
        {
            WriteTiff16(destPath, image.ToInterleaved(), image.W, image.H);
            return;
        }
        SaveEncoded(image.ToDisplay(dither: false), destPath, false);
    }

    public static void SaveEncoded(BitmapSource image, string destPath, bool tiff16)
    {
        if (!tiff16)
        {
            var jpg = new JpegBitmapEncoder { QualityLevel = 90 };
            jpg.Frames.Add(BitmapFrame.Create(image));
            using var fs = File.Create(destPath);
            jpg.Save(fs);
            return;
        }

        int w = image.PixelWidth;
        int h = image.PixelHeight;
        var src = new byte[w * h * 4];
        image.CopyPixels(src, w * 4, 0);
        var dst = new ushort[w * h * 3];
        for (int i = 0, o = 0; i < src.Length; i += 4, o += 3)
        {
            dst[o] = (ushort)(src[i + 2] * 257);
            dst[o + 1] = (ushort)(src[i + 1] * 257);
            dst[o + 2] = (ushort)(src[i] * 257);
        }
        WriteTiff16(destPath, dst, w, h);
    }

    public static void WriteTiff16(string destPath, ushort[] pixels, int w, int h)
    {
        int payload = pixels.Length * 2;
        const int ifdTags = 13;
        const int dataStart = 170;
        const int stripStart = 198;
        byte[] data = new byte[stripStart + payload];
        data[0] = (byte)'I';
        data[1] = (byte)'I';
        data[2] = 42;
        data[3] = 0;
        data[4] = 8;

        void U16(int at, int v)
        {
            data[at] = (byte)v;
            data[at + 1] = (byte)(v >> 8);
        }
        void U32(int at, int v)
        {
            data[at] = (byte)v;
            data[at + 1] = (byte)(v >> 8);
            data[at + 2] = (byte)(v >> 16);
            data[at + 3] = (byte)(v >> 24);
        }
        void Tag(int i, int id, int type, int count, int value)
        {
            int at = 10 + i * 12;
            U16(at, id);
            U16(at + 2, type);
            U32(at + 4, count);
            U32(at + 8, value);
        }

        data[8] = (byte)ifdTags;
        Tag(0, 256, 3, 1, w);
        Tag(1, 257, 3, 1, h);
        Tag(2, 258, 3, 3, dataStart);
        Tag(3, 259, 3, 1, 1);
        Tag(4, 262, 3, 1, 2);
        Tag(5, 273, 4, 1, stripStart);
        Tag(6, 277, 3, 1, 3);
        Tag(7, 278, 3, 1, h);
        Tag(8, 279, 4, 1, payload);
        Tag(9, 282, 5, 1, dataStart + 8);
        Tag(10, 283, 5, 1, dataStart + 16);
        Tag(11, 296, 3, 1, 2);
        Tag(12, 339, 3, 3, 192);

        U32(10 + 13 * 12, 0);
        U16(dataStart, 16);
        U16(dataStart + 2, 16);
        U16(dataStart + 4, 16);
        U32(dataStart + 8, 96);
        U32(dataStart + 12, 1);
        U32(dataStart + 16, 96);
        U32(dataStart + 20, 1);
        U16(192, 1);
        U16(194, 1);
        U16(196, 1);

        for (int i = 0; i < pixels.Length; i++)
        {
            U16(stripStart + i * 2, pixels[i]);
        }

        File.WriteAllBytes(destPath, data);
    }

    private void OnWinMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnWinMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnWinCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnZoomInClick(object sender, RoutedEventArgs e) => Viewer.SetZoom(Viewer.Zoom * 1.25);
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Viewer.SetZoom(Viewer.Zoom / 1.25);
    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        Debug.Log("OnFitClick");
        Viewer.FitToWindow();
    }
    private void On100Click(object sender, RoutedEventArgs e) => Viewer.SetZoom(1.0);

    private void OnOpenClick(object sender, RoutedEventArgs e) => ShowOpenDialog();

    private void SetToolbarMinWidth()
    {
        ToolbarDock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Debug.Log($"MINW: desired={ToolbarDock.DesiredSize.Width:0.#}x{ToolbarDock.DesiredSize.Height:0.#} actual={ToolbarDock.ActualWidth:0.#}x{ToolbarDock.ActualHeight:0.#} minWidth={MinWidth:0.#}");
        double w = ToolbarDock.DesiredSize.Width;
        if (w > 0)
        {
            MinWidth = Math.Ceiling(w) + 40;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.ContextMenu is not ContextMenu menu) return;
        menu.PlacementTarget = sender as Button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Image",
            Filter = "Images|*.jpg;*.jpeg;*.png|RAW|*.rw2;*.nef;*.nrw;*.arw;*.srf;*.sr2;*.cr2;*.cr3;*.crw;*.orf;*.raf;*.pef;*.ptx;*.dng;*.srw;*.x3f;*.3fr;*.fff;*.iiq;*.mos;*.erf;*.kdc;*.dcr;*.mef;*.rwl|All files|*.*",
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
        if (_suppressSliderSync) return;
        _showHistogram = true;
        ShadowValue.Text = $"{Math.Round(ShadowSlider.Value)}";
        GammaValue.Text = GammaSlider.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        UpdateSaveState();
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
        ScheduleHistCommit();
    }

    private void OnPanelToggleClick(object sender, RoutedEventArgs e) => TogglePanel();

    private void TogglePanel()
    {
        bool open = RightPanel.Visibility != Visibility.Visible;
        RightPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BtnPanel.Foreground = (Brush)FindResource(open ? "AccentBrush" : "ForegroundBrush");
        if (open)
        {
            _showHistogram = true;
            HistHost.Visibility = Visibility.Visible;
            ReapplyAdjustments();
        }
        else
        {
            HistHost.Visibility = Visibility.Hidden;
        }
        SetStatus(open ? "Adjustments panel — open (E)" : "Adjustments panel — closed", error: false);
    }

    private PhotoAdjustments? PanelExtras()
    {
        if (ExpSlider is null) return null;
        var a = PhotoAdjustments.FromUi(
            ExpSlider.Value,
            ContrastSlider.Value,
            TempSlider.Value,
            SatSlider.Value,
            ClaritySlider.Value,
            SharpenSlider.Value,
            HighlightsSlider.Value,
            WhitesSlider.Value,
            BlacksSlider.Value,
            DehazeSlider.Value,
            DenoiseSlider.Value,
            ColorNoiseSlider.Value);
        return a.IsDefault ? null : a;
    }

    private static PhotoAdjustments? StripExposureTemp(PhotoAdjustments? a)
    {
        if (a is null) return null;
        return new PhotoAdjustments
        {
            Exposure = 0.0,
            Temperature = 0.0,
            Contrast = a.Contrast,
            Saturation = a.Saturation,
            Clarity = a.Clarity,
            Sharpen = a.Sharpen,
            Highlights = a.Highlights,
            Whites = a.Whites,
            Blacks = a.Blacks,
            Dehaze = a.Dehaze,
            Denoise = a.Denoise,
            ColorNoise = a.ColorNoise
        };
    }

    private bool _syncingShadow;
    private bool _suppressSliderSync;

    private void OnToolbarShadowSynced(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingShadow || PanelShadowSlider is null) return;
        _syncingShadow = true;
        try
        {
            PanelShadowSlider.Value = e.NewValue;
            PanelShadowValue.Text = Math.Round(e.NewValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncingShadow = false;
        }
    }

    private void OnWbPickerClick(object sender, RoutedEventArgs e)
    {
        _wbPicking = !_wbPicking;
        Mouse.OverrideCursor = _wbPicking ? Cursors.Cross : null;
        SetStatus(_wbPicking
            ? "White point eyedropper — click a neutral area of the photo (Esc — cancel)"
            : "White point eyedropper — cancelled", error: false);
    }

    private void OnViewerPickClick(object sender, MouseButtonEventArgs e)
    {
        if (!_wbPicking) return;
        e.Handled = true;
        _wbPicking = false;
        Mouse.OverrideCursor = null;

        if (_baseBitmap is not null && Viewer.TryScreenToPixel(e.GetPosition(Viewer), out double bx, out double by))
        {
            int cx = (int)bx;
            int cy = (int)by;
            int r = 4;
            int x0 = Math.Max(0, cx - r);
            int y0 = Math.Max(0, cy - r);
            int w = Math.Min(_baseBitmap.PixelWidth, cx + r + 1) - x0;
            int h = Math.Min(_baseBitmap.PixelHeight, cy + r + 1) - y0;
            if (w > 0 && h > 0)
            {
                var buf = new byte[w * h * 4];
                _baseBitmap.CopyPixels(new Int32Rect(x0, y0, w, h), buf, w * 4, 0);
                double sr = 0, sg = 0, sb = 0, n = 0;
                for (int i = 0; i < buf.Length - 3; i += 4)
                {
                    if (buf[i + 3] < 16) continue;
                    sb += buf[i];
                    sg += buf[i + 1];
                    sr += buf[i + 2];
                    n++;
                }
                if (n > 0)
                {
                    double t = PhotoOps.WbTemperatureFromSample(sr / n, sg / n, sb / n);
                    TempSlider.Value = Math.Clamp(t * 100.0, TempSlider.Minimum, TempSlider.Maximum);
                    SetStatus($"White balance from white point — temperature {(int)Math.Round(TempSlider.Value)}", error: false);
                    return;
                }
            }
        }
        SetStatus("White point — sampled outside the image", error: true);
    }

    private void OnPanelSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderSync) return;
        if (sender is Slider s && ReferenceEquals(s, PanelShadowSlider) && !_syncingShadow)
        {
            _syncingShadow = true;
            try
            {
                ShadowSlider.Value = e.NewValue;
            }
            finally
            {
                _syncingShadow = false;
            }
        }
        _showHistogram = true;
        HistHost.Visibility = RightPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Hidden;
        UpdatePanelLabels();
        UpdateSaveState();
        if (_adjustTimer is not null)
        {
            _adjustTimer.Stop();
            _adjustTimer.Start();
        }
        ScheduleHistCommit();
    }

    private void UpdatePanelLabels()
    {
        if (ExpValue is null) return;
        ExpValue.Text = (ExpSlider.Value > 0 ? "+" : "") + ExpSlider.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        ContrastValue.Text = ((int)ContrastSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        TempValue.Text = ((int)TempSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        SatValue.Text = ((int)SatSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        PanelShadowValue.Text = Math.Round(PanelShadowSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        HighlightsValue.Text = ((int)HighlightsSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        WhitesValue.Text = ((int)WhitesSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        BlacksValue.Text = ((int)BlacksSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ClarityValue.Text = ((int)ClaritySlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        SharpenValue.Text = ((int)SharpenSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        DehazeValue.Text = ((int)DehazeSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        DenoiseValue.Text = ((int)DenoiseSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ColorNoiseValue.Text = ((int)ColorNoiseSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => ResetAdjustments();

    private void OnMonoToggleClick(object sender, RoutedEventArgs e) => ToggleMono();

    private void ToggleMono()
    {
        if (_currentPath is null) return;
        _monoMode = _monoMode switch
        {
            MonoMode.None => MonoMode.Documentary,
            MonoMode.Documentary => MonoMode.TriX,
            _ => MonoMode.None
        };
        UpdateMonoButton();
        UpdateSaveState();
        ReapplyAdjustments();
        ScheduleHistCommit();
        SetStatus(_monoMode switch
        {
            MonoMode.Documentary => "Black & white (documentary)",
            MonoMode.TriX => "Black & white (Tri-X 400, grain)",
            _ => "Color — current edits"
        }, error: false);
    }

    private void UpdateMonoButton()
    {
        if (BtnMono is null) return;
        BtnMono.Foreground = (Brush)FindResource(_monoMode == MonoMode.None ? "ForegroundBrush" : "AccentBrush");
    }

    private void ResetAdjustments(bool silent = false)
    {
        HistogramPanel.ClearManualStretch();
        ShadowSlider.Value = 0;
        GammaSlider.Value = 1.0;
        if (ExpSlider is not null)
        {
            ExpSlider.Value = 0;
            ContrastSlider.Value = 0;
            TempSlider.Value = 0;
            SatSlider.Value = 0;
            PanelShadowSlider.Value = 0;
            HighlightsSlider.Value = 0;
            WhitesSlider.Value = 0;
            BlacksSlider.Value = 0;
            ClaritySlider.Value = 0;
            SharpenSlider.Value = 0;
            DehazeSlider.Value = 0;
            DenoiseSlider.Value = 0;
            ColorNoiseSlider.Value = 0;
            UpdatePanelLabels();
        }
        _monoMode = MonoMode.None;
        UpdateMonoButton();
        if (!silent)
        {
            SetStatus("Adjustments reset to defaults", error: false);
            ReapplyAdjustments();
        }
    }

    private EyeState CaptureEyeState()
    {
        double gamma = GammaSlider.Value;
        var knots = HistogramPanel.Snapshot().Knots;
        var stretchEv = new double[knots.Count][];
        var stretch = new double[knots.Count][];
        for (int i = 0; i < knots.Count; i++)
        {
            stretchEv[i] = new[] { knots[i].In, knots[i].Out };
            stretch[i] = new[] { PhotoOps.EvToDisplayFraction(knots[i].In, gamma), PhotoOps.EvToDisplayFraction(knots[i].Out, gamma) };
        }
        return new EyeState
        {
            ShadowLift = ShadowSlider.Value,
            Gamma = gamma,
            Stretch = stretch,
            StretchEv = stretchEv,
            Mono = (int)_monoMode,
            Exposure = ExpSlider.Value,
            Contrast = ContrastSlider.Value,
            Temperature = TempSlider.Value,
            Saturation = SatSlider.Value,
            Highlights = HighlightsSlider.Value,
            Whites = WhitesSlider.Value,
            Blacks = BlacksSlider.Value,
            Clarity = ClaritySlider.Value,
            Sharpen = SharpenSlider.Value,
            Dehaze = DehazeSlider.Value,
            Denoise = DenoiseSlider.Value,
            ColorNoise = ColorNoiseSlider.Value
        };
    }

    private void ApplyEyeFile(string path)
    {
        if (!EyeFile.TryLoad(path, out var st)) return;
        ApplyEyeState(st);
    }

    private void ApplyEyeState(EyeState st)
    {
        ShadowSlider.Value = Math.Clamp(st.ShadowLift, ShadowSlider.Minimum, ShadowSlider.Maximum);
        PanelShadowSlider.Value = Math.Clamp(st.ShadowLift, PanelShadowSlider.Minimum, PanelShadowSlider.Maximum);
        GammaSlider.Value = Math.Clamp(st.Gamma, GammaSlider.Minimum, GammaSlider.Maximum);
        _monoMode = (MonoMode)Math.Clamp(st.Mono, 0, (int)MonoMode.TriX);
        UpdateMonoButton();
        if (st.StretchEv is { Length: > 0 })
        {
            var knots = new List<StretchKnot>(st.StretchEv.Length);
            foreach (var p in st.StretchEv)
                if (p is { Length: 2 })
                    knots.Add(new StretchKnot(Math.Clamp(p[0], PhotoOps.ToneEvMin, PhotoOps.ToneEvMax), Math.Clamp(p[1], PhotoOps.ToneEvMin, PhotoOps.ToneEvMax)));
            HistogramPanel.RestoreManualStretch(knots);
        }
        else if (st.Stretch is { Length: > 0 })
        {
            var knots = new List<StretchKnot>(st.Stretch.Length);
            foreach (var p in st.Stretch)
                if (p is { Length: 2 })
                    knots.Add(new StretchKnot(PhotoOps.DisplayFractionToEv(Math.Clamp(p[0], 0.0, 1.0), st.Gamma),
                                              PhotoOps.DisplayFractionToEv(Math.Clamp(p[1], 0.0, 1.0), st.Gamma)));
            HistogramPanel.RestoreManualStretch(knots);
        }
        else
        {
            HistogramPanel.RestoreManualStretch(new List<StretchKnot>());
        }
        ExpSlider.Value = Math.Clamp(st.Exposure, ExpSlider.Minimum, ExpSlider.Maximum);
        ContrastSlider.Value = Math.Clamp(st.Contrast, ContrastSlider.Minimum, ContrastSlider.Maximum);
        TempSlider.Value = Math.Clamp(st.Temperature, TempSlider.Minimum, TempSlider.Maximum);
        double legacySatShift = st.Version == 1 ? 100.0 : 0.0;
        SatSlider.Value = Math.Clamp(st.Saturation - legacySatShift, SatSlider.Minimum, SatSlider.Maximum);
        HighlightsSlider.Value = Math.Clamp(st.Highlights, HighlightsSlider.Minimum, HighlightsSlider.Maximum);
        WhitesSlider.Value = Math.Clamp(st.Whites, WhitesSlider.Minimum, WhitesSlider.Maximum);
        BlacksSlider.Value = Math.Clamp(st.Blacks, BlacksSlider.Minimum, BlacksSlider.Maximum);
        ClaritySlider.Value = Math.Clamp(st.Clarity, ClaritySlider.Minimum, ClaritySlider.Maximum);
        SharpenSlider.Value = Math.Clamp(st.Sharpen, SharpenSlider.Minimum, SharpenSlider.Maximum);
        DehazeSlider.Value = Math.Clamp(st.Dehaze, DehazeSlider.Minimum, DehazeSlider.Maximum);
        DenoiseSlider.Value = Math.Clamp(st.Denoise, DenoiseSlider.Minimum, DenoiseSlider.Maximum);
        ColorNoiseSlider.Value = Math.Clamp(st.ColorNoise, ColorNoiseSlider.Minimum, ColorNoiseSlider.Maximum);
        UpdatePanelLabels();
        ReapplyAdjustments();
    }

    private void OnAdjustSliderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (ReferenceEquals(sender, ShadowSlider)) ShadowSlider.Value = 0;
        else if (ReferenceEquals(sender, GammaSlider)) GammaSlider.Value = 1.0;
        else if (ReferenceEquals(sender, ExpSlider)) ExpSlider.Value = 0;
        else if (ReferenceEquals(sender, ContrastSlider)) ContrastSlider.Value = 0;
        else if (ReferenceEquals(sender, TempSlider)) TempSlider.Value = 0;
        else if (ReferenceEquals(sender, SatSlider)) SatSlider.Value = 0;
        else if (ReferenceEquals(sender, PanelShadowSlider)) PanelShadowSlider.Value = 0;
        else if (ReferenceEquals(sender, HighlightsSlider)) HighlightsSlider.Value = 0;
        else if (ReferenceEquals(sender, WhitesSlider)) WhitesSlider.Value = 0;
        else if (ReferenceEquals(sender, BlacksSlider)) BlacksSlider.Value = 0;
        else if (ReferenceEquals(sender, ClaritySlider)) ClaritySlider.Value = 0;
        else if (ReferenceEquals(sender, SharpenSlider)) SharpenSlider.Value = 0;
        else if (ReferenceEquals(sender, DehazeSlider)) DehazeSlider.Value = 0;
        else if (ReferenceEquals(sender, DenoiseSlider)) DenoiseSlider.Value = 0;
        else if (ReferenceEquals(sender, ColorNoiseSlider)) ColorNoiseSlider.Value = 0;
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

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space && _compareOn)
        {
            SetCompare(false);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_wbPicking)
        {
            _wbPicking = false;
            Mouse.OverrideCursor = null;
            SetStatus("White point eyedropper — cancelled", error: false);
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            return;
        }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Z or Key.Y)
        {
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (e.Key == Key.Y && !shift) Redo();
            else if (shift) Redo();
            else Undo();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is Slider) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase) return;

        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowOpenDialog();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (BtnSave.IsEnabled) SaveCurrentImage();
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
            case Key.B:
                ToggleMono();
                e.Handled = true;
                break;
            case Key.E:
                TogglePanel();
                e.Handled = true;
                break;
            case Key.Space:
                if (_baseBitmap is not null)
                {
                    SetCompare(true);
                }
                e.Handled = true;
                break;
        }
    }
}

