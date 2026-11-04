using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImgViewer.Controls;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;
using Microsoft.Win32;
using static ImgViewer.Services.PluginEditOps;

namespace ImgViewer;

/// <summary>Plugin commands: the command context (core settings, geometry, curves, pixels, masks, viewer), background work, overlays.</summary>
public partial class MainWindow
{
    /// <summary>Labels given by plugin commands to the steps they created (checked before the automatic label).</summary>
    private readonly ConditionalWeakTable<EyeState, string> _stepCustomLabels = new();
    private string? _nextStepLabel;
    private readonly Dictionary<string, CancellationTokenSource> _bgTasks = new(StringComparer.Ordinal);

    /// <summary>Records the current state as its own undo step (not merged with the previous one), labelled when a label is given.</summary>
    private void CommitStep(string? label)
    {
        _histCommitTimer?.Stop();
        _histLastKey = null;
        _nextStepLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        try
        {
            CommitHistNow();
        }
        finally
        {
            _nextStepLabel = null;
            _histLastKey = null;
        }
    }

    /// <summary>Called by CommitHistNow right after a new step was stored.</summary>
    private void LabelNewStep()
    {
        if (_nextStepLabel is { } l && _histPos >= 0 && _histPos < _hist.Count)
            _stepCustomLabels.AddOrUpdate(_hist[_histPos], l);
    }

    private void RunCommand(Registered<CommandDescriptor> c)
    {
        if (c.Owner is { Faulted: true })
        {
            SetStatus($"{c.D.Label}: plugin {c.PluginName} is disabled", error: true);
            return;
        }
        // Earlier edits become their own step, so the command's changes are one step of their own.
        CommitHistNow();
        _histLastKey = null;
        var ctx = new CommandContext(this, c.PluginId, c.Owner, c.D.Label);
        try
        {
            c.D.Execute?.Invoke(ctx);
            PluginManager.ReportSuccess(c.Owner);
        }
        catch (Exception ex)
        {
            PluginManager.Instance.ReportFailure(c.Owner, $"command {c.FullId}", ex);
            SetStatus($"{c.D.Label}: {ex.Message}", error: true);
        }
        FinishCommand(ctx, c.Owner, c.FullId);
    }

    private void FinishCommand(CommandContext ctx, LoadedPlugin? owner, string what)
    {
        try
        {
            ctx.Finish();
        }
        catch (Exception ex)
        {
            PluginManager.Instance.ReportFailure(owner, what, ex);
            SetStatus($"{what}: {ex.Message}", error: true);
        }
    }

    // ------------------------------------------------------------------
    //  Background work
    // ------------------------------------------------------------------

