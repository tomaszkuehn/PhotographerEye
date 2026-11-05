using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImgViewer.Controls;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
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
    /// <summary>What the newest history step changed; the next edit of the same thing replaces that step.</summary>
    private string? _histLastKey;
    private bool _histSuppress;
    private DispatcherTimer? _histCommitTimer;
    private const int HistMax = 50;
    private readonly List<string> _cacheOrder = new();
    private readonly object _cacheOrderLock = new();

    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _applyCts;
    /// <summary>Live render pump: a render is running / controls moved meanwhile / id of the newest render.</summary>
    private bool _renderBusy;
    private bool _renderDirty;
    private bool _renderQueued;
    private int _renderSeq;
    /// <summary>Live RAW preview resolution setting and the downscaled copy last made (source, longer side, copy).</summary>
    private string _previewQuality = PreviewQuality.Window;
    private readonly RenderedPreviewCache _renderedCache = new(RenderedPreviewCache.DefaultDirectory);
    private readonly EyeAutoSave _eyeAutoSave = new();
    /// <summary>A cached render of the current photo is on screen; the embedded RAW preview must not replace it.</summary>
    private bool _instantShown;
    private RenderedSnap? _lastRendered;
    private sealed record RenderedSnap(string Path, EyeState State, double FileBaseEv, double[][]? FileCurve, BitmapSource Bitmap, Size Box);
    private sealed record PreviewCopy(Raw16Image Src, int Long, Raw16Image Copy);
    private volatile PreviewCopy? _previewCopy;
    /// <summary>Longer side the last RAW render used (0 = full image).</summary>
    private int _previewLongUsed;
    private readonly StageCache _stageCache = new();

    private BitmapSource? _baseBitmap;
    private Raw16Image? _baseRaw16;
    private double _baseEv;
    private double _eyeBaseEv = double.NaN;
    private CameraToneCurve? _baseCurve;
    private CameraToneCurve? _eyeCurve;
    private bool _eyeCurveSet;
    private bool _histPending;
    private EyeState? _histPendingSt;
    private ImageInfo? _baseInfo;
    private bool _baseIsFull;
    private bool _saving;
    private Size _fullDecodeBox;
    private Size _baseNativeBox;
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
    private GeoState _geo = GeoState.Default;
    private bool _cropMode;
    private bool _suppressCropCombo;
    private BitmapSource? _lastDisplayed;
    private Size _lastDisplayedBox;
    private BitmapSource? _unGeoSrc;
    private Size _unGeoBox;
    private int _unGeoSeq;
    private BitmapSource? _liveSrc;
    private Size _liveBox;
    private int _liveSeq = -1;
    private string _lastDimsText = "";
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _adjustTimer;
    private readonly DispatcherTimer _redecodeTimer;

    public MainWindow()
    {
        InitializeComponent();
        BuildCoreSections();
        var histToggle = BuildHistogramToggle();
        histToggle.Width = 24;
        histToggle.Height = 18;
        histToggle.Margin = new Thickness(0);
        HistToggleHost.Content = histToggle;
        // the mask colour button sits left of the histogram and moves to the edge when the histogram hides
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(Grid))
            .AddValueChanged(HistHost, (_, _) => UpdateMaskTintSide());
        HistHost.SizeChanged += (_, _) => UpdateMaskTintSide();

        var settings = SettingsStore.Load();
        ShadowSlider.Value = Math.Clamp(settings.ShadowLift, ShadowSlider.Minimum, ShadowSlider.Maximum);
        GammaSlider.Value = Math.Clamp(settings.Gamma, GammaSlider.Minimum, GammaSlider.Maximum);
        _previewQuality = PreviewQuality.Normalize(settings.PreviewQuality);
        _renderedCache.Enabled = settings.RememberPreviews ?? true;
        MnuRememberPreviews.IsChecked = _renderedCache.Enabled;
        _eyeAutoSave.Enabled = settings.AutoSaveEdits ?? true;
        MnuAutoSaveEdits.IsChecked = _eyeAutoSave.Enabled;
        UpdatePreviewQualityMenu();
        InitAppearance(settings);

        Viewer.ZoomChanged += OnViewerZoomChanged;
        CropOverlay.Attach(Viewer);
        ZoomSlider.ValueChanged += OnZoomSliderValueChanged;
        HistogramPanel.StretchEdited += OnManualStretchChanged;
        HistogramPanel.CurvesEdited += OnHistCurvesChanged;
        // Basic adjustment sliders (CoreParams): toolbar sliders re-render directly, panel sliders also update labels.
        foreach (var c in CoreUi())
        {
            if (c.Panel is { } panel)
            {
                panel.ValueChanged += OnPanelSliderChanged;
                panel.PreviewMouseLeftButtonDown += OnAdjustSliderDown;
            }
            if (!ReferenceEquals(c.Panel, c.Slider))
            {
                c.Slider.ValueChanged += OnAdjustmentChanged;
                c.Slider.PreviewMouseLeftButtonDown += OnAdjustSliderDown;
            }
        }
        ShadowSlider.ValueChanged += OnToolbarShadowSynced;
        Viewer.PreviewMouseLeftButtonDown += OnViewerPickClick;
        Viewer.RotateDragged += OnViewerRotateDrag;
        Viewer.RotateEnded += OnViewerRotateEnded;
        Viewer.ViewChanged += (_, _) => { if (_cropMode && !CropOverlay.IsInteracting) CropOverlay.Sync(); };
        CropOverlay.GeometryChanged += OnCropGeometryChanged;
        CropOverlay.RotateChanged += OnCropRotateChanged;
        CropOverlay.InteractionEnded += OnCropInteractionEnded;
        StateChanged += (_, _) =>
        {
            if (BtnWinMax is not null)
                BtnWinMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE739";
        };

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SettingsStore.Save(SettingsStore.Load() with
            {
                ShadowLift = ShadowSlider.Value,
                Gamma = GammaSlider.Value
            });
        };

        _adjustTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _adjustTimer.Tick += (_, _) =>
        {
            _adjustTimer.Stop();
            RequestRender();
        };

        _redecodeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _redecodeTimer.Tick += (_, _) =>
        {
            _redecodeTimer.Stop();
            OnRedecodeTimer();
        };

        InitPlugins();
        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            CommitHistNow();
            CancelBackgroundTasks(quiet: true);
            if (_currentPath is not null) RaiseImageClosed();
            _loadCts?.Cancel();
            _applyCts?.Cancel();
            FlushRendered(sync: true);
            SettingsStore.Save(SettingsStore.Load() with
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
        else
        {
            SetWelcomeVisible(true);
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
            if (_currentPath is null) SetWelcomeVisible(true);
            return;
        }
        BtnEdgeLeft.Visibility = _presenting ? Visibility.Collapsed : Visibility.Visible;
        BtnEdgeRight.Visibility = BtnEdgeLeft.Visibility;
        AddRecentFolder(_dir);

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
            CancelBackgroundTasks(quiet: true);
            RaiseImageClosed();
        }
        FlushRendered(sync: false);
        _instantShown = false;
        _currentPath = path;
        SetCropMode(false);
        _lastDisplayed = null;
        _lastDisplayedBox = default;
        InvalidateUnGeo();
        _lastDimsText = "";
        _monoSeed = MonoSeed(path);
        _hist = new List<EyeState>();
        _histPos = -1;
        _histLastKey = null;
        _histOpOpen = false;
        _histPending = true;
        NotifyHistoryChanged();
        _histPendingSt = null;
        _eyeBaseEv = double.NaN;
        _eyeCurveSet = false;
        _eyeCurve = null;
        _baseCurve = null;
        _baseBitmap = null;
        _baseRaw16 = null;
        // Stage caches of the previous photo (CPU buffers now, GPU buffers off the UI thread).
        _stageCache.Clear();
        _previewCopy = null;
        Adjust16.ClearStageCaches();
        _ = Task.Run(Adjust16.ReleaseDisplayBuffers);
        _baseInfo = null;
        _baseEv = 0.0;
        _fullDecodeBox = default;
        _redecodeSeq++;
        _redecodeCts?.Cancel();

        CounterText.Text = $"{idx + 1} / {_files.Count}";
        Title = $"{Path.GetFileName(path)} — {idx + 1}/{_files.Count} — PhotographerEye";
        ResetAdjustments(silent: true);
        if (_session.TryGetValue(path, out var remembered))
        {
            ApplyEyeStateSuppressed(remembered);
            _histPendingSt = remembered;
        }
        else if (EyeFile.TryLoad(path, out var st))
        {
            ApplyEyeStateSuppressed(st);
            _histPendingSt = st;
        }
        ShowCachedRender(path);
        _showHistogram = false;
        HistHost.Visibility = Visibility.Hidden;
        SetWelcomeVisible(false);
        if (_presenting) ShowPresentHint($"{Path.GetFileName(path)}   {idx + 1} / {_files.Count}");
        StatusRight.Text = "";
        SetStatus("Loading…", error: false);

        _ = Task.Run(() => LoadWorkerAsync(path, idx, ct));
    }

    private async Task LoadWorkerAsync(string path, int idx, CancellationToken ct)
    {
        try
        {
            bool raw = RawDecoder.IsRaw(path);
            ImageInfo info;
            if (raw)
            {
                info = RawDecoder.TryGetInfoHeader(path, out var hdr) ? hdr : ImageLoader.GetInfo(path);
                var preview = RawDecoder.TryDecodePreview(path);
                ct.ThrowIfCancellationRequested();
                if (preview is not null)
                {
                    info = RawDecoder.AlignToPreview(info, preview);
                    await Dispatcher.BeginInvoke(() =>
                    {
                        if (IsCurrent(path)) SetBase(path, info, preview, isFull: false, new Size(preview.PixelWidth, preview.PixelHeight));
                    });
                    PrefetchNeighborPreviews(idx);
                }
            }
            else
            {
                info = ImageLoader.GetInfo(path);
                ct.ThrowIfCancellationRequested();
            }

            await _decodeGate.WaitAsync(ct);
            BitmapSource full;
            try
            {
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

    private void PrefetchNeighborPreviews(int idx)
    {
        if (idx < 0 || _files.Count == 0) return;
        string[] nb =
        {
            _files[(idx + 1) % _files.Count],
            _files[(_files.Count + idx - 1) % _files.Count]
        };
        _ = Task.Run(() =>
        {
            foreach (var p in nb)
            {
                if (string.Equals(p, _currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!RawDecoder.IsRaw(p)) continue;
                RawDecoder.TryDecodePreview(p);
            }
        });
    }

    private double CurrentBaseEv() => double.IsNaN(_eyeBaseEv) ? _baseEv : _eyeBaseEv;

    private CameraToneCurve? CurrentCurve() => _eyeCurveSet ? _eyeCurve : _baseCurve;

    private void SetBase(string path, ImageInfo info, BitmapSource raw, bool isFull, Size requestedBox)
    {
        if (!IsCurrent(path)) return;
        _baseBitmap = raw;
        _baseInfo = info;
        _baseIsFull = isFull;
        if (isFull) _instantShown = false;
        _baseNativeBox = requestedBox.Width > 0 ? requestedBox : new Size(info.Width, info.Height);
        if (isFull)
        {
            _baseRaw16 = RawDecoder.IsRaw(path) ? RawDecoder.GetCached16(path, full: true) : null;
            _baseCurve = _baseRaw16 is not null ? RawDecoder.GetCameraTone(path) : null;
            _baseEv = _baseRaw16 is not null ? RawDecoder.GetBaseEv(path) : 0.0;
            if (double.IsNaN(_eyeBaseEv)) _eyeBaseEv = _baseEv;

            if (_histPending)
            {
                _histPending = false;
                if (_histPendingSt is not null) { InitHistory(_histPendingSt); _histPendingSt = null; }
                else InitHistory(CaptureEyeState());
            }

            _fullDecodeBox = requestedBox;
            string decoded = raw.PixelWidth < info.Width ? $"   decoder: {raw.PixelWidth}×{raw.PixelHeight} px" : "";
            _lastDisplayed = raw;
            _lastDisplayedBox = _baseNativeBox;
            _lastDimsText = $"{Path.GetFileName(path)}   {info.Width}×{info.Height} px{decoded}";
            SetStatus(DimsLine(Path.GetFileName(path), info, decoded), error: false);
        }
        else
        {
            _baseRaw16 = null;
            _baseCurve = null;
            _baseEv = 0.0;
            _lastDisplayed = raw;
            _lastDisplayedBox = _baseNativeBox;
            _lastDimsText = $"{Path.GetFileName(path)}   preview…";
            SetStatus(_lastDimsText, error: false);
        }
        UpdateStatusMeta(path, isFull);
        // The cached render stays until the full image is decoded (or an adjustment is changed).
        if (!isFull && _instantShown) return;
        ReapplyAdjustments(path, info);
    }

    private void ReapplyAdjustments()
    {
        _instantShown = false;
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

        if (_baseRaw16 is { } r16 && _previewLongUsed > 0)
        {
            // Zoomed in past the preview copy: render again with more pixels (or the full image).
            var nbox = _baseNativeBox.Width > 0 ? _baseNativeBox : info.DisplaySize;
            int want = PreviewTargetLong(r16, nbox);
            if (want == 0 || want > _previewLongUsed) RequestRender();
        }
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
        var box = _baseNativeBox.Width > 0 ? _baseNativeBox : info.DisplaySize;
        var displayGeo = _cropMode ? _geo with { CropX = 0.0, CropY = 0.0, CropW = 1.0, CropH = 1.0 } : _geo;

        bool geoAngle = PhotoGeo.IsHeavy(displayGeo);
        bool geoActive = !displayGeo.IsDefault;
        var stSnap = _baseIsFull && !_cropMode && _renderedCache.Enabled ? CaptureEyeState() : null;

        _lastApplyTicks = Environment.TickCount64;
        double s = ShadowSlider.Value / 100.0;
        double g = GammaSlider.Value;
        HistogramPanel.SetEvConverters(
            u => PhotoOps.DisplayFractionToEv(u, g),
            ev => PhotoOps.EvToDisplayFraction(ev, g));
        var manual = HistogramPanel.Snapshot();

        bool baseMatchesFile = Math.Abs(_eyeBaseEv - _baseEv) < 1e-9 && !_eyeCurveSet;
        bool neutralRaw = baseMatchesFile && raw16 is not null && Math.Abs(s) <= 0.0001 && Math.Abs(g - 1.0) < 0.0005 &&
            !manual.HasEffect && _monoMode == MonoMode.None && PanelExtras() is null && MaskExtras() is null && !geoAngle && !PluginHasEffect();
        if (raw16 is not null
                ? neutralRaw
                : !geoAngle && AdjustmentsProcessor.IsNeutral(s, g) && !manual.HasEffect && _monoMode == MonoMode.None && PanelExtras() is null && MaskExtras() is null && !PluginHasEffect())
        {
            _applyCts?.Cancel();
            StoreUnGeo(raw, box);
            var (fast, fastBox) = PhotoGeo.Apply(raw, box, displayGeo);
            SetDisplayed(fast, fastBox);
            RememberRendered(path, stSnap, fast, fastBox);
            if (geoActive) UpdateHistogram(path, fast);
            else UpdateHistogram(path, raw);
            return;
        }

        var extra = PanelExtras();
        _applyCts?.Cancel();
        _applyCts?.Dispose();
        _applyCts = new CancellationTokenSource();
        var ct = _applyCts.Token;
        double baseEv = CurrentBaseEv();
        var toneCurve = CurrentCurve();
        int fullW = info.Width, fullH = info.Height;
        int previewLong = raw16 is not null ? PreviewTargetLong(raw16, box) : 0;
        if (raw16 is not null) _previewLongUsed = previewLong;
        var src16 = raw16 is null ? null : PreviewSource16(raw16, previewLong, peekOnly: true);
        int bufLong = raw16 is not null
            ? (previewLong > 0 ? previewLong : Math.Max(raw16.W, raw16.H))
            : Math.Max(raw.PixelWidth, raw.PixelHeight);
        double pScale = Math.Min(1.0, bufLong / (double)Math.Max(1, Math.Max(fullW, fullH)));
        var job = PluginJob(preview: true, pScale, ct);
        var mono = _monoMode;
        int seed = _monoSeed;
        bool runOutput = !_cropMode;
        int renderSeq = ++_renderSeq;
        _renderBusy = true;
        BeginBusy(null); // fast renders never show it; the clock appears when one runs past 2 s
        string shape = PluginShapeKey();

        _ = Task.Run(() =>
        {
            try
            {
                if (raw16 is not null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var work16 = src16 ?? PreviewSource16(raw16, previewLong, peekOnly: false)!;
                    long downMs = sw.ElapsedMilliseconds;
                    if (ct.IsCancellationRequested) return;
                    var extra2 = StripExposureTemp(extra);
                    HistogramData? hist = null;
                    bool postRan = false;
                    // GPU end to end (resident source, 4 B/px back) unless a plugin or the mask needs the 16-bit buffer.
                    var disp = GpuDisplayAllowed(job)
                        ? Adjust16.TryRenderDisplayGpu(work16, baseEv + (extra?.Exposure ?? 0.0), extra?.Temperature ?? 0.0, extra?.Tint ?? 0.0,
                            s, g, manual, out hist, mono, seed, extra2, ct, toneCurve)
                        : null;
                    bool gpuDisp = disp is not null;
                    if (disp is null)
                    {
                        var corrected = Correct16(work16, baseEv, extra, s, g, manual, mono, seed, toneCurve, job, fullW, fullH, ct, out hist, out bool blended, _stageCache);
                        if (ct.IsCancellationRequested) return;
                        var post = PluginPipeline.ApplyRaw16(corrected, FilterStage.Display, job, fullW, fullH);
                        postRan = post is not null || blended;
                        corrected = post ?? corrected;
                        if (ct.IsCancellationRequested) return;
                        disp = corrected.ToDisplay(dither: true);
                    }
                    sw.Stop();
                    Debug.Log($"APPLY16: {sw.ElapsedMilliseconds} ms (downscale {downMs}) size={work16.W}x{work16.H} of {raw16.W}x{raw16.H} s={s:0.##} g={g:0.##} baseEv={baseEv:0.00} manual={manual} mono={mono:D} extra={extra2 is not null} plugins={job.Report.Ran} gpuDisplay={gpuDisp}");
                    if (ct.IsCancellationRequested) return;
                    var (gdisp, gbox) = PhotoGeo.Apply(disp, box, displayGeo);
                    if (runOutput)
                    {
                        job.OutputGeo = (displayGeo, disp.PixelWidth, disp.PixelHeight);
                        var outB = PluginPipeline.ApplyBitmap(gdisp, FilterStage.Output, job,
                            (int)Math.Round(gdisp.PixelWidth / pScale), (int)Math.Round(gdisp.PixelHeight / pScale));
                        if (outB is not null) { gdisp = outB; postRan = true; }
                    }
                    ReportPluginErrors(path, job);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!IsCurrent(path) || !ReferenceEquals(_baseRaw16, raw16)) return;
                        if (_compareOn || IsStaleRender(ct, shape)) return;
                        StoreUnGeo(disp, box);
                        SetDisplayed(gdisp, gbox);
                        RememberRendered(path, stSnap, gdisp, gbox);
                        if (geoActive || postRan) UpdateHistogram(path, gdisp);
                        else if (hist is not null) ApplyHistogram(path, hist);
                    });
                    return;
                }

                var sw8 = System.Diagnostics.Stopwatch.StartNew();
                var src8 = PluginPipeline.ApplyBitmapLinearStages(raw, job, fullW, fullH) ?? raw;
                var processed = Correct8(src8, extra, s, g, manual, mono, seed, job, fullW, fullH, out var hist8, out bool blended8);
                if (processed is null || ct.IsCancellationRequested) return;
                BitmapSource processedOut = processed;
                var post8 = PluginPipeline.ApplyBitmap(processedOut, FilterStage.Display, job, fullW, fullH);
                bool postRan8 = post8 is not null || blended8;
                processedOut = post8 ?? processedOut;
                sw8.Stop();
                Debug.Log($"APPLY: {sw8.ElapsedMilliseconds} ms size={raw.PixelWidth}x{raw.PixelHeight} s={s:0.##} g={g:0.##} manual={manual} mono={mono:D} extra={extra is not null} plugins={job.Report.Ran}");
                var (gprocessed, gbox8) = PhotoGeo.Apply(processedOut, box, displayGeo);
                if (runOutput)
                {
                    job.OutputGeo = (displayGeo, processedOut.PixelWidth, processedOut.PixelHeight);
                    var outB = PluginPipeline.ApplyBitmap(gprocessed, FilterStage.Output, job,
                        (int)Math.Round(gprocessed.PixelWidth / pScale), (int)Math.Round(gprocessed.PixelHeight / pScale));
                    if (outB is not null) { gprocessed = outB; postRan8 = true; }
                }
                ReportPluginErrors(path, job);
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrent(path) || !ReferenceEquals(_baseBitmap, raw)) return;
                    if (_compareOn || IsStaleRender(ct, shape)) return;
                    StoreUnGeo(processedOut, box);
                    SetDisplayed(gprocessed, gbox8);
                    RememberRendered(path, stSnap, gprocessed, gbox8);
                    if (geoActive || postRan8) UpdateHistogram(path, gprocessed);
                    else if (hist8 is not null) ApplyHistogram(path, hist8);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.Log($"APPLY ERROR: [{ex.GetType().Name}] {ex.Message} {ex.StackTrace}");
            }
            finally
            {
                Dispatcher.BeginInvoke(() =>
                {
                    OnRenderFinished(renderSeq); // may start the next render, whose BeginBusy keeps the clock up
                    EndBusy();
                });
            }
        });
    }

    /// <summary>Longer side of the live preview buffer for <paramref name="full"/> (0 = render the full image).</summary>
    private int PreviewTargetLong(Raw16Image full, Size box)
    {
        double boxLong = Math.Max(box.Width, box.Height);
        return PreviewQuality.TargetLong(_previewQuality, boxLong, Viewer.Zoom, Math.Max(full.W, full.H));
    }

    /// <summary>
    /// Downscaled copy of <paramref name="full"/> for the live preview (the image itself for 0). With
    /// <paramref name="peekOnly"/> only a cached copy is returned (null when it still has to be made, off the UI thread).
    /// </summary>
    private Raw16Image? PreviewSource16(Raw16Image full, int targetLong, bool peekOnly)
    {
        if (targetLong <= 0) return full;
        var c = _previewCopy;
        if (c is not null && ReferenceEquals(c.Src, full) && c.Long == targetLong) return c.Copy;
        if (peekOnly) return null;
        var copy = full.Downscale(targetLong);
        _previewCopy = new PreviewCopy(full, targetLong, copy);
        return copy;
    }

    /// <summary>Context of the cached-render signature: this build of the app and the loaded plugins.</summary>
    private string RenderedContext() =>
        typeof(MainWindow).Assembly.ManifestModule.ModuleVersionId + "|" +
        string.Join(";", _pluginReg.Plugins.Select(p => p.Id + "@" + p.Version).OrderBy(x => x, StringComparer.Ordinal)) + "|" +
        string.Join(";", _pluginReg.Settings.Select(s => s.FullId + "=" + s.D.Value).OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Latest finished render of the current photo (stored in the cache when the photo is left).</summary>
    private void RememberRendered(string path, EyeState? st, BitmapSource bmp, Size box)
    {
        if (st is null || !bmp.IsFrozen || _compareOn || _cropMode || !_baseIsFull || !IsCurrent(path)) return;
        _lastRendered = new RenderedSnap(path, st, _baseEv, _baseCurve?.KnotPairs(), bmp, box);
    }

    private void FlushRendered(bool sync)
    {
        if (_lastRendered is not { } r) return;
        _lastRendered = null;
        if (!_renderedCache.Enabled) return;
        var meta = RenderedPreviewCache.MakeMeta(r.Path, r.State, RenderedContext(), r.FileBaseEv, r.FileCurve, r.Box);
        if (meta is null) return;
        _renderedCache.Put(meta, r.Bitmap);
        if (sync) _renderedCache.Persist(meta, r.Bitmap);
        else _ = Task.Run(() => _renderedCache.Persist(meta, r.Bitmap));
    }

    /// <summary>Shows the cached render of <paramref name="path"/> when its file and edits are unchanged.</summary>
    private void ShowCachedRender(string path)
    {
        if (!_renderedCache.Enabled) return;
        var cur = CaptureEyeState() with { ToneCurve = _eyeCurveSet ? _eyeCurve?.KnotPairs() : null };
        string ctx = RenderedContext();
        bool evKnown = !double.IsNaN(_eyeBaseEv), curveKnown = _eyeCurveSet;
        if (!_renderedCache.TryGet(path, m => RenderedPreviewCache.Signature(RenderedPreviewCache.StateFor(cur, m, evKnown, curveKnown), ctx),
                out var bmp, out var box) || bmp is null) return;
        SetDisplayed(bmp, box);
        _instantShown = true;
        Debug.Log($"CACHED RENDER: {Path.GetFileName(path)} {bmp.PixelWidth}x{bmp.PixelHeight}");
    }

    private void OnAutoSaveEditsClick(object sender, RoutedEventArgs e)
    {
        _eyeAutoSave.Enabled = MnuAutoSaveEdits.IsChecked == true;
        SettingsStore.Save(SettingsStore.Load() with { AutoSaveEdits = _eyeAutoSave.Enabled });
        if (_eyeAutoSave.Enabled) SyncSessionHist();
        SetStatus(_eyeAutoSave.Enabled ? "Edits are saved as you work (_eye_ folder next to the photos)" : "Edits are saved only with Save Photo", error: false);
    }

    private void OnRememberPreviewsClick(object sender, RoutedEventArgs e)
    {
        _renderedCache.Enabled = MnuRememberPreviews.IsChecked == true;
        if (!_renderedCache.Enabled) { _lastRendered = null; _renderedCache.Clear(disk: false); }
        SettingsStore.Save(SettingsStore.Load() with { RememberPreviews = _renderedCache.Enabled });
        SetStatus(_renderedCache.Enabled ? "Rendered previews are remembered" : "Rendered previews are not remembered", error: false);
    }

    private void OnClearPreviewCacheClick(object sender, RoutedEventArgs e)
    {
        CloseSettingsFlyout();
        _lastRendered = null;
        _renderedCache.Clear(disk: true);
        SetStatus("Preview cache cleared", error: false);
    }

    private void SetDisplayed(BitmapSource src, Size box)
    {
        _lastDisplayed = src;
        _lastDisplayedBox = box;
        Viewer.SetSource(src, box);
        if (_cropMode && !CropOverlay.IsInteracting) CropOverlay.Sync();
        RefreshMaskOverlay();
        RefreshPointMarkers();
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
        HistogramPanel.ClearCurves();
        UpdateSaveState();
        ReapplyAdjustments();
        ScheduleHistCommit();
        SetStatus("Histogram stretch points and R/G/B curves removed", error: false);
    }

    private void OnHistChannelClick(object sender, RoutedEventArgs e)
    {
        var ch = sender == BtnHistR ? HistChannel.R
            : sender == BtnHistG ? HistChannel.G
            : sender == BtnHistB ? HistChannel.B
            : HistChannel.Rgb;
        SetHistChannel(ch);
    }

    private void SetHistChannel(HistChannel ch)
    {
        HistogramPanel.Channel = ch;
        BtnHistRgb.IsChecked = ch == HistChannel.Rgb;
        BtnHistR.IsChecked = ch == HistChannel.R;
        BtnHistG.IsChecked = ch == HistChannel.G;
        BtnHistB.IsChecked = ch == HistChannel.B;
        BtnHistBake.IsEnabled = ch == HistChannel.Rgb;
    }

    private void OnHistCurvesChanged(object? sender, EventArgs e)
    {
        if (_currentPath is null) return;
        UpdateSaveState();
        ScheduleApplyWithThrottle();
        ScheduleHistCommit();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void UpdateUndoButtons()
    {
        NotifyHistoryChanged();
        if (BtnUndo is null || BtnRedo is null) return;
        BtnUndo.IsEnabled = _histPos > 0;
        BtnRedo.IsEnabled = _histPos < _hist.Count - 1;
    }

    private void SyncSessionHist()
    {
        if (_currentPath is null) return;
        var cur = CaptureEyeState();
        var withHist = cur with { History = new List<EyeState>(_hist), HistoryPos = _histPos };
        _session[_currentPath] = withHist;
        // Edits reach the .eye file at once (history has more than the opening state once anything changed).
        string? prevErr = _eyeAutoSave.LastError;
        if (!_eyeAutoSave.Save(_currentPath, withHist, edited: _hist.Count > 1) && _eyeAutoSave.LastError is { } err && err != prevErr)
            SetStatus($"Edits not saved to .eye: {err}", error: true);
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
        // Consecutive edits of the same control keep only the last value as one step.
        _histLastKey = HistoryCoalesce.Commit(_hist, ref _histPos, cur, _histLastKey, HistMax, HistEqual);
        LabelNewStep();
        Debug.Log($"HIST: stored count={_hist.Count} pos={_histPos} key={_histLastKey}");
        SyncSessionHist();
        UpdateUndoButtons();
    }

    private static bool HistEqual(EyeState a, EyeState b)
    {
        if (a.Version != b.Version || a.Mono != b.Mono || Math.Abs(a.BaseEv - b.BaseEv) > 1e-9) return false;
        if (!KnotsEqual(a.ToneCurve, b.ToneCurve)) return false;
        if (!CoreParams.ValuesEqual(a, b)) return false;
        if (a.Rot90 != b.Rot90 || a.CropAspect != b.CropAspect || Math.Abs(a.Angle - b.Angle) > 1e-6) return false;
        if (Math.Abs(a.CropX - b.CropX) > 1e-9 || Math.Abs(a.CropY - b.CropY) > 1e-9 ||
            Math.Abs(a.CropW - b.CropW) > 1e-9 || Math.Abs(a.CropH - b.CropH) > 1e-9) return false;
        if (!PluginEyeEqual(a, b)) return false;
        if (!KnotsEqual(a.CurveR, b.CurveR) || !KnotsEqual(a.CurveG, b.CurveG) || !KnotsEqual(a.CurveB, b.CurveB)) return false;
        if (!KnotsEqual(a.LevelR, b.LevelR) || !KnotsEqual(a.LevelG, b.LevelG) || !KnotsEqual(a.LevelB, b.LevelB)) return false;
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
        _histLastKey = null;
        Debug.Log($"HIST: init count={_hist.Count} pos={_histPos} exp={_hist[_histPos].Exposure:0.###} eye={st.History is { Count: > 0 }}");
        UpdateUndoButtons();
    }

    private void Undo()
    {
        CommitHistNow();
        if (_currentPath is null || _histPos <= 0) { SetStatus("Nothing to undo", error: false); return; }
        _histPos--;
        _histLastKey = null;
        var st = _hist[_histPos];
        Debug.Log($"HIST: undo -> pos={_histPos} exp={st.Exposure:0.###} sh={st.ShadowLift:0.#} knots={st.StretchEv?.Length ?? 0}");
        _histFromNav = true;
        ApplyEyeStateSuppressed(st);
        SyncSessionHist();
        UpdateUndoButtons();
        SetStatus($"Undo — {_hist.Count - 1 - _histPos} steps before this state", error: false);
    }

    private void Redo()
    {
        if (_currentPath is null || _histPos >= _hist.Count - 1) { SetStatus("Nothing to redo", error: false); return; }
        _histPos++;
        _histLastKey = null;
        var st = _hist[_histPos];
        Debug.Log($"HIST: redo -> pos={_histPos} exp={st.Exposure:0.###}");
        _histFromNav = true;
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
        if (AdjEditMask)
        {
            ShowAdjSet(false); // the histogram stretch is a whole-photo setting
            DeferRebuild();
        }

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

    /// <summary>
    /// Live render while a control moves: renders at once when idle; while a render runs, only marks the preview
    /// stale, and the finished render starts the next one with the newest values (intermediate values are skipped).
    /// </summary>
    private void RequestRender()
    {
        // Several controls changed by one input (synced sliders, presets) give one render.
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            _renderQueued = false;
            RequestRenderNow();
        });
    }

    private void RequestRenderNow()
    {
        if (IsGeoInteracting) return;
        if (_renderBusy)
        {
            _renderDirty = true;
            return;
        }
        _renderDirty = false;
        ReapplyAdjustments();
    }

    /// <summary>
    /// Which filters are on and the kind of mask each one has ("-" = whole photo), with the Adjustments mask. A slow
    /// render started before a filter got its mask would otherwise show the effect on the whole photo for a moment.
    /// </summary>
    private string PluginShapeKey()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in _pluginReg.Filters)
        {
            if (!_pluginState.IsFilterOn(f)) continue;
            sb.Append(f.FullId).Append('=').Append(_pluginState.MaskFor(f)?.Kind ?? "-").Append(';');
        }
        var am = _pluginState.Masks.TryGetValue(AdjustMask.Key, out var a) ? _pluginState.Resolve(a)?.Kind : null;
        sb.Append("adjust=").Append(am ?? "-").Append(';');
        // masks with values of their own (any filter's) change the photo too
        foreach (var kv in _pluginState.Masks.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (kv.Key != AdjustMask.Key && kv.Value.Adjust is { Count: > 0 })
                sb.Append("local:").Append(kv.Key).Append('=').Append(_pluginState.Resolve(kv.Value)?.Kind ?? "-").Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// A finished render is not shown when a newer one replaced it (cancelled) or when a newer render is waiting and
    /// the filters or their masks changed since this one started (a lagging slider value is still shown at once).
    /// </summary>
    private bool IsStaleRender(CancellationToken ct, string shape) =>
        ct.IsCancellationRequested || (_renderDirty && !string.Equals(shape, PluginShapeKey(), StringComparison.Ordinal));

    private void OnRenderFinished(int seq)
    {
        if (seq != _renderSeq) return;
        _renderBusy = false;
        if (!_renderDirty) return;
        _renderDirty = false;
        if (IsGeoInteracting) return;
        ReapplyAdjustments();
    }

    private void ScheduleApplyWithThrottle()
    {
        if (_adjustTimer is null) return;
        if (IsGeoInteracting)
        {
            _adjustTimer.Stop();
            _lastApplyTicks = Environment.TickCount64;
            RenderLiveGeoPreview();
            return;
        }
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

    private bool IsGeoInteracting => CropOverlay.IsInteracting || Viewer.IsRotating;

    private void StoreUnGeo(BitmapSource src, Size box)
    {
        _unGeoSrc = src;
        _unGeoBox = box;
        _unGeoSeq++;
    }

    private void InvalidateUnGeo()
    {
        _unGeoSrc = null;
        _unGeoBox = default;
        _unGeoSeq++;
        _liveSrc = null;
        _liveBox = default;
        _liveSeq++;
    }

    private void RenderLiveGeoPreview()
    {
        if (_compareOn) return;
        var src = _unGeoSrc;
        if (src is null || _unGeoBox.Width <= 0)
        {
            ReapplyAdjustments();
            return;
        }

        var displayGeo = _cropMode ? _geo with { CropX = 0.0, CropY = 0.0, CropW = 1.0, CropH = 1.0 } : _geo;
        if (displayGeo.IsDefault)
        {
            SetDisplayed(src, _unGeoBox);
            return;
        }

        int seq = _unGeoSeq;
        if (_liveSeq != seq)
        {
            double srcLong = Math.Max(src.PixelWidth, src.PixelHeight);
            double vp = Math.Max(1024.0, Math.Max(Viewer.ActualWidth, Viewer.ActualHeight));
            double target = Math.Min(3200.0, vp * Math.Max(1.0, Viewer.Zoom));
            double scale = Math.Min(1.0, target / srcLong);
            _liveSeq = seq;
            if (scale > 0.9995)
            {
                _liveSrc = src;
                _liveBox = _unGeoBox;
            }
            else
            {
                _liveSrc = null;
                _liveBox = default;
                Size box = _unGeoBox;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var st = new ScaleTransform(scale, scale, 0.0, 0.0);
                        st.Freeze();
                        var tb = new TransformedBitmap(src, st);
                        tb.Freeze();
                        var conv = new FormatConvertedBitmap(tb, PixelFormats.Bgra32, null, 0);
                        conv.Freeze();
                        int w = conv.PixelWidth;
                        int h = conv.PixelHeight;
                        byte[] buf = new byte[(long)w * 4 * h];
                        conv.CopyPixels(buf, w * 4, 0);
                        var mat = BitmapSource.Create(w, h, src.DpiX, src.DpiY, PixelFormats.Bgra32, null, buf, w * 4);
                        mat.Freeze();
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (_unGeoSeq != seq) return;
                            _liveSrc = mat;
                            _liveBox = new Size(box.Width * scale, box.Height * scale);
                            if (IsGeoInteracting) RenderLiveGeoPreview();
                        });
                    }
                    catch (Exception)
                    {
                    }
                });
            }
        }

        if (_liveSrc is null) return;

        var (gsrc, gbox) = PhotoGeo.Apply(_liveSrc, _liveBox, displayGeo);
        SetDisplayed(gsrc, gbox);
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
        var box = _baseNativeBox.Width > 0 ? _baseNativeBox : info.DisplaySize;
        var (dsp, gbox) = PhotoGeo.Apply(_baseBitmap, box, _geo);
        SetDisplayed(dsp, gbox);
        if (_compareOn)
        {
            UpdateHistogram(_currentPath, dsp);
            SetStatus("Compare — original without edits", error: false);
        }
        else
        {
            ReapplyAdjustments();
            SetStatus("Ready", error: false);
        }
    }

    private void OnCropClick(object sender, RoutedEventArgs e) => SetCropMode(!_cropMode);

    private void OnCropDoneClick(object sender, RoutedEventArgs e) => SetCropMode(false);

    private void SetCropMode(bool open)
    {
        if (open && _baseBitmap is null) return;
        if (open) EndMaskEdit();
        _cropMode = open;
        if (open && _compareOn) SetCompare(false);
        BtnCrop.Foreground = (Brush)FindResource(open ? "AccentBrush" : "ForegroundBrush");
        CropOverlay.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CropBar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        bool editOn = !open;
        ShadowSlider.IsEnabled = editOn;
        GammaSlider.IsEnabled = editOn;
        RightPanel.IsEnabled = editOn;
        HistHost.IsEnabled = editOn;
        if (open)
        {
            _suppressCropCombo = true;
            try { SelectComboAspect(_geo.Aspect); }
            finally { _suppressCropCombo = false; }
            CropOverlay.Aspect = ResolveAspect(_geo.Aspect);
            CropOverlay.SetGeometry(_geo.CropX, _geo.CropY, _geo.CropW, _geo.CropH, _geo.Angle);
            SetStatus("Crop — drag inside: move, handles: resize, drag outside or Ctrl+drag anywhere: rotate, Esc: close", error: false);
        }
        else
        {
            CropOverlay.Hide();
        }
        RenderLiveGeoPreview();
        ReapplyAdjustments();
        RefreshMaskPins();
    }

    private void OnRotateLeftClick(object sender, RoutedEventArgs e) => RotateQuarter(-1);

    private void OnRotateRightClick(object sender, RoutedEventArgs e) => RotateQuarter(1);

    private void RotateQuarter(int turns)
    {
        if (_baseBitmap is null) return;
        if (_compareOn) SetCompare(false);
        _geo = _geo.WithRotation(turns);
        if (_cropMode)
        {
            CropOverlay.Aspect = ResolveAspect(_geo.Aspect);
            CropOverlay.SetGeometry(_geo.CropX, _geo.CropY, _geo.CropW, _geo.CropH, _geo.Angle);
        }
        UpdateSaveState();
        RenderLiveGeoPreview();
        ReapplyAdjustments();
        ScheduleHistCommit();
        SetStatus(turns > 0 ? "Rotated 90° right — orientation swapped" : "Rotated 90° left — orientation swapped", error: false);
        RefreshDimsStatus();
    }

    private void OnViewerRotateDrag(object? sender, double deltaDeg)
    {
        if (_baseBitmap is null || _cropMode) return;
        _geo = _geo with { Angle = Math.Clamp(Math.Round((_geo.Angle + deltaDeg) * 10.0) / 10.0, -45.0, 45.0) };
        UpdateSaveState();
        ScheduleApplyWithThrottle();
        ScheduleHistCommit();
        SetStatus($"Straighten {_geo.Angle:0.0}° (Ctrl+drag)", error: false);
    }

    private void OnViewerRotateEnded(object? sender, EventArgs e)
    {
        if (IsGeoInteracting || _baseBitmap is null) return;
        ReapplyAdjustments();
        ScheduleHistCommit();
    }

    private void OnCropGeometryChanged()
    {
        _geo = _geo with
        {
            CropX = CropOverlay.CropFx,
            CropY = CropOverlay.CropFy,
            CropW = CropOverlay.CropFw,
            CropH = CropOverlay.CropFh
        };
        UpdateSaveState();
        RefreshDimsStatus();
        ScheduleApplyWithThrottle();
    }

    private void OnCropRotateChanged()
    {
        _geo = _geo with { Angle = CropOverlay.Angle };
        UpdateSaveState();
        RefreshDimsStatus();
        ScheduleApplyWithThrottle();
        SetStatus($"Straighten {CropOverlay.Angle:0.0}° — drag outside the crop edges", error: false);
    }

    private void OnCropInteractionEnded()
    {
        RefreshDimsStatus();
        ReapplyAdjustments();
        ScheduleHistCommit();
        if (_cropMode && !CropOverlay.IsInteracting) CropOverlay.Sync();
    }

    private void OnCropResetClick(object sender, RoutedEventArgs e)
    {
        _geo = _geo with { Angle = 0, CropX = 0, CropY = 0, CropW = 1, CropH = 1, Aspect = "original" };
        if (_cropMode)
        {
            _suppressCropCombo = true;
            try { SelectComboAspect("original"); }
            finally { _suppressCropCombo = false; }
            CropOverlay.Aspect = ResolveAspect("original");
            CropOverlay.SetGeometry(0, 0, 1, 1, 0);
        }
        UpdateSaveState();
        RenderLiveGeoPreview();
        ReapplyAdjustments();
        ScheduleHistCommit();
        SetStatus("Crop reset to original frame", error: false);
        RefreshDimsStatus();
    }

    private void OnCropAspectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCropCombo) return;
        if (CropAspectCombo.SelectedItem is not ComboBoxItem item) return;
        string value = (string)(item.Tag ?? "original");
        _geo = FitAspect(_geo with { Aspect = value }, ResolveAspect(value));
        if (_cropMode)
        {
            CropOverlay.Aspect = ResolveAspect(value);
            CropOverlay.SetGeometry(_geo.CropX, _geo.CropY, _geo.CropW, _geo.CropH, _geo.Angle);
        }
        UpdateSaveState();
        RenderLiveGeoPreview();
        ReapplyAdjustments();
        ScheduleHistCommit();
        SetStatus($"Crop aspect: {item.Content}", error: false);
        RefreshDimsStatus();
    }

    private void SelectComboAspect(string value)
    {
        foreach (var item in CropAspectCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals((string)(item.Tag ?? "original"), value, StringComparison.Ordinal))
            {
                item.IsSelected = true;
                return;
            }
        }
    }

    private double ResolveAspect(string preset) => preset switch
    {
        "3:2" => 1.5,
        "4:3" => 4.0 / 3.0,
        "16:9" => 16.0 / 9.0,
        "16:10" => 1.6,
        _ => IntrinsicFrameAspect()
    };

    private double IntrinsicFrameAspect()
    {
        if (_baseInfo is not ImageInfo info || info.Width <= 0 || info.Height <= 0) return 1.0;
        var (w, h) = FrameNativeSize(info);
        return w / Math.Max(1.0, h);
    }

    private (double W, double H) FrameNativeSize(ImageInfo info)
    {
        int rot = ((_geo.Rot90 % 4) + 4) % 4;
        return rot is 1 or 3 ? ((double)info.Height, (double)info.Width) : ((double)info.Width, (double)info.Height);
    }

    private GeoState FitAspect(GeoState g, double ratio)
    {
        if (double.IsNaN(ratio) || ratio <= 0) return g;
        if (_baseInfo is not ImageInfo info || info.Width <= 0 || info.Height <= 0) return g;
        var (fw, fh) = FrameNativeSize(info);
        double wPx = g.CropW * fw;
        double hPx = g.CropH * fh;
        double hA = Math.Min(hPx, wPx / ratio);
        double wA = hA * ratio;
        double wB = Math.Min(wPx, hPx * ratio);
        double hB = wB / ratio;
        double w, h;
        if (wA * hA >= wB * hB) { w = wA; h = hA; }
        else { w = wB; h = hB; }
        double cx = g.CropX + g.CropW / 2.0;
        double cy = g.CropY + g.CropH / 2.0;
        double x = Math.Clamp(cx - w / (2.0 * fw), 0.0, Math.Max(0.0, 1.0 - w / fw));
        double y = Math.Clamp(cy - h / (2.0 * fh), 0.0, Math.Max(0.0, 1.0 - h / fh));
        return g with { CropX = x, CropY = y, CropW = w / fw, CropH = h / fh };
    }

    private string DimsLine(string name, ImageInfo info, string decoded) => DimsLineCore(name, info, decoded) + PluginMissingNote();

    private string DimsLineCore(string name, ImageInfo info, string decoded)
    {
        if (_geo.IsDefault || info.Width <= 0 || info.Height <= 0) return $"{name}   {info.Width}×{info.Height} px{decoded}";
        var (fw, fh) = FrameNativeSize(info);
        if (Math.Abs(_geo.Angle) >= 0.005)
        {
            double t = PhotoGeo.InsetScale(fw, fh, _geo.Angle);
            fw *= t;
            fh *= t;
        }
        int fx0 = (int)Math.Round(_geo.CropX * fw, MidpointRounding.AwayFromZero);
        int fx1 = (int)Math.Round((_geo.CropX + _geo.CropW) * fw, MidpointRounding.AwayFromZero);
        int fy0 = (int)Math.Round(_geo.CropY * fh, MidpointRounding.AwayFromZero);
        int fy1 = (int)Math.Round((_geo.CropY + _geo.CropH) * fh, MidpointRounding.AwayFromZero);
        int iw = Math.Max(1, fx1 - fx0);
        int ih = Math.Max(1, fy1 - fy0);
        bool cropped = _geo.CropW < 0.9999 || _geo.CropH < 0.9999 || Math.Abs(_geo.Angle) >= 0.005;
        if (!cropped) return $"{name}   {iw}×{ih} px{decoded}";
        return $"{name}   {info.Width}×{info.Height} px{decoded}   crop {iw}×{ih} px";
    }

    private void RefreshDimsStatus()
    {
        if (_currentPath is null || _baseBitmap is null || _baseInfo is not ImageInfo info) return;
        SetStatus(DimsLine(Path.GetFileName(_currentPath), info, ""), error: false);
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
                        PanelExtras() is not null ||
                        MaskExtras() is not null ||
                        PluginHasEffect() ||
                        !_geo.IsDefault;
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
        string fmt = tiff ? "tiff16" : "jpg";
        string? cancel = null;
        try { cancel = PluginManager.Instance.RaiseBeforeSave(path, dest, fmt); }
        catch (Exception ex) { Debug.Log($"PLUGINS: BeforeSave [{ex.GetType().Name}] {ex.Message}"); }
        if (cancel is not null)
        {
            SetStatus($"Save cancelled — {cancel}", error: true);
            return;
        }
        var snap = CaptureFullSnapshot(path);
        var geo = _geo;

        _saving = true;
        BtnSave.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        BeginBusy(ExpectedFullRenderMs());

        _ = Task.Run(() =>
        {
            bool saved = false;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (r16, bmp) = RenderFull(snap);
                double mp = (r16 is not null ? (double)r16.W * r16.H : (double)bmp!.PixelWidth * bmp.PixelHeight) / 1e6;
                double fullMs = sw.Elapsed.TotalMilliseconds;
                Dispatcher.BeginInvoke(() => RecordFullRenderMs(fullMs, mp));
                if (r16 is not null)
                {
                    SaveEncoded(r16, dest, tiff);
                    Debug.Log($"SAVE: {dest} {sw.ElapsedMilliseconds} ms out={r16.W}x{r16.H} raw16 tiff16={tiff} plugins={snap.Job.Report.Ran} geo(rot={geo.Rot90} angle={geo.Angle:0.##} crop={geo.CropX:0.##},{geo.CropY:0.##},{geo.CropW:0.##},{geo.CropH:0.##})");
                }
                else
                {
                    SaveEncoded(bmp!, dest, tiff);
                    Debug.Log($"SAVE: {dest} {sw.ElapsedMilliseconds} ms out={bmp!.PixelWidth}x{bmp.PixelHeight} tiff16={tiff} plugins={snap.Job.Report.Ran} geo(rot={geo.Rot90} angle={geo.Angle:0.##} crop={geo.CropX:0.##},{geo.CropY:0.##},{geo.CropW:0.##},{geo.CropH:0.##})");
                }
                saved = true;
                var perr = snap.Job.Report.Errors.Count > 0 ? $"  (plugin errors: {string.Join("; ", snap.Job.Report.Errors.Distinct())})" : "";
                try
                {
                    EyeFile.Save(path, eyeState);
                    Dispatcher.BeginInvoke(() => _eyeAutoSave.Written(path, eyeState));
                }
                catch (Exception eyeEx)
                {
                    Debug.Log($"EYE ERROR: {Path.GetFileName(path)} [{eyeEx.GetType().Name}] {eyeEx.Message}");
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrent(path)) SetStatus($"Saved: {Path.GetFileName(dest)} ({(tiff ? "TIFF 16-bit" : "JPG 90%")}){perr}", error: perr.Length > 0);
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
                try { PluginManager.Instance.RaiseAfterSave(path, dest, fmt, saved, export: false); }
                catch (Exception ex) { Debug.Log($"PLUGINS: AfterSave [{ex.GetType().Name}] {ex.Message}"); }
                _saving = false;
                Dispatcher.BeginInvoke(() => { EndBusy(); UpdateSaveState(); });
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

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsFlyout();

    private void OnPreviewQualityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string q }) return;
        _previewQuality = PreviewQuality.Normalize(q);
        UpdatePreviewQualityMenu();
        SettingsStore.Save(SettingsStore.Load() with { PreviewQuality = _previewQuality });
        ReapplyAdjustments();
        SetStatus(_previewQuality switch
        {
            PreviewQuality.Full => "Preview quality: full resolution",
            PreviewQuality.Sharp => "Preview quality: sharp (2× window size)",
            _ => "Preview quality: fast (window size)"
        }, error: false);
    }

    private void UpdatePreviewQualityMenu()
    {
        MnuPreviewWindow.IsChecked = _previewQuality == PreviewQuality.Window;
        MnuPreviewSharp.IsChecked = _previewQuality == PreviewQuality.Sharp;
        MnuPreviewFull.IsChecked = _previewQuality == PreviewQuality.Full;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        CloseSettingsFlyout();
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Image",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.heic;*.heif;*.hif;*.avif" + PluginOpenPatterns() + "|RAW|*.rw2;*.nef;*.nrw;*.arw;*.srf;*.sr2;*.cr2;*.cr3;*.crw;*.orf;*.raf;*.pef;*.ptx;*.dng;*.srw;*.x3f;*.3fr;*.fff;*.iiq;*.mos;*.erf;*.kdc;*.dcr;*.mef;*.rwl|All files|*.*",
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
        RequestRender();
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

    /// <summary>Whole-photo panel values (also while the sliders show the mask's set); null when neutral.</summary>
    private PhotoAdjustments? PanelExtras() => ExpSlider is null ? null : BuildExtras(GlobalCore, force: false);

    private static PhotoAdjustments? StripExposureTemp(PhotoAdjustments? a)
    {
        if (a is null) return null;
        return new PhotoAdjustments
        {
            Exposure = 0.0,
            Temperature = 0.0,
            Tint = 0.0,
            Contrast = a.Contrast,
            Saturation = a.Saturation,
            Clarity = a.Clarity,
            Sharpen = a.Sharpen,
            Highlights = a.Highlights,
            Whites = a.Whites,
            Blacks = a.Blacks,
            Dehaze = a.Dehaze,
            Denoise = a.Denoise,
            ColorNoise = a.ColorNoise,
            Curves = a.Curves
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
        PointMarkers.Interactive = !_wbPicking && _pointPick is null;
        Mouse.OverrideCursor = _wbPicking ? Cursors.Cross : null;
        SetStatus(_wbPicking
            ? "White point eyedropper — click a neutral area of the photo (Esc — cancel)"
            : "White point eyedropper — cancelled", error: false);
    }

    private void OnViewerPickClick(object sender, MouseButtonEventArgs e)
    {
        if (_pointPick is not null) { HandlePointPick(e); return; }
        if (!_wbPicking) return;
        e.Handled = true;
        _wbPicking = false;
        PointMarkers.Interactive = !_wbPicking && _pointPick is null;
        Mouse.OverrideCursor = null;

        var disp = _lastDisplayed ?? _baseBitmap;
        if (disp is null || _baseInfo is not ImageInfo bi || !Viewer.TryScreenToPixel(e.GetPosition(Viewer), out double bx, out double by))
        {
            SetStatus("White point — clicked outside the photo", error: true);
            return;
        }
        // The displayed image already carries the current white balance and the tone curve, so the sample is taken
        // from the unprocessed source (geometry mapped back) in linear light, and the result is absolute.
        var src = PhotoGeo.MapFromOutput(CurrentDisplayGeo(), bi.Width, bi.Height, new Point(bx / disp.PixelWidth, by / disp.PixelHeight));
        if (src.X < 0 || src.Y < 0 || src.X > 1 || src.Y > 1 || !SampleSourceLinear(src.X, src.Y, out double lr, out double lg, out double lb, out bool clipped))
        {
            SetStatus("White point — clicked outside the photo", error: true);
            return;
        }
        if (clipped)
        {
            SetStatus("White point — that area is overexposed (clipped); pick a light gray that is not pure white", error: true);
            return;
        }
        bool inRange = PhotoOps.WbFromLinearSample(lr, lg, lb, out double temp, out double tint);
        if (AdjEditMask)
        {
            // inside the mask the slider value adds to the whole-photo one
            TempSlider.Value = Math.Clamp(Math.Round(temp * 100.0) - GlobalCore(CoreCtl("Temperature")), TempSlider.Minimum, TempSlider.Maximum);
            TintSlider.Value = Math.Clamp(Math.Round(tint * 100.0) - GlobalCore(CoreCtl("Tint")), TintSlider.Minimum, TintSlider.Maximum);
            SetStatus($"White balance inside the mask from white point — mask values: temperature {TempSlider.Value:+0;-0;0}, tint {TintSlider.Value:+0;-0;0}", error: false);
            return;
        }
        TempSlider.Value = Math.Clamp(Math.Round(temp * 100.0), TempSlider.Minimum, TempSlider.Maximum);
        TintSlider.Value = Math.Clamp(Math.Round(tint * 100.0), TintSlider.Minimum, TintSlider.Maximum);
        SetStatus(inRange
            ? $"White balance from white point — temperature {(int)TempSlider.Value}, tint {(int)TintSlider.Value}"
            : $"White point — color cast beyond the slider range, corrected as far as possible (temperature {(int)TempSlider.Value}, tint {(int)TintSlider.Value})",
            error: !inRange);
    }

    /// <summary>
    /// Mean linear-light color of the unprocessed source around a normalized point (RAW: decoded linear data;
    /// other formats: decoded pixels with the same 2.2 linearization the adjustments use). Clipped pixels are skipped.
    /// </summary>
    private bool SampleSourceLinear(double nx, double ny, out double r, out double g, out double b, out bool clipped)
    {
        r = g = b = 0; clipped = false;
        double sr = 0, sg = 0, sb = 0; int n = 0, nClip = 0;
        if (_baseRaw16 is Raw16Image raw)
        {
            int cx = (int)Math.Clamp(nx * raw.W, 0, raw.W - 1), cy = (int)Math.Clamp(ny * raw.H, 0, raw.H - 1);
            int rad = Math.Max(2, Math.Max(raw.W, raw.H) / 500);
            for (int y = Math.Max(0, cy - rad); y <= Math.Min(raw.H - 1, cy + rad); y++)
                for (int x = Math.Max(0, cx - rad); x <= Math.Min(raw.W - 1, cx + rad); x++)
                {
                    int i = y * raw.W + x;
                    if (Math.Max(raw.R[i], Math.Max(raw.G[i], raw.B[i])) >= 64800) { nClip++; continue; }
                    sr += raw.R[i] / 65535.0; sg += raw.G[i] / 65535.0; sb += raw.B[i] / 65535.0; n++;
                }
        }
        else if (_baseBitmap is BitmapSource bmp)
        {
            int cx = (int)Math.Clamp(nx * bmp.PixelWidth, 0, bmp.PixelWidth - 1), cy = (int)Math.Clamp(ny * bmp.PixelHeight, 0, bmp.PixelHeight - 1);
            int rad = Math.Max(2, Math.Max(bmp.PixelWidth, bmp.PixelHeight) / 500);
            int x0 = Math.Max(0, cx - rad), y0 = Math.Max(0, cy - rad);
            int w = Math.Min(bmp.PixelWidth, cx + rad + 1) - x0, h = Math.Min(bmp.PixelHeight, cy + rad + 1) - y0;
            if (w <= 0 || h <= 0) return false;
            var region = new FormatConvertedBitmap(new CroppedBitmap(bmp, new Int32Rect(x0, y0, w, h)), PixelFormats.Bgra32, null, 0);
            var buf = new byte[w * h * 4];
            region.CopyPixels(buf, w * 4, 0);
            for (int i = 0; i + 3 < buf.Length; i += 4)
            {
                if (buf[i + 3] < 16) continue;
                if (Math.Max(buf[i], Math.Max(buf[i + 1], buf[i + 2])) >= 252) { nClip++; continue; }
                sb += Math.Pow(buf[i] / 255.0, 2.2); sg += Math.Pow(buf[i + 1] / 255.0, 2.2); sr += Math.Pow(buf[i + 2] / 255.0, 2.2); n++;
            }
        }
        else return false;
        if (n == 0) { clipped = nClip > 0; return nClip > 0; }
        r = sr / n; g = sg / n; b = sb / n;
        return true;
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
        if (_adjSwitching) return;
        StoreMaskSliders();
        _showHistogram = true;
        HistHost.Visibility = RightPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Hidden;
        UpdatePanelLabels();
        UpdateSaveState();
        RequestRender();
        ScheduleHistCommit();
    }

    private void UpdatePanelLabels()
    {
        if (!IsInitialized) return;
        foreach (var c in CoreUi())
            if (c.Text is not null && c.Panel is not null) c.Text.Text = c.P.Format(c.Panel.Value);
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
        _adjGlobal = null;
        _adjMaskKey = null;
        _eyeBaseEv = double.NaN;
        _eyeCurveSet = false;
        _eyeCurve = null;
        _geo = GeoState.Default;
        if (_cropMode)
        {
            _suppressCropCombo = true;
            try { SelectComboAspect("original"); }
            finally { _suppressCropCombo = false; }
            CropOverlay.Aspect = ResolveAspect("original");
            CropOverlay.SetGeometry(0, 0, 1, 1, 0);
        }
        HistogramPanel.ClearManualStretch();
        HistogramPanel.ClearCurves();
        foreach (var c in CoreUi())
        {
            c.Slider.Value = c.P.Default;
            if (c.Panel is not null) c.Panel.Value = c.P.Default;
        }
        UpdatePanelLabels();
        _monoMode = MonoMode.None;
        UpdateMonoButton();
        _pluginState.Clear();
        EndMaskEdit();
        RebuildPluginUi();
        if (!silent)
        {
            SetStatus("Adjustments reset to defaults", error: false);
            ReapplyAdjustments();
        }
    }

    private static double[][]? NullIfEmpty(double[][] p) => p.Length == 0 ? null : p;

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
        var ps = _pluginState.Save();
        var st = new EyeState
        {
            BaseEv = double.IsNaN(_eyeBaseEv) ? _baseEv : _eyeBaseEv,
            ToneCurve = CurrentCurve()?.KnotPairs(),
            Stretch = stretch,
            StretchEv = stretchEv,
            CurveR = NullIfEmpty(HistogramPanel.CurvePairs(HistChannel.R)),
            CurveG = NullIfEmpty(HistogramPanel.CurvePairs(HistChannel.G)),
            CurveB = NullIfEmpty(HistogramPanel.CurvePairs(HistChannel.B)),
            LevelR = NullIfEmpty(HistogramPanel.LevelPairs(HistChannel.R)),
            LevelG = NullIfEmpty(HistogramPanel.LevelPairs(HistChannel.G)),
            LevelB = NullIfEmpty(HistogramPanel.LevelPairs(HistChannel.B)),
            Mono = (int)_monoMode,
            Rot90 = _geo.Rot90,
            Angle = _geo.Angle,
            CropX = _geo.CropX,
            CropY = _geo.CropY,
            CropW = _geo.CropW,
            CropH = _geo.CropH,
            CropAspect = _geo.Aspect,
            PluginValues = ps.Values,
            PluginActive = ps.Active,
            PluginText = ps.Text,
            PluginOrder = ps.Order,
            PluginMasks = ps.Masks
        };
        foreach (var c in CoreUi()) st = c.P.Set(st, GlobalCore(c));
        return st;
    }

    private void ApplyEyeFile(string path)
    {
        if (!EyeFile.TryLoad(path, out var st)) return;
        ApplyEyeState(st);
    }

    private static CameraToneCurve? ParseCurve(double[][]? knots)
    {
        if (knots is not { Length: >= 2 }) return null;
        try
        {
            return new CameraToneCurve(knots);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void ApplyEyeState(EyeState st)
    {
        string? wasMaskSet = AdjEditMask ? AdjSetKey : null; // the sliders get the whole-photo values first, the mask's set again below
        _adjGlobal = null;
        _adjMaskKey = null;
        _eyeBaseEv = st.BaseEv;
        _eyeCurveSet = true;
        _eyeCurve = st.ToneCurve is { Length: >= 2 } ? ParseCurve(st.ToneCurve) : null;
        // Version 1 files stored saturation as 0..200 with 100 neutral.
        var core = st.Version == 1 ? st with { Saturation = st.Saturation - 100.0 } : st;
        SetCoreSliders(core, toolbar: true);
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
        HistogramPanel.RestoreCurves(st.CurveR, st.CurveG, st.CurveB, st.LevelR, st.LevelG, st.LevelB);
        SetCoreSliders(core, toolbar: false);
        _geo = new GeoState
        {
            Rot90 = Math.Clamp(st.Rot90, 0, 3),
            Angle = Math.Clamp(st.Angle, -45.0, 45.0),
            CropX = Math.Clamp(st.CropX, 0.0, 1.0),
            CropY = Math.Clamp(st.CropY, 0.0, 1.0),
            CropW = Math.Clamp(st.CropW <= 0 ? 1.0 : st.CropW, PhotoGeo.MinFraction, 1.0),
            CropH = Math.Clamp(st.CropH <= 0 ? 1.0 : st.CropH, PhotoGeo.MinFraction, 1.0),
            Aspect = string.IsNullOrWhiteSpace(st.CropAspect) ? "original" : st.CropAspect
        };
        _pluginState.Load(st, _pluginReg);
        if (wasMaskSet is not null) ShowAdjSet(true, wasMaskSet);
        SyncMaskOverlaySpec();
        RebuildPluginUi();
        if (_cropMode)
        {
            _suppressCropCombo = true;
            try { SelectComboAspect(_geo.Aspect); }
            finally { _suppressCropCombo = false; }
            CropOverlay.Aspect = ResolveAspect(_geo.Aspect);
            CropOverlay.SetGeometry(_geo.CropX, _geo.CropY, _geo.CropW, _geo.CropH, _geo.Angle);
        }
        UpdatePanelLabels();
        UpdateSaveState();
        ReapplyAdjustments();
    }

    private void OnAdjustSliderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not Slider s) return;
        foreach (var c in CoreUi())
        {
            if (!ReferenceEquals(s, c.Slider) && !ReferenceEquals(s, c.Panel)) continue;
            s.Value = c.P.Default;
            e.Handled = true;
            return;
        }
    }

    /// <summary>A basic adjustment's controls in this window (<see cref="CoreParams"/>).</summary>
    private sealed record CoreControl(CoreParam P, Slider Slider, Slider? Panel, TextBlock? Text);

    private CoreControl[]? _coreUi;

    /// <summary>
    /// The sliders of the basic adjustments, found by the names in <see cref="CoreParams"/>. Slider holds the value;
    /// Panel is the adjustments-panel slider (the same slider, a mirror of a toolbar slider, or null for toolbar only).
    /// </summary>
    private CoreControl[] CoreUi() => _coreUi ??= CoreParams.All.Where(p => p.Slider is not null).Select(p =>
    {
        var slider = Named<Slider>(p.Slider!);
        Slider? panel = p.PanelSlider is { } pn ? Named<Slider>(pn) : p.ValueText is not null ? slider : null;
        return new CoreControl(p, slider, panel, p.ValueText is { } t ? Named<TextBlock>(t) : null);
    }).ToArray();

    private T Named<T>(string name) where T : class =>
        FindName(name) as T ?? throw new InvalidOperationException($"MainWindow.xaml has no {typeof(T).Name} '{name}' (CoreParams)");

    /// <summary>Sets the sliders from <paramref name="st"/>: the toolbar ones or the panel-only ones.</summary>
    private void SetCoreSliders(EyeState st, bool toolbar)
    {
        foreach (var c in CoreUi())
        {
            if (ReferenceEquals(c.Panel, c.Slider) == toolbar) continue;
            double v = c.P.Get(st);
            c.Slider.Value = Math.Clamp(v, c.Slider.Minimum, c.Slider.Maximum);
            if (c.Panel is not null && !ReferenceEquals(c.Panel, c.Slider)) c.Panel.Value = Math.Clamp(v, c.Panel.Minimum, c.Panel.Maximum);
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(e.Effects == DragDropEffects.Copy);
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    /// <summary>
    /// A list inside the adjustments panel that scrolls its own rows (History following the newest step after a
    /// slider change) passes the request on to the panel scroller, which would jump the whole panel to that list.
    /// Follow such a cascade only while the user works in that list (keyboard focus inside it).
    /// </summary>
    private void OnPanelRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is ScrollViewer inner && !inner.IsKeyboardFocusWithin)
            e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
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
            PointMarkers.Interactive = !_wbPicking && _pointPick is null;
            Mouse.OverrideCursor = null;
            SetStatus("White point eyedropper — cancelled", error: false);
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            return;
        }
        if (_cropMode && e.Key == Key.Escape)
        {
            SetCropMode(false);
            SetStatus("Crop closed", error: false);
            e.Handled = true;
            return;
        }
        if (HandlePluginKey(e)) return;
        if (HandlePresentationKey(e)) return;
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

        if (e.Key is Key.Left or Key.Right && Keyboard.Modifiers == ModifierKeys.None &&
            Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            Navigate(e.Key == Key.Left ? -1 : 1);
            e.Handled = true;
            return;
        }

        // O toggles the mask colour while a mask is edited, also right after moving a slider
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.None && _maskEditFilter is not null && MaskBar.Visibility == Visibility.Visible
            && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            SetMaskTint(BtnMaskTint.IsChecked != true);
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is Slider) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase) return;
        // Plugin list panels (history): arrow keys, Home/End and PageUp/PageDown move the selection.
        if (Keyboard.FocusedElement is ListBox or ListBoxItem &&
            e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) return;

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
            case Key.C:
                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    SetCropMode(!_cropMode);
                    e.Handled = true;
                }
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