    private void StartBackground(string pid, LoadedPlugin? owner, string label, string stepLabel,
        Func<IProgress<double>, CancellationToken, object> work, Action<ICommandContext, object>? done)
    {
        if (_bgTasks.Remove(pid, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        _bgTasks[pid] = cts;
        string? path = _currentPath;
        label = string.IsNullOrWhiteSpace(label) ? "Working" : label.Trim();
        SetStatus($"{label}… (Esc — cancel)", error: false);
        int lastPct = -1;
        var progress = new Progress<double>(p =>
        {
            if (cts.IsCancellationRequested || !double.IsFinite(p)) return;
            int pct = (int)Math.Round(Math.Clamp(p, 0, 1) * 100);
            if (pct == lastPct) return;
            lastPct = pct;
            SetStatus($"{label}… {pct}% (Esc — cancel)", error: false);
        });
        var token = cts.Token;
        Task.Run(() => work(progress, token), token).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            bool mine = _bgTasks.TryGetValue(pid, out var c) && ReferenceEquals(c, cts);
            if (mine) _bgTasks.Remove(pid);
            bool cancelled = cts.IsCancellationRequested || t.IsCanceled ||
                             t.Exception?.GetBaseException() is OperationCanceledException;
            cts.Dispose();
            if (cancelled) return;
            if (t.IsFaulted)
            {
                var ex = t.Exception!.GetBaseException();
                PluginManager.Instance.ReportFailure(owner, "background work " + label, ex);
                SetStatus($"{label}: {ex.Message}", error: true);
                return;
            }
            if (!string.Equals(path, _currentPath, StringComparison.Ordinal))
            {
                SetStatus($"{label} — finished for another photo, result dropped", error: false);
                return;
            }
            SetStatus($"{label} — done", error: false);
            if (done is null) return;
            CommitHistNow();
            _histLastKey = null;
            var ctx = new CommandContext(this, pid, owner, stepLabel);
            try
            {
                done(ctx, t.Result);
                PluginManager.ReportSuccess(owner);
            }
            catch (Exception ex)
            {
                PluginManager.Instance.ReportFailure(owner, "background work " + label, ex);
                SetStatus($"{label}: {ex.Message}", error: true);
            }
            FinishCommand(ctx, owner, "background work " + label);
        }), TaskScheduler.Default);
    }

    /// <summary>Cancels all background work of plugins; returns false when none was running.</summary>
    private bool CancelBackgroundTasks(bool quiet = false)
    {
        if (_bgTasks.Count == 0) return false;
        foreach (var c in _bgTasks.Values) c.Cancel();
        _bgTasks.Clear();
        if (!quiet) SetStatus("Plugin work cancelled", error: false);
        return true;
    }

    // ------------------------------------------------------------------
    //  Pixels
    // ------------------------------------------------------------------

    private PhotoPixels? SourcePixels(int maxLongSide)
    {
        if (_baseRaw16 is Raw16Image raw)
        {
            float gain = (float)Math.Pow(2.0, CurrentBaseEv());
            var img = PluginEditOps.Downscale(raw, maxLongSide, gain / 65535f);
            return new PhotoPixels
            {
                Image = img, Space = PluginColorSpace.LinearSrgb,
                FullWidth = _baseInfo?.Width ?? raw.W, FullHeight = _baseInfo?.Height ?? raw.H
            };
        }
        if (_baseBitmap is BitmapSource bmp)
        {
            return new PhotoPixels
            {
                Image = PluginPipeline.FromBitmap(Shrink(bmp, maxLongSide), out _, out _), Space = PluginColorSpace.Srgb,
                FullWidth = _baseInfo?.Width ?? bmp.PixelWidth, FullHeight = _baseInfo?.Height ?? bmp.PixelHeight
            };
        }
        return null;
    }

    /// <summary>The photo at default settings: RAW through the default gain and camera tone curve; other formats as decoded.</summary>
    private PhotoPixels? DevelopedPixels(int maxLongSide)
    {
        if (_baseRaw16 is Raw16Image raw)
        {
            var small = PluginEditOps.Downscale16(raw, maxLongSide);
            var lin = LinearRender.ApplyGain(small, CurrentBaseEv(), 0.0, 0.0);
            var dev = Adjust16.Apply(lin, 0.0, 1.0, null, out _, cameraTone: CurrentCurve());
            return new PhotoPixels
            {
                Image = PluginPipeline.FromRaw16(dev), Space = PluginColorSpace.Srgb,
                FullWidth = _baseInfo?.Width ?? raw.W, FullHeight = _baseInfo?.Height ?? raw.H
            };
        }
        return SourcePixels(maxLongSide);
    }

    private PhotoPixels? RenderedPixels(int maxLongSide)
    {
        if (_lastDisplayed is not BitmapSource bmp) return null;
        var box = _lastDisplayedBox;
        return new PhotoPixels
        {
            Image = PluginPipeline.FromBitmap(Shrink(bmp, maxLongSide), out _, out _), Space = PluginColorSpace.Srgb,
            FullWidth = box.Width >= 1 ? (int)Math.Round(box.Width) : bmp.PixelWidth,
            FullHeight = box.Height >= 1 ? (int)Math.Round(box.Height) : bmp.PixelHeight
        };
    }

    private static BitmapSource Shrink(BitmapSource bmp, int maxLongSide)
    {
        int lon = Math.Max(bmp.PixelWidth, bmp.PixelHeight);
        if (maxLongSide <= 0 || lon <= maxLongSide) return bmp;
        double s = (double)maxLongSide / lon;
        var t = new TransformedBitmap(bmp, new ScaleTransform(s, s));
        t.Freeze();
        return t;
    }

    // ------------------------------------------------------------------
    //  Overlays
    // ------------------------------------------------------------------

    private bool _overlayRefreshQueued;

    private void BuildOverlayMenu()
    {
        foreach (var o in _pluginReg.Overlays) o.D.OnRefresh = RefreshPluginOverlays;
        if (MnuPluginOverlays is not null)
        {
            MnuPluginOverlays.Items.Clear();
            foreach (var o in _pluginReg.Overlays)
            {
                var label = string.IsNullOrWhiteSpace(o.D.D.Label) ? o.LocalId : o.D.D.Label;
                var it = new MenuItem
                {
                    Header = label, IsCheckable = true, IsChecked = o.D.Visible,
                    ToolTip = "Plugin " + o.PluginName, IsEnabled = o.Owner is not { Faulted: true }
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(it, "PluginOverlay_" + o.FullId);
                var oo = o;
                it.Click += (_, _) => oo.D.Visible = it.IsChecked;
                MnuPluginOverlays.Items.Add(it);
            }
            TileOverlays.Visibility = MnuPluginOverlays.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshPluginOverlays();
    }

    /// <summary>Asks the visible plugin overlays for new content (once per dispatcher round).</summary>
    private void RefreshPluginOverlays()
    {
        if (_overlayRefreshQueued) return;
        _overlayRefreshQueued = true;
        Dispatcher.BeginInvoke(DrawPluginOverlays, DispatcherPriority.Background);
    }

    private void DrawPluginOverlays()
    {
        _overlayRefreshQueued = false;
        if (PluginOverlays is null) return;
        var visible = _pluginReg.Overlays.Where(o => o.D.Visible && o.Owner is not { Faulted: true })
            .Select(o => (Draw: o.D.D.Draw, o.Owner, What: "overlay " + o.LocalId)).ToList();
        // The active pointer tool draws on top (brush outline, guides).
        if (ActiveToolReg() is { Owner: not { Faulted: true } } tool && tool.D.D.Draw is { } td && !ToolInputBlocked)
            visible.Add((td, tool.Owner, "tool " + tool.LocalId));
        if (visible.Count == 0 || _currentPath is null || _cropMode || _baseInfo is not ImageInfo bi || (_lastDisplayed ?? _baseBitmap) is null)
        {
            PluginOverlays.SetLayers(Array.Empty<OverlayContent>());
            PluginOverlays.Visibility = Visibility.Collapsed;
            return;
        }
        PhotoPixels? rendered = null;
        bool fetched = false;
        var req = new OverlayRequest
        {
            Path = _currentPath,
            Meta = CurrentMeta(),
            Rendered = () =>
            {
                if (!fetched)
                {
                    fetched = true;
                    rendered = RenderedPixels(1024);
                }
                return rendered!;
            }
        };
        var layers = new List<OverlayContent>();
        foreach (var o in visible)
        {
            try
            {
                if (o.Draw?.Invoke(req) is { } c) layers.Add(c);
            }
            catch (Exception ex)
            {
                PluginManager.Instance.ReportFailure(o.Owner, o.What, ex);
            }
        }
        PluginOverlays.SetMapping(CurrentDisplayGeo(), bi.Width, bi.Height);
        PluginOverlays.Visibility = layers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginOverlays.SetLayers(layers);
    }

    // ------------------------------------------------------------------
    //  Parameter dialog (Ask, export options)
    // ------------------------------------------------------------------

    private FilterParams? ShowParamsDialog(string title, IReadOnlyList<ParamDef> defs, string memoryKey)
    {
        _exportOptions.TryGetValue(memoryKey, out var last);
        var values = new Dictionary<string, double>();
        var text = new Dictionary<string, string>();
        foreach (var d in defs)
        {
            if (d.IsText) text[d.Id] = last.Text is not null && last.Text.TryGetValue(d.Id, out var t) ? t : d.DefaultText ?? "";
            else values[d.Id] = last.Values is not null && last.Values.TryGetValue(d.Id, out var v) ? v : d.Default;
        }

        var w = new Window
        {
            Title = string.IsNullOrWhiteSpace(title) ? "PhotographerEye" : title,
            Owner = this,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        w.SetResourceReference(BackgroundProperty, "PanelBrush");
        w.SetResourceReference(ForegroundProperty, "ForegroundBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var sp = new StackPanel { Margin = new Thickness(14), Width = 300 };
        var readers = new List<Action>();
        foreach (var d in defs)
        {
            var pd = d;
            switch (d.Kind)
            {
                case ParamKind.Toggle:
                {
                    var cb = new CheckBox { Content = d.Label, IsChecked = values[d.Id] >= 0.5, Margin = new Thickness(0, 8, 0, 0) };
                    readers.Add(() => values[pd.Id] = cb.IsChecked == true ? 1 : 0);
                    sp.Children.Add(cb);
                    break;
                }
                case ParamKind.Choice:
                {
                    sp.Children.Add(LabelRow(d.Label, out _, muted));
                    var combo = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
                    foreach (var ch in d.Choices)
                    {
                        var it = new ComboBoxItem { Content = ch.Label, Tag = ch.Id };
                        combo.Items.Add(it);
                        if (ch.Id == text[d.Id]) combo.SelectedItem = it;
                    }
                    if (combo.SelectedItem is null && combo.Items.Count > 0) combo.SelectedIndex = 0;
                    readers.Add(() => { if (combo.SelectedItem is ComboBoxItem { Tag: string id }) text[pd.Id] = id; });
                    sp.Children.Add(combo);
                    break;
                }
                case ParamKind.Slider:
                {
                    sp.Children.Add(LabelRow(d.Label, out var v, muted));
                    double min = Math.Min(d.Min, d.Max), max = Math.Max(d.Min, d.Max);
                    var s = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(values[d.Id], min, max), SmallChange = Math.Max(0.001, d.Step), LargeChange = Math.Max(0.01, d.Step * 10), Margin = new Thickness(0, 4, 0, 0) };
                    PhotoshopSlider.SetEnable(s, true);
                    v.Text = FormatParam(s.Value, d);
                    s.ValueChanged += (_, e) => v.Text = FormatParam(e.NewValue, pd);
                    readers.Add(() => values[pd.Id] = s.Value);
                    sp.Children.Add(s);
                    break;
                }
                case ParamKind.File:
                {
                    sp.Children.Add(LabelRow(d.Label, out _, muted));
                    var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                    var browse = new Button { Content = "…", Width = 28, Margin = new Thickness(4, 0, 0, 0) };
                    DockPanel.SetDock(browse, Dock.Right);
                    var tb = new TextBox { Text = text[d.Id] };
                    browse.Click += (_, _) =>
                    {
                        var dlg = new OpenFileDialog { Title = pd.Label };
                        if (dlg.ShowDialog(w) == true) tb.Text = dlg.FileName;
                    };
                    row.Children.Add(browse);
                    row.Children.Add(tb);
                    readers.Add(() => text[pd.Id] = tb.Text);
                    sp.Children.Add(row);
                    break;
                }
                default:
                {
                    sp.Children.Add(LabelRow(d.Label, out _, muted));
                    var tb = new TextBox { Text = text[d.Id], Margin = new Thickness(0, 4, 0, 0) };
                    readers.Add(() => text[pd.Id] = tb.Text);
                    sp.Children.Add(tb);
                    break;
                }
            }
        }
        sp.Children.Add(DialogButtons(w));
        w.Content = sp;
        if (w.ShowDialog() != true) return null;
        foreach (var r in readers) r();
        _exportOptions[memoryKey] = (new Dictionary<string, double>(values), new Dictionary<string, string>(text));
        return new FilterParams(defs, values, text);
    }

    // ------------------------------------------------------------------
    //  Viewer control
    // ------------------------------------------------------------------

    private sealed class ViewerControl : IViewerControl
    {
        private readonly MainWindow _w;
        public ViewerControl(MainWindow w) => _w = w;

        public double Zoom => _w.Viewer.Zoom;

        public void SetZoom(double zoom, double centerX = double.NaN, double centerY = double.NaN)
        {
            if (!double.IsFinite(zoom) || zoom <= 0) throw new ArgumentOutOfRangeException(nameof(zoom));
            _w.Viewer.SetZoom(zoom);
            if (double.IsFinite(centerX) && double.IsFinite(centerY)) _w.Viewer.CenterOn(centerX, centerY);
        }

        public void FitToWindow() => _w.Viewer.FitToWindow();
        public void ActualSize() => _w.Viewer.SetZoom(1.0);

        public bool ShowOriginal
        {
            get => _w._compareOn;
            set => _w.SetCompare(value);
        }
    }

    // ------------------------------------------------------------------
    //  Command context
    // ------------------------------------------------------------------

    private sealed class CommandContext : ICommandContext
    {
        private readonly MainWindow _w;
        private readonly string _pid;
        private readonly LoadedPlugin? _owner;
        private EyeState? _st;
        private bool _stDirty;
        public bool Dirty;

        public CommandContext(MainWindow w, string pluginId, LoadedPlugin? owner, string? label)
        {
            _w = w;
            _pid = pluginId;
            _owner = owner;
            StepLabel = label ?? "";
        }

        public string StepLabel { get; set; }
        /// <summary>Context of a pointer tool press (one undo step for the whole press).</summary>
        public bool IsPress { get; init; }
        public LoadedPlugin? Owner => _owner;
        /// <summary>Changes are waiting to be applied.</summary>
        public bool Changed => _stDirty || Dirty;
        public string CurrentPath => _w._currentPath!;
        public ImageMeta Meta => _w.CurrentMeta();
        public void SetStatus(string text) => _w.SetStatus(text ?? "", error: false);
        public void ShowMessage(string text) => MessageBox.Show(_w, text ?? "", "PhotographerEye");

        /// <summary>Applies the collected changes and records them as one undo step.</summary>
        public void Finish()
        {
            if (_stDirty && _st is not null) Flush();
            else if (Dirty) _w.RebuildPluginUi();
            if (Dirty) _w.OnPluginUiChanged();
            _w.CommitStep(StepLabel);
            _w.RefreshPluginOverlays();
        }

        /// <summary>Applies the changes collected so far for preview, without recording a step (pointer tool presses).</summary>
        public void Apply()
        {
            if (!Changed) return;
            if (_stDirty && _st is not null) Flush();
            else if (Dirty) _w.RebuildPluginUi();
            if (Dirty) _w.OnPluginUiChanged();
            Dirty = false;
            _w._histCommitTimer?.Stop();
            _w.RefreshPluginOverlays();
        }

        private void Flush()
        {
            if (!_stDirty || _st is null) return;
            var ps = _w._pluginState.Save();
            _w.ApplyEyeStateSuppressed(_st with
            {
                PluginValues = ps.Values, PluginActive = ps.Active, PluginText = ps.Text, PluginOrder = ps.Order, PluginMasks = ps.Masks
            });
            _st = null;
            _stDirty = false;
        }

        private EyeState St => _st ??= _w.CaptureEyeState() with { History = null, HistoryPos = null };

        private void Put(EyeState s)
        {
            _st = s;
            _stDirty = true;
        }

        // ---- plugin filters ----

        private Registered<FilterDescriptor> F(string id) =>
            _w._pluginReg.FindFilter(id.Contains(':') ? id : _pid + ":" + id) ?? throw new ArgumentException($"unknown filter '{id}'");

        private static ParamDef P(Registered<FilterDescriptor> f, string id) =>
            f.D.Params.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"unknown parameter '{id}' of {f.FullId}");

        public double GetValue(string filterId, string paramId)
        {
            var f = F(filterId);
            return _w._pluginState.GetValue(f, P(f, paramId));
        }

        public void SetValue(string filterId, string paramId, double value)
        {
            var f = F(filterId);
            var d = P(f, paramId);
            if (d.IsText) throw new ArgumentException($"parameter '{paramId}' is text; use SetText");
            if (!double.IsFinite(value)) throw new ArgumentException("value must be finite");
            if (d.Kind == ParamKind.Slider) value = Math.Clamp(value, Math.Min(d.Min, d.Max), Math.Max(d.Min, d.Max));
            _w._pluginState.SetValue(f, d, value);
            Dirty = true;
        }

        public void SetText(string filterId, string paramId, string value)
        {
            var f = F(filterId);
            var d = P(f, paramId);
            if (!d.IsText) throw new ArgumentException($"parameter '{paramId}' is numeric; use SetValue");
            _w._pluginState.SetText(f, d, value);
            Dirty = true;
        }

        public string GetText(string filterId, string paramId)
        {
            var f = F(filterId);
            var d = P(f, paramId);
            if (!d.IsText) throw new ArgumentException($"parameter '{paramId}' is numeric; use GetValue");
            return _w._pluginState.GetText(f, d);
        }

        public bool IsActive(string filterId) => _w._pluginState.IsFilterOn(F(filterId));

        public void SetActive(string filterId, bool active)
        {
            var f = F(filterId);
            if (active) _w._pluginState.Active.Add(f.FullId);
            else _w._pluginState.Active.Remove(f.FullId);
            Dirty = true;
        }

        public void ApplyPreset(string presetId)
        {
            var reg = _w._pluginReg;
            var all = PresetStore.FromRegistry(reg);
            for (int i = 0; i < reg.Presets.Count && i < all.Count; i++)
            {
                if (reg.Presets[i].PluginId != _pid || reg.Presets[i].D.Id != presetId) continue;
                Flush();
                _st = null;
                _w._histSuppress = true;
                try
                {
                    _w.ApplyPreset(all[i]);
                }
                finally
                {
                    _w._histSuppress = false;
                }
                Dirty = true;
                return;
            }
            throw new ArgumentException($"unknown preset '{presetId}'");
        }

        // ---- core settings ----

        public IReadOnlyList<string> CoreKeys => CoreKeyNames;

        private static string K(string key) => CoreKey(key) ?? throw new ArgumentException($"unknown core setting '{key}'");

        public double GetCore(string key) => CoreGet(St, K(key));

        public void SetCore(string key, double value)
        {
            var k = K(key);
            if (!double.IsFinite(value)) throw new ArgumentException("value must be finite");
            var r = GetCoreRange(k);
            Put(CoreSet(St, k, Math.Clamp(value, r.Min, r.Max)));
        }

        public CoreRange GetCoreRange(string key)
        {
            var p = CoreParams.Find(K(key))!;
            return new CoreRange { Min = p.Min, Max = p.Max, Default = p.Default };
        }

        // ---- geometry ----

        public PhotoGeometry GetGeometry()
        {
            var s = St;
            return new PhotoGeometry { Rot90 = s.Rot90, Angle = s.Angle, CropX = s.CropX, CropY = s.CropY, CropW = s.CropW, CropH = s.CropH, Aspect = s.CropAspect };
        }

        public void SetGeometry(PhotoGeometry g)
        {
            if (g is null) throw new ArgumentNullException(nameof(g));
            double[] v = { g.Angle, g.CropX, g.CropY, g.CropW, g.CropH };
            if (v.Any(x => !double.IsFinite(x))) throw new ArgumentException("geometry values must be finite");
            var aspect = string.IsNullOrWhiteSpace(g.Aspect) ? "original" : g.Aspect.Trim();
            if (!CropAspects.Contains(aspect)) throw new ArgumentException($"unknown aspect '{g.Aspect}' ({string.Join(", ", CropAspects)})");
            double min = PhotoGeo.MinFraction;
            double cx = Math.Clamp(g.CropX, 0.0, 1.0 - min), cy = Math.Clamp(g.CropY, 0.0, 1.0 - min);
            Put(St with
            {
                Rot90 = ((g.Rot90 % 4) + 4) % 4,
                Angle = Math.Clamp(g.Angle, -45.0, 45.0),
                CropX = cx,
                CropY = cy,
                CropW = Math.Clamp(g.CropW <= 0 ? 1.0 : g.CropW, min, 1.0 - cx),
                CropH = Math.Clamp(g.CropH <= 0 ? 1.0 : g.CropH, min, 1.0 - cy),
                CropAspect = aspect
            });
        }

        public void ResetGeometry() => SetGeometry(new PhotoGeometry());

        // ---- curves ----

        public double[][] GetCurve(CurveKind kind)
        {
            var s = St;
            switch (kind)
            {
                case CurveKind.Red: return CopyCurve(s.CurveR);
                case CurveKind.Green: return CopyCurve(s.CurveG);
                case CurveKind.Blue: return CopyCurve(s.CurveB);
                case CurveKind.LevelsRed: return CopyCurve(s.LevelR);
                case CurveKind.LevelsGreen: return CopyCurve(s.LevelG);
                case CurveKind.LevelsBlue: return CopyCurve(s.LevelB);
                case CurveKind.ToneCurve: return CopyCurve(s.ToneCurve);
                case CurveKind.StretchEv:
                    if (s.StretchEv is { Length: > 0 } || s.Stretch is not { Length: > 0 } st) return CopyCurve(s.StretchEv);
                    return st.Select(p => new[] { PhotoOps.DisplayFractionToEv(p[0], s.Gamma), PhotoOps.DisplayFractionToEv(p[1], s.Gamma) }).ToArray();
                case CurveKind.Stretch:
                    if (s.StretchEv is { Length: > 0 } ev)
                        return ev.Select(p => new[] { PhotoOps.EvToDisplayFraction(p[0], s.Gamma), PhotoOps.EvToDisplayFraction(p[1], s.Gamma) }).ToArray();
                    return CopyCurve(s.Stretch);
                default: throw new ArgumentException($"unknown curve {kind}");
            }
        }

        public void SetCurve(CurveKind kind, double[][] points)
        {
            var s = St;
            switch (kind)
            {
                case CurveKind.Red: Put(s with { CurveR = CleanCurve(points, 0, 1) }); break;
                case CurveKind.Green: Put(s with { CurveG = CleanCurve(points, 0, 1) }); break;
                case CurveKind.Blue: Put(s with { CurveB = CleanCurve(points, 0, 1) }); break;
                case CurveKind.LevelsRed: Put(s with { LevelR = CleanCurve(points, 0, 1) }); break;
                case CurveKind.LevelsGreen: Put(s with { LevelG = CleanCurve(points, 0, 1) }); break;
                case CurveKind.LevelsBlue: Put(s with { LevelB = CleanCurve(points, 0, 1) }); break;
                case CurveKind.Stretch:
                {
                    var c = CleanCurve(points, 0, 1);
                    Put(s with { Stretch = c, StretchEv = null });
                    break;
                }
                case CurveKind.StretchEv:
                {
                    var c = CleanCurve(points, PhotoOps.ToneEvMin, PhotoOps.ToneEvMax);
                    Put(s with { StretchEv = c, Stretch = null });
                    break;
                }
                case CurveKind.ToneCurve:
                {
                    var c = CleanCurve(points, 0, 1);
                    if (c is not null && ParseCurve(c) is null) throw new ArgumentException("not a valid tone curve (at least 2 increasing points)");
                    Put(s with { ToneCurve = c });
                    break;
                }
                default: throw new ArgumentException($"unknown curve {kind}");
            }
        }

        public bool SetWhitePoint(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > 1 || y > 1) return false;
            if (!_w.SampleSourceLinear(x, y, out double r, out double g, out double b, out bool clipped) || clipped) return false;
            ApplyNeutral(r, g, b);
            return true;
        }

        public bool SetWhiteBalanceFromNeutral(double r, double g, double b)
        {
            if (!double.IsFinite(r) || !double.IsFinite(g) || !double.IsFinite(b) || r <= 0 || g <= 0 || b <= 0) return false;
            return ApplyNeutral(r, g, b);
        }

        private bool ApplyNeutral(double r, double g, double b)
        {
            bool inRange = PhotoOps.WbFromLinearSample(r, g, b, out double temp, out double tint);
            var rt = GetCoreRange("Temperature");
            var rn = GetCoreRange("Tint");
            Put(St with
            {
                Temperature = Math.Clamp(Math.Round(temp * 100.0), rt.Min, rt.Max),
                Tint = Math.Clamp(Math.Round(tint * 100.0), rn.Min, rn.Max)
            });
            return inRange;
        }

        // ---- pixels ----

        public PhotoPixels GetImage(PhotoImageKind kind, int maxLongSide) => (kind switch
        {
            PhotoImageKind.Rendered => _w.RenderedPixels(maxLongSide),
            PhotoImageKind.Developed => _w.DevelopedPixels(maxLongSide),
            PhotoImageKind.Source => _w.SourcePixels(maxLongSide),
            _ => throw new ArgumentException($"unknown image kind {kind}")
        })!;

        public PhotoHistogram GetHistogram()
        {
            if (_w.HistogramPanel.Histogram is not { } h) return null!;
            return new PhotoHistogram { Luma = (int[])h.Lum.Clone(), R = (int[])h.R.Clone(), G = (int[])h.G.Clone(), B = (int[])h.B.Clone() };
        }

        // ---- interaction ----

        public FilterParams Ask(string title, IReadOnlyList<ParamDef> parameters)
        {
            if (parameters is null) throw new ArgumentNullException(nameof(parameters));
            if (parameters.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id)))
                throw new ArgumentException("every parameter needs an Id");
            if (parameters.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != parameters.Count)
                throw new ArgumentException("parameter ids must be unique");
            return _w.ShowParamsDialog(title ?? "", parameters, "ask:" + _pid + ":" + (title ?? ""))!;
        }

        public string PickOpenFile(string title, string filter)
        {
            var dlg = new OpenFileDialog { Title = title ?? "" };
            SetFilter(dlg, filter);
            return dlg.ShowDialog(_w) == true ? dlg.FileName : null!;
        }

        public string PickSaveFile(string title, string filter, string defaultName)
        {
            var dlg = new SaveFileDialog { Title = title ?? "", FileName = defaultName ?? "", OverwritePrompt = true };
            SetFilter(dlg, filter);
            return dlg.ShowDialog(_w) == true ? dlg.FileName : null!;
        }

        private static void SetFilter(FileDialog dlg, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return;
            try
            {
                dlg.Filter = filter;
            }
            catch (ArgumentException)
            {
                throw new ArgumentException($"bad file filter '{filter}' (use \"Name|*.ext;*.ext2\")");
            }
        }

        public void RunInBackground(string label, Func<IProgress<double>, CancellationToken, object> work, Action<ICommandContext, object> done)
        {
            if (work is null) throw new ArgumentNullException(nameof(work));
            _w.StartBackground(_pid, _owner, label, string.IsNullOrWhiteSpace(StepLabel) ? label : StepLabel, work, done);
        }

        // ---- viewer ----

        private ViewerControl? _view;
        public IViewerControl View => _view ??= new ViewerControl(_w);

        // ---- masks ----

        private string MaskId(string filterId)
        {
            if (string.Equals(filterId, AdjustMask.Key, StringComparison.Ordinal)) return AdjustMask.Key;
            var f = F(filterId);
            if (!f.D.SupportsMask) throw new ArgumentException($"filter {f.FullId} does not support masks");
            return f.FullId;
        }

        public MaskInfo GetMask(string filterId) =>
            ToMaskInfo(_w._pluginState.Masks.TryGetValue(MaskId(filterId), out var m) ? m : null)!;

        public void SetMask(string filterId, MaskInfo mask)
        {
            var id = MaskId(filterId);
            var spec = ToMaskSpec(mask);
            if (spec is null) _w._pluginState.Masks.Remove(id);
            else _w._pluginState.Masks[id] = spec;
            _w.SyncMaskOverlaySpec();
            Dirty = true;
        }
    }
}
