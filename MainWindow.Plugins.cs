using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Controls;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;
using Microsoft.Win32;

namespace ImgViewer;

/// <summary>Plugin integration of the main window: panel/toolbar UI, masks, presets, export, commands, events.</summary>
public partial class MainWindow
{
    private PluginRegistry _pluginReg = PluginRegistry.Empty;
    private readonly PluginEditState _pluginState = new();
    private readonly Dictionary<string, ToggleButton> _pluginToggles = new(StringComparer.Ordinal);
    private readonly List<(KeyGesture Gesture, Registered<CommandDescriptor> Cmd)> _pluginShortcuts = new();
    private readonly Dictionary<string, (Dictionary<string, double> Values, Dictionary<string, string> Text)> _exportOptions = new(StringComparer.Ordinal);
    private string? _maskEditFilter;
    private (Registered<FilterDescriptor> F, ParamDef D)? _pointPick;
    /// <summary>Runs after a successful point pick (Add of a repeat-group item).</summary>
    private Action? _pointPickDone;
    /// <summary>Selected item of each repeat group ("pluginId|group" → filter FullId).</summary>
    private readonly Dictionary<string, string> _repeatSelected = new(StringComparer.Ordinal);
    private ImageMeta? _pluginMeta;
    private string? _openedRaisedFor;
    private int _maskPreviewSeq;
    private HashSet<string> _importerExts = new(StringComparer.OrdinalIgnoreCase);
    private bool _pluginUiBuilding;

    private static readonly string[] ReservedShortcuts = { "Ctrl+O", "Ctrl+S", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z" };

    private sealed record FullSnapshot(
        string Path, bool IsRaw, double S, double G, ManualStretchMap Manual, PhotoAdjustments? Extra,
        MonoMode Mono, int Seed, CameraToneCurve? Curve, double BaseEv, GeoState Geo, PluginRenderJob Job);

    // ------------------------------------------------------------------
    //  Setup / registry changes
    // ------------------------------------------------------------------

    private void InitPlugins()
    {
        InitEditHistoryBridge();
        _pluginReg = PluginManager.Instance.Registry;
        _importerExts = new HashSet<string>(_pluginReg.ImporterExtensions, StringComparer.OrdinalIgnoreCase);
        MaskOverlay.Attach(Viewer);
        PointMarkers.Attach(Viewer);
        PluginOverlays.Attach(Viewer);
        InitTools();
        PointMarkers.MarkerMoved += OnPointMarkerMoved;
        PointMarkers.MarkerGrabbed += OnPointMarkerGrabbed;
        MaskOverlay.SpecChanged += OnMaskSpecChanged;
        PluginManager.Instance.Changed += OnPluginsChanged;
        PluginManager.Instance.PluginFaulted += OnPluginFaulted;
        try
        {
            PluginManager.Instance.Reload();
        }
        catch (Exception ex)
        {
            Debug.Log($"PLUGINS: reload failed [{ex.GetType().Name}] {ex.Message}");
        }
        _pluginReg = PluginManager.Instance.Registry;
        RebuildPluginUi();
    }

    private void OnPluginsClick(object sender, RoutedEventArgs e)
    {
        var w = new PluginsWindow { Owner = this };
        w.ShowDialog();
    }

    private void OnPluginsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnPluginsChanged);
            return;
        }
        _pluginReg = PluginManager.Instance.Registry;
        if (_maskEditFilter is not null && MaskTarget(_maskEditFilter) is null) EndMaskEdit();
        CancelPointPick();
        RebuildPluginUi();

        var exts = new HashSet<string>(_pluginReg.ImporterExtensions, StringComparer.OrdinalIgnoreCase);
        if (!exts.SetEquals(_importerExts))
        {
            _importerExts = exts;
            if (_dir is not null)
            {
                RefreshFiles();
                int i = _currentPath is null ? -1 : _files.FindIndex(f => string.Equals(f, _currentPath, StringComparison.OrdinalIgnoreCase));
                if (i >= 0)
                {
                    _index = i;
                    CounterText.Text = $"{i + 1} / {_files.Count}";
                }
            }
        }

        UpdateSaveState();
        ReapplyAdjustments();
        var err = PluginManager.Instance.Files.FirstOrDefault(f => f.Enabled && f.Error is not null);
        int loaded = _pluginReg.Plugins.Count;
        if (err is not null) SetStatus($"Plugin {err.FileName}: {err.Error}", error: true);
        else if (loaded > 0) SetStatus($"Plugins: {loaded} loaded, {_pluginReg.Filters.Count(f => !f.IsCore)} filters", error: false);
    }

    private void OnPluginFaulted(LoadedPlugin plugin, string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SetStatus($"Plugin '{plugin.Name}' disabled after repeated errors: {reason}", error: true);
            RebuildPluginUi();
            UpdateSaveState();
            ReapplyAdjustments();
        });
    }

    private bool PluginHasEffect() => _pluginState.HasEffect(_pluginReg);

    private ImageMeta CurrentMeta()
    {
        var p = _currentPath ?? "";
        if (_pluginMeta is { } m && string.Equals(m.SourcePath, p, StringComparison.OrdinalIgnoreCase)) return m;
        return new ImageMeta
        {
            SourcePath = p,
            IsRaw = p.Length > 0 && RawDecoder.IsRaw(p),
            Width = _baseInfo?.Width ?? 0,
            Height = _baseInfo?.Height ?? 0
        };
    }

    private PluginRenderJob PluginJob(bool preview, double scale, CancellationToken ct) => new()
    {
        Registry = _pluginReg,
        State = _pluginState.Clone(),
        Meta = CurrentMeta(),
        IsPreview = preview,
        Scale = Math.Clamp(scale, 1e-4, 1.0),
        Cancel = ct
    };

    private GeoState CurrentDisplayGeo() =>
        _cropMode ? _geo with { CropX = 0.0, CropY = 0.0, CropW = 1.0, CropH = 1.0 } : _geo;

    private string PluginMissingNote()
    {
        var miss = _pluginState.MissingPlugins(_pluginReg);
        return miss.Count > 0 ? $"   missing plugins: {string.Join(", ", miss)}" : "";
    }

    private string PluginOpenPatterns() => string.Concat(_pluginReg.ImporterExtensions.Select(e => ";*" + e));

    private void ReportPluginErrors(string path, PluginRenderJob job)
    {
        if (job.Report.Errors.Count == 0) return;
        var msg = string.Join("; ", job.Report.Errors.Distinct());
        Dispatcher.BeginInvoke(() =>
        {
            if (IsCurrent(path)) SetStatus($"Plugin error: {msg}", error: true);
        });
    }

    private static string PluginCanon(EyeState s)
    {
        return JsonSerializer.Serialize(new
        {
            V = s.PluginValues is { Count: > 0 } v ? new SortedDictionary<string, double>(v, StringComparer.Ordinal) : null,
            A = s.PluginActive is { Count: > 0 } a ? a.OrderBy(x => x, StringComparer.Ordinal).ToList() : null,
            T = s.PluginText is { Count: > 0 } t ? new SortedDictionary<string, string>(t, StringComparer.Ordinal) : null,
            O = s.PluginOrder is { Count: > 0 } o ? o : null,
            M = s.PluginMasks is { Count: > 0 } m ? new SortedDictionary<string, MaskSpec>(m, StringComparer.Ordinal) : null
        });
    }

    private static bool PluginEyeEqual(EyeState a, EyeState b) => PluginCanon(a) == PluginCanon(b);

    // ------------------------------------------------------------------
    //  Full-resolution render shared by Save and Export
    // ------------------------------------------------------------------

    private FullSnapshot CaptureFullSnapshot(string path) => new(
        path, RawDecoder.IsRaw(path), ShadowSlider.Value / 100.0, GammaSlider.Value, HistogramPanel.Snapshot(), PanelExtras(),
        _monoMode, _monoSeed, CurrentCurve(), CurrentBaseEv(), _geo, PluginJob(false, 1.0, CancellationToken.None));

    /// <summary>
    /// Exposure/white balance gain, SceneLinear plugins and the Adjust16 tone pass. With an Adjustments mask the
    /// panel values are blended in by the mask (<paramref name="blended"/>; the histogram is then not computed).
    /// </summary>
    private static Raw16Image Correct16(Raw16Image raw16, double baseEv, PhotoAdjustments? extra, double s, double g, ManualStretchMap? manual,
        MonoMode mono, int seed, CameraToneCurve? curve, PluginRenderJob job, int fullW, int fullH, CancellationToken ct,
        out HistogramData? hist, out bool blended, StageCache? cache = null)
    {
        // Live preview: gain, Base and SceneLinear plugins reused while their inputs are unchanged.
        Raw16Image rendered = cache is not null
            ? cache.Linear(raw16, baseEv, extra?.Exposure ?? 0.0, extra?.Temperature ?? 0.0, extra?.Tint ?? 0.0, job, fullW, fullH, ct)
            : StageCache.LinearUncached(raw16, baseEv, extra?.Exposure ?? 0.0, extra?.Temperature ?? 0.0, extra?.Tint ?? 0.0, job, fullW, fullH, ct);
        var extra2 = StripExposureTemp(extra);
        var corrected = Adjust16.TryApplyGpu(rendered, s, g, manual, out hist, mono: mono, grainSeed: seed, extra: extra2, ct: ct, cameraTone: curve)
            ?? Adjust16.Apply(rendered, s, g, manual, out hist, mono: mono, grainSeed: seed, extra: extra2, ct: ct, cameraTone: curve);
        blended = false;
        var amask = AdjustMask.For(job.State);
        if (amask is null || extra is null || ct.IsCancellationRequested) return corrected;

        var neutralIn = rendered;
        if (Math.Abs(extra.Exposure) > 1e-9 || Math.Abs(extra.Temperature) > 1e-9 || Math.Abs(extra.Tint) > 1e-9)
        {
            neutralIn = StageCache.LinearUncached(raw16, baseEv, 0.0, 0.0, 0.0, job, fullW, fullH, ct);
        }
        var neutral = Adjust16.TryApplyGpu(neutralIn, s, g, manual, out _, mono: mono, grainSeed: seed, extra: null, ct: ct, cameraTone: curve)
            ?? Adjust16.Apply(neutralIn, s, g, manual, out _, mono: mono, grainSeed: seed, extra: null, ct: ct, cameraTone: curve);
        ct.ThrowIfCancellationRequested();
        hist = null;
        blended = true;
        return AdjustMask.Blend16(corrected, neutral, amask, job, fullW, fullH);
    }

    /// <summary>The live preview may run on the GPU end to end: no Base/SceneLinear/Display plugin and no Adjustments mask.</summary>
    private static bool GpuDisplayAllowed(PluginRenderJob job) =>
        AdjustMask.For(job.State) is null && !job.Has(FilterStage.Base) && !job.Has(FilterStage.SceneLinear) && !job.Has(FilterStage.Display);

    /// <summary>8-bit counterpart of <see cref="Correct16"/>; <paramref name="src"/> already went through SceneLinear.</summary>
    private static BitmapSource? Correct8(BitmapSource src, PhotoAdjustments? extra, double s, double g, ManualStretchMap? manual,
        MonoMode mono, int seed, PluginRenderJob job, int fullW, int fullH, out HistogramData? hist, out bool blended)
    {
        var processed = AdjustmentsProcessor.Apply(src, s, g, manual, out hist, mono: mono, grainSeed: seed, extra: extra);
        blended = false;
        var amask = AdjustMask.For(job.State);
        if (processed is null || amask is null || extra is null || job.Cancel.IsCancellationRequested) return processed;
        BitmapSource neutral = AdjustmentsProcessor.Apply(src, s, g, manual, out _, mono: mono, grainSeed: seed, extra: null) ?? src;
        hist = null;
        blended = true;
        return AdjustMask.Blend8(processed, neutral, amask, job, fullW, fullH);
    }

    private static (Raw16Image? R16, BitmapSource? Bmp) RenderFull(FullSnapshot sn)
    {
        var job = sn.Job;
        if (sn.IsRaw)
        {
            var base16 = RawDecoder.Decode16(sn.Path, halfSize: false);
            int fw = base16.W, fh = base16.H;
            var corrected = Correct16(base16, sn.BaseEv, sn.Extra, sn.S, sn.G, sn.Manual, sn.Mono, sn.Seed, sn.Curve, job, fw, fh, CancellationToken.None, out _, out _);
            corrected = PluginPipeline.ApplyRaw16(corrected, FilterStage.Display, job, fw, fh) ?? corrected;
            var geo16 = PhotoGeo.Apply(corrected, sn.Geo);
            job.OutputGeo = (sn.Geo, corrected.W, corrected.H);
            geo16 = PluginPipeline.ApplyRaw16(geo16, FilterStage.Output, job, geo16.W, geo16.H) ?? geo16;
            return (geo16, null);
        }

        var info = ImageLoader.GetInfo(sn.Path);
        var full = ImageLoader.LoadScaled(sn.Path, info, info.Width, info.Height);
        int w = full.PixelWidth, h = full.PixelHeight;
        var src = PluginPipeline.ApplyBitmapLinearStages(full, job, w, h) ?? full;
        var processed = Correct8(src, sn.Extra, sn.S, sn.G, sn.Manual, sn.Mono, sn.Seed, job, w, h, out _, out _)
            ?? throw new InvalidOperationException("Failed to process image");
        BitmapSource outB = PluginPipeline.ApplyBitmap(processed, FilterStage.Display, job, w, h) ?? processed;
        var (geo8, _) = PhotoGeo.Apply(outB, new Size(outB.PixelWidth, outB.PixelHeight), sn.Geo);
        job.OutputGeo = (sn.Geo, outB.PixelWidth, outB.PixelHeight);
        geo8 = PluginPipeline.ApplyBitmap(geo8, FilterStage.Output, job, geo8.PixelWidth, geo8.PixelHeight) ?? geo8;
        return (null, geo8);
    }

    // ------------------------------------------------------------------
    //  Status metadata, metadata providers, ImageOpened
    // ------------------------------------------------------------------

    private void UpdateStatusMeta(string path, bool full)
    {
        int w = _baseInfo?.Width ?? 0, h = _baseInfo?.Height ?? 0;
        bool raise = full && !string.Equals(_openedRaisedFor, path, StringComparison.OrdinalIgnoreCase);
        if (raise) _openedRaisedFor = path;
        var reg = _pluginReg;
        Task.Run(() =>
        {
            var meta = PhotoMetaReader.Get(path);
            ImageMeta im;
            try { im = PluginManager.MetaFor(path, w, h); }
            catch (Exception) { im = new ImageMeta { SourcePath = path, Width = w, Height = h }; }
            var lines = new List<string>();
            foreach (var mp in reg.MetaProviders)
            {
                if (mp.Owner is { Faulted: true } || mp.D.Provide is null) continue;
                try
                {
                    var kv = mp.D.Provide(im);
                    if (kv is not null)
                        foreach (var p in kv)
                            if (!string.IsNullOrWhiteSpace(p.Key)) lines.Add($"{p.Key}: {p.Value}");
                    PluginManager.ReportSuccess(mp.Owner);
                }
                catch (Exception ex)
                {
                    PluginManager.Instance.ReportFailure(mp.Owner, $"metadata {mp.FullId}", ex);
                }
            }
            if (raise)
            {
                try { PluginManager.Instance.RaiseImageOpened(path, im); }
                catch (Exception ex) { Debug.Log($"PLUGINS: ImageOpened [{ex.GetType().Name}] {ex.Message}"); }
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsCurrent(path)) return;
                _pluginMeta = im;
                var summary = meta?.Summary ?? "";
                StatusRight.Text = lines.Count > 0 ? (summary.Length > 0 ? summary + "   " : "") + string.Join("   ", lines) : summary;
                StatusRight.ToolTip = lines.Count > 0 ? string.Join("\n", lines) : null;
            });
        });
    }

    // ------------------------------------------------------------------
    //  Panel / toolbar UI
    // ------------------------------------------------------------------

    private void RebuildPluginUi()
    {
        if (PluginToolbarHost is null || PluginPanelHost is null) return;
        _pluginUiBuilding = true;
        string? focusedList = FocusedListPanel();
        try
        {
            _listPanelBoxes.Clear();
            _pluginToggles.Clear();
            PluginToolbarHost.Children.Clear();
            PluginPanelHost.Children.Clear();
            BuildPluginToolbar();
            BuildPluginPanel();
            BuildAdjustMaskUi();
            BuildCommandShortcuts();
            UpdatePluginMenus();
            RestoreListPanelFocus(focusedList);
        }
        catch (Exception ex)
        {
            Debug.Log($"PLUGINS: UI build failed [{ex.GetType().Name}] {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            _pluginUiBuilding = false;
        }
    }

    private static bool IsFaulted(Registered<FilterDescriptor> f) => f.Owner is { Faulted: true };

    private static string StageText(FilterStage s) => s switch
    {
        FilterStage.Base => "first, before the manual corrections (linear)",
        FilterStage.SceneLinear => "before develop (linear)",
        FilterStage.Output => "after crop",
        _ => "after develop"
    };

    private static string FilterTooltip(Registered<FilterDescriptor> f)
    {
        var parts = new List<string> { f.IsCore ? f.D.Label : $"{f.D.Label} — plugin {f.PluginName}" };
        if (!string.IsNullOrWhiteSpace(f.D.Description)) parts.Add(f.D.Description);
        parts.Add("Runs " + StageText(f.D.Stage));
        if (f.Owner is { Faulted: true } o) parts.Add("Disabled: " + (o.DisabledReason ?? o.Error ?? "plugin failed"));
        return string.Join("\n", parts);
    }

    private void BuildPluginToolbar()
    {
        foreach (var f in _pluginReg.Filters.Where(f => f.D.Placement == FilterPlacement.Toolbar))
        {
            var btn = new ToggleButton
            {
                Content = f.D.Label,
                MinWidth = 30,
                Height = 26,
                Margin = new Thickness(6, 0, 0, 0),
                IsChecked = _pluginState.Active.Contains(f.FullId),
                IsEnabled = !IsFaulted(f),
                ToolTip = FilterTooltip(f)
            };
            btn.SetResourceReference(StyleProperty, "ToolToggle");
            AutomationProperties.SetAutomationId(btn, "PluginBtn_" + f.FullId);
            var ff = f;
            btn.Click += (_, _) => SetFilterActive(ff, btn.IsChecked == true);
            _pluginToggles[f.FullId] = btn;
            PluginToolbarHost.Children.Add(btn);
        }
        foreach (var c in _pluginReg.Commands.Where(c => c.D.Placement.HasFlag(CommandPlacement.Toolbar)))
        {
            var text = string.IsNullOrEmpty(c.D.ToolbarText) ? c.D.Label : c.D.ToolbarText;
            bool glyph = text.Length == 1 && text[0] >= '' && text[0] <= '';
            var b = new Button
            {
                Content = text,
                MinWidth = 30,
                Height = 26,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = $"{c.D.Label} — plugin {c.PluginName}" + (string.IsNullOrEmpty(c.D.Shortcut) ? "" : $" ({c.D.Shortcut})"),
                IsEnabled = c.Owner is not { Faulted: true }
            };
            if (glyph)
            {
                b.FontFamily = new FontFamily("Segoe MDL2 Assets");
                b.FontSize = 14;
            }
            b.SetResourceReference(StyleProperty, "ToolButton");
            AutomationProperties.SetAutomationId(b, "PluginCmd_" + c.FullId);
            var cc = c;
            b.Click += (_, _) => RunCommand(cc);
            PluginToolbarHost.Children.Add(b);
        }
    }

    private void SetFilterActive(Registered<FilterDescriptor> f, bool on)
    {
        if (on) _pluginState.Active.Add(f.FullId);
        else _pluginState.Active.Remove(f.FullId);
        if (_pluginToggles.TryGetValue(f.FullId, out var btn)) btn.IsChecked = on;
        OnPluginUiChanged();
    }

    private void OnPluginUiChanged()
    {
        UpdatePluginSectionBadges();
        RefreshPointMarkers();
        if (_maskEditFilter is not null) UpdateMaskBar();
        _showHistogram = true;
        HistHost.Visibility = RightPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Hidden;
        UpdateSaveState();
        RequestRender();
        ScheduleHistCommit();
    }

    private Dictionary<string, bool>? _pluginExpandedUser;
    private readonly HashSet<string> _pluginExpandedSession = new(StringComparer.Ordinal);
    private readonly List<(FrameworkElement Badge, List<Registered<FilterDescriptor>> Filters)> _pluginSectionBadges = new();

    private static string SectionName(Registered<FilterDescriptor> f) => f.IsCore ? "Built-in" : f.PluginName;

    private bool IsSectionExpanded(string pluginId, bool active, bool defaultOpen = false)
    {
        _pluginExpandedUser ??= SettingsStore.Load().PluginSectionExpanded is { } m
            ? new Dictionary<string, bool>(m, StringComparer.Ordinal)
            : new Dictionary<string, bool>(StringComparer.Ordinal);
        if (_pluginExpandedUser.TryGetValue(pluginId, out var user)) return user;
        // No explicit choice: open sections that edit this photo, and keep them open for the session.
        if (active || defaultOpen) _pluginExpandedSession.Add(pluginId);
        return _pluginExpandedSession.Contains(pluginId);
    }

    private void SetSectionExpanded(string pluginId, bool expanded)
    {
        _pluginExpandedUser ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        _pluginExpandedUser[pluginId] = expanded;
        if (expanded) _pluginExpandedSession.Add(pluginId);
        else _pluginExpandedSession.Remove(pluginId);
        RefreshPointMarkers();
        try
        {
            SettingsStore.Save(SettingsStore.Load() with { PluginSectionExpanded = new Dictionary<string, bool>(_pluginExpandedUser) });
        }
        catch (Exception ex)
        {
            Debug.Log($"PLUGINS: save section state failed: {ex.Message}");
        }
    }

    /// <summary>Refreshes the "edits this photo" dot of every plugin section (no rebuild).</summary>
    private void UpdatePluginSectionBadges()
    {
        foreach (var (badge, filters) in _pluginSectionBadges)
            badge.Visibility = filters.Any(_pluginState.IsFilterActive) ? Visibility.Visible : Visibility.Hidden;
    }

    private void BuildPluginPanel()
    {
        var muted = (Brush)FindResource("MutedBrush");
        var border = (Brush)FindResource("BorderBrushCol");
        _pluginSectionBadges.Clear();
        var ordered = new List<Registered<FilterDescriptor>>();
        foreach (var stage in new[] { FilterStage.Base, FilterStage.SceneLinear, FilterStage.Display, FilterStage.Output })
            ordered.AddRange(_pluginState.Ordered(_pluginReg, stage)
                .Where(f => f.D.Params.Count > 0 || f.D.Placement == FilterPlacement.Panel));
        if (ordered.Count == 0 && _pluginReg.ListPanels.Count == 0) return;

        PluginPanelHost.Children.Add(new Border
        {
            BorderBrush = border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(0, 6, 0, 0),
            Child = new TextBlock { Text = "PLUGINS", Foreground = muted, FontSize = 11, FontWeight = FontWeights.SemiBold }
        });

        // One collapsible section per plugin (built-in filters first), filters inside in run order.
        var sections = ordered.GroupBy(f => f.PluginId)
            .OrderBy(g => g.First().IsCore ? 0 : 1)
            .ThenBy(g => SectionName(g.First()), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var g in sections)
            PluginPanelHost.Children.Add(BuildPluginSection(g.Key, g.ToList(), muted, border));
        BuildListPanels(muted);
    }

    private FrameworkElement BuildPluginSection(string pluginId, List<Registered<FilterDescriptor>> filters, Brush muted, Brush border)
    {
        var first = filters[0];
        if (filters.Count > 1 && first.D.RepeatGroup is { Length: > 0 } rg && filters.All(f => f.D.RepeatGroup == rg))
            return BuildRepeatSection(pluginId, filters, muted);
        bool single = filters.Count == 1;
        bool active = filters.Any(_pluginState.IsFilterActive);
        bool faulted = first.Owner is { Faulted: true };

        // Right side: per-filter controls when the plugin has one filter, otherwise "reset all".
        var tools = new List<UIElement>();
        if (single)
        {
            tools.AddRange(FilterHeaderTools(first));
        }
        else
        {
            var resetAll = SmallButton("", "Reset all filters of this plugin");
            resetAll.Click += (_, _) =>
            {
                foreach (var f in filters) ResetFilterKeepMask(f);
                DeferRebuild();
                OnPluginUiChanged();
            };
            tools.Add(resetAll);
        }

        string sub = single
            ? (string.Equals(first.D.Label, first.PluginName, StringComparison.CurrentCultureIgnoreCase) ? StageBadge(first) : first.D.Label + StageSuffix(first))
            : $"{filters.Count} filters";

        var body = new StackPanel();
        if (faulted)
        {
            var t = new TextBlock { Text = "Disabled — the plugin failed. Re-enable it in Settings ▸ Plugins.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            body.Children.Add(t);
        }
        foreach (var f in filters)
            body.Children.Add(single ? BuildFilterBody(f, muted) : BuildFilterSection(f, muted));

        var frame = MakeCollapsibleSection(pluginId, "PluginGroup_" + pluginId, SectionName(first), sub, SectionTooltip(first, filters),
            tools, body, active, defaultOpen: false, out var badge, headerAutomationId: "PluginGroupHeader_" + pluginId);
        _pluginSectionBadges.Add((badge, filters));
        return frame;
    }

    // ------------------------------------------------------------------
    //  Repeat groups (several instances of one tool, e.g. heal spots)
    // ------------------------------------------------------------------

    private static string RepeatKey(Registered<FilterDescriptor> f) => f.PluginId + "|" + f.D.RepeatGroup;

    private static string RepeatPlural(string noun, int n) => n == 1 ? noun.ToLowerInvariant() : noun.ToLowerInvariant() + "s";

    /// <summary>All instances of the repeat group of <paramref name="f"/>, in slot order.</summary>
    private List<Registered<FilterDescriptor>> RepeatInstances(Registered<FilterDescriptor> f) =>
        _pluginReg.Filters.Where(o => o.PluginId == f.PluginId && o.D.RepeatGroup == f.D.RepeatGroup)
            .OrderBy(o => o.D.Order).ThenBy(o => o.Index).ToList();

    /// <summary>An instance is in use once one of its points is set (or, without points, any parameter is changed).</summary>
    private bool RepeatUsed(Registered<FilterDescriptor> f) =>
        f.D.Params.Any(d => d.Kind == ParamKind.Point)
            ? f.D.Params.Any(d => d.Kind == ParamKind.Point && FilterParams.ParsePoint(_pluginState.GetText(f, d), out _, out _))
            : _pluginState.IsFilterOn(f) || _pluginState.Active.Contains(f.FullId);

    private Registered<FilterDescriptor>? RepeatSelected(List<Registered<FilterDescriptor>> inst)
    {
        var used = inst.Where(RepeatUsed).ToList();
        if (used.Count == 0) return null;
        if (_repeatSelected.TryGetValue(RepeatKey(inst[0]), out var id) && used.FirstOrDefault(u => u.FullId == id) is { } s) return s;
        return used[^1];
    }

    /// <summary>One section for a repeat group: chips of the used items, Add / Remove, the selected item's controls.</summary>
    private FrameworkElement BuildRepeatSection(string pluginId, List<Registered<FilterDescriptor>> filters, Brush muted)
    {
        var inst = filters.OrderBy(f => f.D.Order).ThenBy(f => f.Index).ToList();
        var first = inst[0];
        string noun = first.D.RepeatGroup;
        string nounL = noun.ToLowerInvariant();
        bool points = first.D.Params.Any(d => d.Kind == ParamKind.Point);
        bool faulted = first.Owner is { Faulted: true };
        var used = inst.Where(RepeatUsed).ToList();
        var sel = RepeatSelected(inst);
        bool active = filters.Any(_pluginState.IsFilterActive);

        var tools = new List<UIElement>();
        var clear = SmallButton("\uE74D", $"Remove all {RepeatPlural(noun, 2)}");
        clear.IsEnabled = used.Count > 0;
        clear.Click += (_, _) =>
        {
            // A removed item takes its mask with it (a later Add must not inherit a stale mask).
            foreach (var f in inst) _pluginState.ResetFilter(f);
            _repeatSelected.Remove(RepeatKey(first));
            DeferRebuild();
            OnPluginUiChanged();
        };
        tools.Add(clear);

        string sub = (used.Count == 0 ? "" : $"{used.Count} {RepeatPlural(noun, used.Count)}") +
                     (StageBadge(first) is { Length: > 0 } b ? (used.Count == 0 ? b : " · " + b) : "");

        var body = new StackPanel { IsEnabled = !faulted };
        AutomationProperties.SetAutomationId(body, "PluginRepeat_" + pluginId);
        if (faulted)
        {
            var t = new TextBlock { Text = "Disabled — the plugin failed. Re-enable it in Settings ▸ Plugins.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            body.Children.Add(t);
        }

        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var u in used)
        {
            int n = inst.IndexOf(u) + 1;
            var chip = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = n.ToString(CultureInfo.InvariantCulture),
                IsChecked = u == sel,
                Width = 26, Height = 22, Margin = new Thickness(0, 0, 4, 4),
                FontWeight = u == sel ? FontWeights.Bold : FontWeights.Normal,
                BorderThickness = new Thickness(u == sel ? 1 : 0),
                ToolTip = points ? $"{noun} {n} — click to edit (or grab its circle on the photo)" : $"{noun} {n} — click to edit its settings and mask"
            };
            chip.SetResourceReference(StyleProperty, "ToolToggle");
            if (u == sel) chip.SetResourceReference(BorderBrushProperty, "AccentBrush");
            AutomationProperties.SetAutomationId(chip, "RepeatItem_" + u.FullId);
            var target = u;
            chip.Click += (_, _) =>
            {
                _repeatSelected[RepeatKey(target)] = target.FullId;
                DeferRebuild();
                RefreshPointMarkers();
            };
            row.Children.Add(chip);
        }
        bool full = used.Count >= inst.Count;
        var add = new Button
        {
            Content = "+ Add " + nounL, Height = 22, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 4, 4),
            IsEnabled = !full,
            ToolTip = full ? $"Limit reached ({inst.Count} {RepeatPlural(noun, inst.Count)}) — remove one first"
                           : points ? $"Add a {nounL}: click it on the photo (Esc cancels). Up to {inst.Count}."
                                    : $"Add a {nounL} with its own settings and mask (starts with the settings of the selected one). Up to {inst.Count}."
        };
        ToolTipService.SetShowOnDisabled(add, true);
        add.SetResourceReference(StyleProperty, "ToolButton");
        AutomationProperties.SetAutomationId(add, "RepeatAdd_" + pluginId);
        add.Click += (_, _) => StartRepeatAdd(inst, sel);
        row.Children.Add(add);
        body.Children.Add(row);

        if (sel is null)
        {
            body.Children.Add(new TextBlock
            {
                Text = points ? $"No {RepeatPlural(noun, 2)} yet. Click Add {nounL}, then click the photo."
                              : $"No {RepeatPlural(noun, 2)} yet. Click Add {nounL}, then set it up and give it a mask (without a mask it covers the whole photo).",
                Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
            });
        }
        else
        {
            int n = inst.IndexOf(sel) + 1;
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 0) };
            var remove = SmallButton("\uE74D", $"Remove {noun.ToLowerInvariant()} {n}");
            AutomationProperties.SetAutomationId(remove, "RepeatRemove_" + pluginId);
            remove.Click += (_, _) =>
            {
                _pluginState.ResetFilter(sel);
                _repeatSelected.Remove(RepeatKey(sel));
                DeferRebuild();
                OnPluginUiChanged();
                SetStatus($"{noun} {n} removed", error: false);
            };
            DockPanel.SetDock(remove, Dock.Right);
            header.Children.Add(remove);
            header.Children.Add(new TextBlock { Text = $"{noun} {n}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(header);
            body.Children.Add(BuildFilterBody(sel, muted));
        }

        var frame = MakeCollapsibleSection(pluginId, "PluginGroup_" + pluginId, SectionName(first), sub, SectionTooltip(first, new List<Registered<FilterDescriptor>> { first }),
            tools, body, active, defaultOpen: false, out var badge, headerAutomationId: "PluginGroupHeader_" + pluginId);
        _pluginSectionBadges.Add((badge, filters));
        return frame;
    }

    /// <summary>Add: pick the first point of a free instance on the photo, then fill in the rest.</summary>
    private void StartRepeatAdd(List<Registered<FilterDescriptor>> inst, Registered<FilterDescriptor>? from)
    {
        var free = inst.FirstOrDefault(f => !RepeatUsed(f));
        if (free is null) return;
        var pd = free.D.Params.FirstOrDefault(d => d.Kind == ParamKind.Point);
        if (pd is null)
        {
            CopyRepeatParams(from, free);
            _pluginState.Active.Add(free.FullId);
            _repeatSelected[RepeatKey(free)] = free.FullId;
            DeferRebuild();
            OnPluginUiChanged();
            return;
        }
        string label = $"{free.D.RepeatGroup} {inst.IndexOf(free) + 1}";
        StartPointPick(free, pd, () =>
        {
            CopyRepeatParams(from, free);
            PlaceLinkedPoints(free, pd);
            _repeatSelected[RepeatKey(free)] = free.FullId;
            bool linked = free.D.Params.Any(d => d.Kind == ParamKind.Point && d.MarkerLink == pd.Id);
            SetStatus(linked ? $"{label} added — drag the dashed circle to a clean area to change the source" : $"{label} added", error: false);
        }, label);
    }

    /// <summary>New item starts with the non-point settings of the selected one (size, feather, mode...).</summary>
    private void CopyRepeatParams(Registered<FilterDescriptor>? from, Registered<FilterDescriptor> to)
    {
        if (from is null || from == to) return;
        foreach (var d in to.D.Params)
        {
            if (d.Kind == ParamKind.Point) continue;
            if (d.IsText) { var t = _pluginState.GetText(from, d); _pluginState.SetText(to, d, t == d.DefaultText ? null : t); }
            else _pluginState.SetValue(to, d, _pluginState.GetValue(from, d));
        }
    }

    /// <summary>Unset points linked to <paramref name="anchor"/> are placed next to it, inside the photo.</summary>
    private void PlaceLinkedPoints(Registered<FilterDescriptor> f, ParamDef anchor)
    {
        if (!FilterParams.ParsePoint(_pluginState.GetText(f, anchor), out var ax, out var ay)) return;
        double w = 1, h = 1;
        if (_baseInfo is ImageInfo bi && bi.Width > 0 && bi.Height > 0) { w = bi.Width; h = bi.Height; }
        double lng = Math.Max(w, h);
        foreach (var d in f.D.Params.Where(d => d.Kind == ParamKind.Point && d.MarkerLink == anchor.Id))
        {
            if (FilterParams.ParsePoint(_pluginState.GetText(f, d), out _, out _)) continue;
            double r = 0.02;
            if (d.MarkerRadius is { Length: > 0 } rid && f.D.Params.FirstOrDefault(q => q.Id == rid && q.Kind == ParamKind.Slider) is { } rd)
                r = Math.Max(0.002, _pluginState.GetValue(f, rd) / 100.0);
            var p = RepeatSourceSpot(ax, ay, r * lng / w, r * lng / h);
            _pluginState.SetText(f, d, Inv(p.X) + "," + Inv(p.Y));
        }
    }

    /// <summary>A spot 2.5 radii away (right, left, below, above, diagonals) that keeps the circle inside the photo.</summary>
    public static Point RepeatSourceSpot(double ax, double ay, double rx, double ry)
    {
        var dirs = new (double X, double Y)[] { (1, 0), (-1, 0), (0, 1), (0, -1), (0.7071, 0.7071), (-0.7071, 0.7071), (0.7071, -0.7071), (-0.7071, -0.7071) };
        foreach (var (dx, dy) in dirs)
        {
            double x = ax + dx * 2.5 * rx, y = ay + dy * 2.5 * ry;
            if (x - rx >= 0 && x + rx <= 1 && y - ry >= 0 && y + ry <= 1) return new Point(x, y);
        }
        // Huge spot: the farthest corner direction, clamped.
        double fx = ax < 0.5 ? 1 : -1, fy = ay < 0.5 ? 1 : -1;
        return new Point(Math.Clamp(ax + fx * 2.5 * rx, 0, 1), Math.Clamp(ay + fy * 2.5 * ry, 0, 1));
    }

    /// <summary>Grabbing a repeat-group marker on the photo selects that item in the panel.</summary>
    private void OnPointMarkerGrabbed(string key)
    {
        int bar = key.LastIndexOf('|');
        if (bar <= 0 || _pluginReg.FindFilter(key[..bar]) is not { } f || f.D.RepeatGroup.Length == 0) return;
        if (_repeatSelected.TryGetValue(RepeatKey(f), out var cur) && cur == f.FullId) return;
        _repeatSelected[RepeatKey(f)] = f.FullId;
        DeferRebuild();
        Dispatcher.BeginInvoke(RefreshPointMarkers, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static string StageSuffix(Registered<FilterDescriptor> f) => StageBadge(f) is { Length: > 0 } b ? " · " + b : "";

    private static string StageBadge(Registered<FilterDescriptor> f) => f.D.Stage switch
    {
        FilterStage.Output => "post-crop",
        FilterStage.SceneLinear => "linear",
        FilterStage.Base => "base",
        _ => ""
    };

    private static string SectionTooltip(Registered<FilterDescriptor> first, List<Registered<FilterDescriptor>> filters)
    {
        var parts = new List<string>();
        if (first.IsCore) parts.Add("Built-in filters of PhotographerEye");
        else
        {
            var o = first.Owner!;
            parts.Add($"Plugin {o.Name}" + (string.IsNullOrEmpty(o.Version) ? "" : $" {o.Version}") + (string.IsNullOrEmpty(o.Author) ? "" : $" by {o.Author}"));
            if (!string.IsNullOrWhiteSpace(o.Description)) parts.Add(o.Description);
            if (o.Faulted) parts.Add("Disabled: " + (o.DisabledReason ?? o.Error ?? "plugin failed"));
        }
        parts.Add("Filters: " + string.Join(", ", filters.Select(f => f.D.Label)));
        parts.Add("Click to expand or collapse");
        return string.Join("\n", parts);
    }

    private void ResetFilterKeepMask(Registered<FilterDescriptor> f)
    {
        var mask = _pluginState.Masks.TryGetValue(f.FullId, out var m) ? m : null;
        _pluginState.ResetFilter(f);
        if (mask is not null) _pluginState.Masks[f.FullId] = mask;
    }

    /// <summary>Enable checkbox (parameterless panel filters), run earlier/later and reset.</summary>
    private List<FrameworkElement> FilterHeaderTools(Registered<FilterDescriptor> f)
    {
        var list = new List<FrameworkElement>();
        if (f.D.Params.Count == 0 && f.D.Placement == FilterPlacement.Panel)
        {
            var cb = new CheckBox { IsChecked = _pluginState.Active.Contains(f.FullId), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Apply " + f.D.Label };
            cb.Click += (_, _) => SetFilterActive(f, cb.IsChecked == true);
            list.Add(cb);
        }
        var up = SmallButton("", "Run earlier (order among all plugin filters of the same stage)");
        up.Click += (_, _) => { if (_pluginState.Move(_pluginReg, f, -1)) { DeferRebuild(); OnPluginUiChanged(); } };
        var down = SmallButton("", "Run later (order among all plugin filters of the same stage)");
        down.Click += (_, _) => { if (_pluginState.Move(_pluginReg, f, +1)) { DeferRebuild(); OnPluginUiChanged(); } };
        var reset = SmallButton("", "Reset " + f.D.Label);
        reset.Click += (_, _) =>
        {
            ResetFilterKeepMask(f);
            DeferRebuild();
            OnPluginUiChanged();
        };
        list.Add(up);
        list.Add(down);
        list.Add(reset);
        return list;
    }

    private static Button SmallButton(string glyph, string tip)
    {
        var b = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 9,
            Width = 18,
            Height = 18,
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            ToolTip = tip
        };
        b.SetResourceReference(StyleProperty, "ToolButton");
        return b;
    }

    /// <summary>A filter inside a multi-filter plugin section: sub-header with its own tools, then its controls.</summary>
    private FrameworkElement BuildFilterSection(Registered<FilterDescriptor> f, Brush muted)
    {
        var section = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(section, "PluginSection_" + f.FullId);

        var header = new DockPanel { LastChildFill = true };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var el in FilterHeaderTools(f)) tools.Children.Add(el);
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = FilterTooltip(f) };
        title.Inlines.Add(new System.Windows.Documents.Run(f.D.Label) { FontWeight = FontWeights.SemiBold });
        if (StageBadge(f) is { Length: > 0 } badge)
            title.Inlines.Add(new System.Windows.Documents.Run("  " + badge) { Foreground = muted, FontSize = 10 });
        header.Children.Add(title);
        section.Children.Add(header);
        section.Children.Add(BuildFilterBody(f, muted));
        return section;
    }

    /// <summary>Parameter controls (and mask controls) of one filter.</summary>
    private FrameworkElement BuildFilterBody(Registered<FilterDescriptor> f, Brush muted)
    {
        var body = new StackPanel { IsEnabled = !IsFaulted(f) };
        AutomationProperties.SetAutomationId(body, "PluginBody_" + f.FullId);
        foreach (var d in f.D.Params)
        {
            try
            {
                var ctl = BuildParamControl(f, d, muted);
                if (!string.IsNullOrWhiteSpace(d.Tooltip)) ctl.ToolTip = d.Tooltip;
                body.Children.Add(ctl);
            }
            catch (Exception ex)
            {
                Debug.Log($"PLUGINS: param {f.FullId}/{d.Id} UI failed: {ex.Message}");
            }
        }
        if (f.D.SupportsMask)
            body.Children.Add(BuildMaskControls(f.FullId, f.D.Label, muted));
        return body;
    }

    private void DeferRebuild() => Dispatcher.BeginInvoke(RebuildPluginUi, System.Windows.Threading.DispatcherPriority.Background);

    private static string FormatParam(double v, ParamDef d)
    {
        try { return v.ToString(string.IsNullOrEmpty(d.Format) ? "0" : d.Format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
    }

    private static string Inv(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static Grid LabelRow(string label, out TextBlock value, Brush muted)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 50, 0) });
        value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Foreground = muted };
        grid.Children.Add(value);
        return grid;
    }

    private FrameworkElement BuildParamControl(Registered<FilterDescriptor> f, ParamDef d, Brush muted)
    {
        string aid = $"PluginParam_{f.FullId}_{d.Id}";
        switch (d.Kind)
        {
            case ParamKind.Toggle:
            {
                var cb = new CheckBox { Content = d.Label, Margin = new Thickness(0, 8, 0, 0), IsChecked = _pluginState.GetValue(f, d) >= 0.5 };
                AutomationProperties.SetAutomationId(cb, aid);
                cb.Click += (_, _) =>
                {
                    _pluginState.SetValue(f, d, cb.IsChecked == true ? 1.0 : 0.0);
                    OnPluginUiChanged();
                };
                return cb;
            }
            case ParamKind.Choice:
            {
                var sp = new StackPanel();
                sp.Children.Add(LabelRow(d.Label, out _, muted));
                var combo = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
                AutomationProperties.SetAutomationId(combo, aid);
                var cur = _pluginState.GetText(f, d);
                foreach (var ch in d.Choices)
                {
                    var item = new ComboBoxItem { Content = ch.Label, Tag = ch.Id };
                    combo.Items.Add(item);
                    if (ch.Id == cur) combo.SelectedItem = item;
                }
                if (combo.SelectedItem is null && combo.Items.Count > 0) combo.SelectedIndex = 0;
                combo.SelectionChanged += (_, _) =>
                {
                    if (_pluginUiBuilding || combo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
                    _pluginState.SetText(f, d, id);
                    if (id == "custom")
                    {
                        var fileDef = f.D.Params.FirstOrDefault(p => p.Kind == ParamKind.File);
                        if (fileDef is not null && string.IsNullOrWhiteSpace(_pluginState.GetText(f, fileDef)))
                        {
                            var pick = BrowseFile(fileDef);
                            if (pick is not null) _pluginState.SetText(f, fileDef, pick);
                            DeferRebuild();
                        }
                    }
                    OnPluginUiChanged();
                };
                sp.Children.Add(combo);
                return sp;
            }
            case ParamKind.Color:
                return BuildColorControl(f, d, muted, aid);
            case ParamKind.Curve:
            {
                var sp = new StackPanel();
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var rb = SmallButton("", "Reset curve");
                DockPanel.SetDock(rb, Dock.Right);
                row.Children.Add(rb);
                row.Children.Add(new TextBlock { Text = d.Label, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(row);
                var ed = new CurveEditor { Margin = new Thickness(0, 4, 0, 0) };
                AutomationProperties.SetAutomationId(ed, aid);
                ed.Value = _pluginState.GetText(f, d);
                ed.CurveChanged += (v, final) =>
                {
                    _pluginState.SetText(f, d, v);
                    OnPluginUiChanged();
                };
                rb.Click += (_, _) =>
                {
                    _pluginState.SetText(f, d, null);
                    ed.Value = d.DefaultText;
                    OnPluginUiChanged();
                };
                sp.Children.Add(ed);
                return sp;
            }
            case ParamKind.Point:
            {
                var sp = new StackPanel();
                sp.Children.Add(LabelRow(d.Label, out var value, muted));
                var cur = _pluginState.GetText(f, d);
                value.Text = FilterParams.ParsePoint(cur, out var px, out var py) ? $"{px:0.00}, {py:0.00}" : "—";
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                var pick = new Button { Content = "Pick on photo", Padding = new Thickness(8, 1, 8, 1), ToolTip = "Click, then click the photo (Esc cancels)" };
                pick.SetResourceReference(StyleProperty, "ToolButton");
                AutomationProperties.SetAutomationId(pick, aid);
                pick.Click += (_, _) => StartPointPick(f, d);
                var rs = SmallButton("", "Reset point");
                rs.Margin = new Thickness(6, 0, 0, 0);
                rs.Click += (_, _) =>
                {
                    _pluginState.SetText(f, d, null);
                    DeferRebuild();
                    OnPluginUiChanged();
                };
                row.Children.Add(pick);
                row.Children.Add(rs);
                sp.Children.Add(row);
                return sp;
            }
            case ParamKind.File:
            {
                var sp = new StackPanel();
                sp.Children.Add(LabelRow(d.Label, out _, muted));
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var browse = new Button { Content = "Browse…", Padding = new Thickness(8, 1, 8, 1) };
                browse.SetResourceReference(StyleProperty, "ToolButton");
                AutomationProperties.SetAutomationId(browse, aid);
                var clear = SmallButton("", "Clear");
                DockPanel.SetDock(clear, Dock.Right);
                DockPanel.SetDock(browse, Dock.Right);
                var cur = _pluginState.GetText(f, d);
                var name = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(cur) ? "(none)" : Path.GetFileName(cur),
                    ToolTip = string.IsNullOrWhiteSpace(cur) ? null : cur,
                    Foreground = muted,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                browse.Click += (_, _) =>
                {
                    var p = BrowseFile(d);
                    if (p is null) return;
                    _pluginState.SetText(f, d, p);
                    DeferRebuild();
                    OnPluginUiChanged();
                };
                clear.Click += (_, _) =>
                {
                    _pluginState.SetText(f, d, null);
                    DeferRebuild();
                    OnPluginUiChanged();
                };
                row.Children.Add(clear);
                row.Children.Add(browse);
                row.Children.Add(name);
                sp.Children.Add(row);
                return sp;
            }
            default:
            {
                var sp = new StackPanel();
                sp.Children.Add(LabelRow(d.Label, out var value, muted));
                double min = Math.Min(d.Min, d.Max), max = Math.Max(d.Min, d.Max);
                var slider = new Slider
                {
                    Minimum = min,
                    Maximum = max,
                    SmallChange = Math.Max(0.001, d.Step),
                    LargeChange = Math.Max(0.01, d.Step * 10),
                    Margin = new Thickness(0, 4, 0, 0),
                    Value = Math.Clamp(_pluginState.GetValue(f, d), min, max)
                };
                PhotoshopSlider.SetEnable(slider, true);
                AutomationProperties.SetAutomationId(slider, aid);
                value.Text = FormatParam(slider.Value, d);
                slider.ValueChanged += (_, e) =>
                {
                    value.Text = FormatParam(e.NewValue, d);
                    if (_pluginUiBuilding) return;
                    _pluginState.SetValue(f, d, e.NewValue);
                    OnPluginUiChanged();
                };
                slider.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount != 2) return;
                    slider.Value = Math.Clamp(d.Default, min, max);
                    e.Handled = true;
                };
                sp.Children.Add(slider);
                return sp;
            }
        }
    }

    private string? BrowseFile(ParamDef d)
    {
        var dlg = new OpenFileDialog { Title = d.Label };
        try { dlg.Filter = string.IsNullOrWhiteSpace(d.FileFilter) ? "All files|*.*" : d.FileFilter; }
        catch (ArgumentException) { dlg.Filter = "All files|*.*"; }
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    // ------------------------------------------------------------------
    //  Color parameter
    // ------------------------------------------------------------------

    private static readonly string[] Swatches =
    {
        "#000000", "#404040", "#808080", "#C0C0C0", "#FFFFFF", "#7A4A2A",
        "#E06030", "#F0A040", "#F0E070", "#60B060", "#40A0A0", "#3070C0",
        "#2040A0", "#7050B0", "#C04080", "#B03030", "#E8C8A0", "#A0C8E8"
    };

    private static Color ParseHex(string hex)
    {
        FilterParams.ParseColor(hex, out var r, out var g, out var b);
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static void RgbToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        l = (mx + mn) / 2;
        double d = mx - mn;
        if (d < 1e-9) { h = 0; s = 0; return; }
        s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
        if (mx == r) h = ((g - b) / d + (g < b ? 6 : 0)) * 60;
        else if (mx == g) h = ((b - r) / d + 2) * 60;
        else h = ((r - g) / d + 4) * 60;
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double C(double t, double p, double q)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        double r, g, b;
        if (s < 1e-9) r = g = b = l;
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            double hh = h / 360.0;
            r = C(hh + 1.0 / 3, p, q);
            g = C(hh, p, q);
            b = C(hh - 1.0 / 3, p, q);
        }
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private FrameworkElement BuildColorControl(Registered<FilterDescriptor> f, ParamDef d, Brush muted, string aid)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var cur = ParseHex(_pluginState.GetText(f, d));
        var hexText = new TextBlock { Text = ToHex(cur), Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var swatch = new Button
        {
            Width = 34,
            Height = 18,
            MinWidth = 0,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(cur),
            BorderBrush = (Brush)FindResource("BorderBrushCol"),
            BorderThickness = new Thickness(1),
            ToolTip = "Pick color"
        };
        swatch.Template = SwatchTemplate();
        AutomationProperties.SetAutomationId(swatch, aid);
        DockPanel.SetDock(hexText, Dock.Right);
        DockPanel.SetDock(swatch, Dock.Right);
        row.Children.Add(hexText);
        row.Children.Add(swatch);
        row.Children.Add(new TextBlock { Text = d.Label, VerticalAlignment = VerticalAlignment.Center });

        void Commit(Color c)
        {
            _pluginState.SetText(f, d, ToHex(c));
            swatch.Background = new SolidColorBrush(c);
            hexText.Text = ToHex(c);
            OnPluginUiChanged();
        }

        swatch.Click += (_, _) => OpenColorPopup(swatch, ParseHex(_pluginState.GetText(f, d)), Commit);
        return row;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        b.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        b.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        t.VisualTree = b;
        return t;
    }

    private void OpenColorPopup(FrameworkElement target, Color start, Action<Color> commit)
    {
        RgbToHsl(start, out var h0, out var s0, out var l0);
        var root = new StackPanel { Margin = new Thickness(10), Width = 220 };
        var wrap = new WrapPanel();
        bool syncing = false;
        Slider hue = null!, sat = null!, lig = null!;
        var hex = new TextBox { Text = ToHex(start), Margin = new Thickness(0, 8, 0, 0) };
        var preview = new Border { Height = 18, Margin = new Thickness(0, 8, 0, 0), Background = new SolidColorBrush(start), CornerRadius = new CornerRadius(2) };

        void Set(Color c, bool fromSliders)
        {
            if (syncing) return;
            syncing = true;
            try
            {
                preview.Background = new SolidColorBrush(c);
                hex.Text = ToHex(c);
                if (!fromSliders)
                {
                    RgbToHsl(c, out var h, out var s, out var l);
                    hue.Value = h;
                    sat.Value = s * 100;
                    lig.Value = l * 100;
                }
            }
            finally
            {
                syncing = false;
            }
            commit(c);
        }

        foreach (var sw in Swatches)
        {
            var c = ParseHex(sw);
            var b = new Button { Width = 22, Height = 18, Margin = new Thickness(0, 0, 3, 3), Background = new SolidColorBrush(c), BorderBrush = Brushes.Black, ToolTip = sw, Template = SwatchTemplate() };
            b.Click += (_, _) => Set(c, false);
            wrap.Children.Add(b);
        }
        root.Children.Add(wrap);

        Slider Mk(string label, double max, double v)
        {
            root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 0) });
            var s = new Slider { Minimum = 0, Maximum = max, Value = v, SmallChange = 1, LargeChange = 10 };
            PhotoshopSlider.SetEnable(s, true);
            root.Children.Add(s);
            return s;
        }
        hue = Mk("Hue", 360, h0);
        sat = Mk("Saturation", 100, s0 * 100);
        lig = Mk("Lightness", 100, l0 * 100);
        RoutedPropertyChangedEventHandler<double> onSlide = (_, _) =>
        {
            if (syncing) return;
            Set(HslToRgb(hue.Value, sat.Value / 100, lig.Value / 100), true);
        };
        hue.ValueChanged += onSlide;
        sat.ValueChanged += onSlide;
        lig.ValueChanged += onSlide;
        hex.LostFocus += (_, _) =>
        {
            if (syncing) return;
            var t = hex.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (t.Length == 7 && int.TryParse(t.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) Set(ParseHex(t), false);
        };
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
        root.Children.Add(hex);
        root.Children.Add(preview);

        var border = new Border { Child = root, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrushCol");
        border.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "ForegroundBrush");
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = border
        };
        popup.IsOpen = true;
    }

    // ------------------------------------------------------------------
    //  Point parameter (click on the photo)
    // ------------------------------------------------------------------

    private void StartPointPick(Registered<FilterDescriptor> f, ParamDef d, Action? done = null, string? label = null)
    {
        if (_baseBitmap is null)
        {
            SetStatus("Open a photo first", error: true);
            return;
        }
        CancelPointPick();
        _pointPick = (f, d);
        _pointPickDone = done;
        PointMarkers.Interactive = false;
        Mouse.OverrideCursor = Cursors.Cross;
        SetStatus($"{label ?? f.D.Label}: click the photo to set \"{d.Label}\" (Esc cancels)", error: false);
    }

    private void CancelPointPick()
    {
        if (_pointPick is null) return;
        _pointPick = null;
        _pointPickDone = null;
        PointMarkers.Interactive = !_wbPicking;
        Mouse.OverrideCursor = null;
    }

    private void HandlePointPick(MouseButtonEventArgs e)
    {
        var (f, d) = _pointPick!.Value;
        var done = _pointPickDone;
        CancelPointPick();
        e.Handled = true;
        var disp = _lastDisplayed ?? _baseBitmap;
        if (disp is null || _baseInfo is not ImageInfo bi || !Viewer.TryScreenToPixel(e.GetPosition(Viewer), out double bx, out double by))
        {
            SetStatus($"{d.Label} — clicked outside the photo", error: true);
            return;
        }
        var fr = new Point(bx / disp.PixelWidth, by / disp.PixelHeight);
        var src = PhotoGeo.MapFromOutput(CurrentDisplayGeo(), bi.Width, bi.Height, fr);
        var val = f.D.Stage == FilterStage.Output ? PhotoGeo.MapToOutput(_geo, bi.Width, bi.Height, src) : src;
        val = new Point(Math.Clamp(val.X, 0, 1), Math.Clamp(val.Y, 0, 1));
        _pluginState.SetText(f, d, Inv(val.X) + "," + Inv(val.Y));
        if (done is not null)
        {
            done();
            RebuildPluginUi();
            OnPluginUiChanged();
            return;
        }
        RebuildPluginUi();
        OnPluginUiChanged();
        SetStatus($"{f.D.Label}: {d.Label} set to {val.X:0.00}, {val.Y:0.00}", error: false);
    }

    // ------------------------------------------------------------------
    //  Point markers (set Point parameters drawn on the photo)
    // ------------------------------------------------------------------

    /// <summary>Open state of a panel section without the side effects of <see cref="IsSectionExpanded"/>.</summary>
    private bool IsSectionOpenNow(string key) =>
        _pluginExpandedUser is not null && _pluginExpandedUser.TryGetValue(key, out var user) ? user : _pluginExpandedSession.Contains(key);

    /// <summary>
    /// Draws the set Point parameters of the filters whose plugin section is open: circles when the parameter names a
    /// radius slider (<see cref="ParamDef.MarkerRadius"/>), dashed with an arrow for a linked point (<see cref="ParamDef.MarkerLink"/>).
    /// </summary>
    private void RefreshPointMarkers()
    {
        RefreshPluginOverlays();
        if (PointMarkers is null) return;
        if (_cropMode || _baseInfo is not ImageInfo bi || _lastDisplayed is null && _baseBitmap is null)
        {
            if (PointMarkers.Visibility == Visibility.Visible) PointMarkers.Visibility = Visibility.Collapsed;
            return;
        }
        var list = new List<PointMarkerOverlay.Marker>();
        var open = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var f in _pluginReg.Filters)
        {
            if (!f.D.Params.Any(d => d.Kind == ParamKind.Point) || f.Owner is { Faulted: true }) continue;
            if (!open.TryGetValue(f.PluginId, out var isOpen)) open[f.PluginId] = isOpen = IsSectionOpenNow(f.PluginId);
            if (!isOpen || !_pluginState.IsFilterOn(f)) continue;
            int nPoints = f.D.Params.Count(d => d.Kind == ParamKind.Point);
            bool many = _pluginReg.Filters.Count(o => o.PluginId == f.PluginId) > 1;
            string? repeatLabel = null;
            bool selected = false;
            if (f.D.RepeatGroup.Length > 0)
            {
                var inst = RepeatInstances(f);
                repeatLabel = $"{f.D.RepeatGroup} {inst.IndexOf(f) + 1}";
                selected = RepeatSelected(inst) == f;
            }
            Point? Src(ParamDef d)
            {
                if (!FilterParams.ParsePoint(_pluginState.GetText(f, d), out var x, out var y)) return null;
                var p = new Point(x, y);
                return f.D.Stage == FilterStage.Output ? PhotoGeo.MapFromOutput(_geo, bi.Width, bi.Height, p) : p;
            }
            foreach (var d in f.D.Params.Where(d => d.Kind == ParamKind.Point))
            {
                if (Src(d) is not { } p) continue;
                double r = 0;
                if (d.MarkerRadius is { Length: > 0 } rid && f.D.Params.FirstOrDefault(q => q.Id == rid && q.Kind == ParamKind.Slider) is { } rd)
                    r = Math.Max(0, _pluginState.GetValue(f, rd)) / 100.0;
                bool linked = d.MarkerLink is { Length: > 0 } lid && f.D.Params.Any(q => q.Id == lid && q.Kind == ParamKind.Point);
                string label = linked ? "" : repeatLabel ?? (many ? f.D.Label : nPoints > 1 ? d.Label : f.D.Label);
                list.Add(new PointMarkerOverlay.Marker(MarkerKey(f, d), p, r, linked, label, linked ? f.FullId + "|" + d.MarkerLink : null, selected));
            }
        }
        if (list.Count == 0)
        {
            if (PointMarkers.Visibility == Visibility.Visible) PointMarkers.Visibility = Visibility.Collapsed;
            PointMarkers.SetMarkers(list);
            return;
        }
        PointMarkers.Visibility = Visibility.Visible;
        PointMarkers.SetMapping(CurrentDisplayGeo(), bi.Width, bi.Height);
        PointMarkers.SetMarkers(list);
    }

    private static string MarkerKey(Registered<FilterDescriptor> f, ParamDef d) => f.FullId + "|" + d.Id;

    /// <summary>A marker dragged on the photo: store the point (live render while dragging, panel text on release).</summary>
    private void OnPointMarkerMoved(string key, Point src, bool final)
    {
        int bar = key.LastIndexOf('|');
        if (bar <= 0 || _baseInfo is not ImageInfo bi || _pluginReg.FindFilter(key[..bar]) is not { } f) return;
        var d = f.D.Params.FirstOrDefault(p => p.Id == key[(bar + 1)..] && p.Kind == ParamKind.Point);
        if (d is null) return;
        var val = f.D.Stage == FilterStage.Output ? PhotoGeo.MapToOutput(_geo, bi.Width, bi.Height, src) : src;
        val = new Point(Math.Clamp(val.X, 0, 1), Math.Clamp(val.Y, 0, 1));
        _pluginState.SetText(f, d, Inv(val.X) + "," + Inv(val.Y));
        if (final)
        {
            DeferRebuild();
            SetStatus($"{f.D.Label}: {d.Label} moved to {val.X:0.00}, {val.Y:0.00}", error: false);
        }
        OnPluginUiChanged();
    }

    // ------------------------------------------------------------------
    //  Masks
    // ------------------------------------------------------------------

    /// <summary>Label and stage of a mask owner: a registered filter or the Adjustments panel.</summary>
    private (string Key, string Label, FilterStage Stage)? MaskTarget(string? key)
    {
        if (key is null) return null;
        if (key == AdjustMask.Key) return (key, AdjustMask.Label, FilterStage.Display);
        var f = _pluginReg.FindFilter(key);
        return f is null ? null : (f.FullId, f.D.Label, f.D.Stage);
    }

    /// <summary>Mask selector at the top of the Adjustments panel (STANDARD / DETAIL sliders and curves).</summary>
    private void BuildAdjustMaskUi()
    {
        if (AdjMaskHost is null) return;
        AdjMaskHost.Children.Clear();
        var muted = (Brush)FindResource("MutedBrush");
        var ui = BuildMaskControls(AdjustMask.Key, AdjustMask.Label, muted);
        ui.Margin = new Thickness(0, 0, 0, 4);
        AdjMaskHost.Children.Add(ui);
    }

    private static bool IsInteractiveMask(string kind) => kind is MaskRender.Linear or MaskRender.Radial or MaskRender.Brush;

    private FrameworkElement BuildMaskControls(string key, string label, Brush muted)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var mask = _pluginState.Masks.TryGetValue(key, out var mm) ? mm : null;
        var row = new DockPanel();
        var combo = new ComboBox { MinWidth = 130 };
        AutomationProperties.SetAutomationId(combo, "PluginMask_" + key);
        combo.ToolTip = key == AdjustMask.Key
            ? "Limits the STANDARD and DETAIL sliders and the R/G/B curves to part of the photo (the red area). Toolbar shadows/gamma, histogram levels and B&W stay global."
            : $"Limits the {label} sliders to part of the photo (the red area). The main Adjustments sliders have their own mask at the top of the panel.";
        DockPanel.SetDock(combo, Dock.Right);
        var items = new List<(string Kind, string Label)>
        {
            ("", "Whole photo"),
            (MaskRender.Linear, "Linear gradient"),
            (MaskRender.Radial, "Radial"),
            (MaskRender.Brush, "Brush"),
            (MaskRender.Luma, "Luminance range")
        };
        items.AddRange(_pluginReg.Masks.Select(m => (MaskRender.PluginPrefix + m.FullId, $"{m.D.Label} ({m.PluginName})")));
        foreach (var (kind, itemLabel) in items)
        {
            var it = new ComboBoxItem { Content = itemLabel, Tag = kind };
            combo.Items.Add(it);
            if ((mask?.Kind ?? "") == kind) combo.SelectedItem = it;
        }
        if (combo.SelectedItem is null)
        {
            var it = new ComboBoxItem { Content = $"{mask?.Kind} (missing)", Tag = mask?.Kind ?? "" };
            combo.Items.Add(it);
            combo.SelectedItem = it;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (_pluginUiBuilding || combo.SelectedItem is not ComboBoxItem { Tag: string kind }) return;
            if (kind.Length == 0)
            {
                _pluginState.Masks.Remove(key);
                if (_maskEditFilter == key) EndMaskEdit();
            }
            else
            {
                _pluginState.Masks[key] = MaskRender.DefaultFor(kind);
                BeginMaskEdit(key);
            }
            DeferRebuild();
            OnPluginUiChanged();
        };
        row.Children.Add(combo);
        row.Children.Add(new TextBlock { Text = key == AdjustMask.Key ? "Mask (all sliders below)" : "Mask", Foreground = muted, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(row);
        if (mask is null) return sp;

        var opts = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var inv = new CheckBox { Content = "Invert", IsChecked = mask.Invert, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        inv.Click += (_, _) =>
        {
            if (_pluginState.Masks.TryGetValue(key, out var m)) _pluginState.Masks[key] = m with { Invert = inv.IsChecked == true };
            SyncMaskOverlaySpec();
            OnPluginUiChanged();
        };
        opts.Children.Add(inv);
        var edit = new ToggleButton
        {
            Content = _maskEditFilter == key ? "Done" : IsInteractiveMask(mask.Kind) ? "Edit on photo" : "Show",
            ToolTip = _maskEditFilter == key ? "Finish editing the mask (Esc)" : "Edit / show the mask on the photo",
            IsChecked = _maskEditFilter == key,
            Padding = new Thickness(8, 1, 8, 1)
        };
        edit.SetResourceReference(StyleProperty, "ToolToggle");
        AutomationProperties.SetAutomationId(edit, "PluginMaskEdit_" + key);
        edit.Click += (_, _) =>
        {
            if (edit.IsChecked == true) BeginMaskEdit(key);
            else EndMaskEdit();
            DeferRebuild();
        };
        opts.Children.Add(edit);
        sp.Children.Add(opts);

        if (mask.Kind == MaskRender.Brush)
        {
            sp.Children.Add(LabelRow("Brush size", out var bv, muted));
            var size = new Slider { Minimum = 0.5, Maximum = 30, Value = Math.Clamp(MaskOverlay.BrushRadius * 100, 0.5, 30), SmallChange = 0.5, LargeChange = 2 };
            PhotoshopSlider.SetEnable(size, true);
            bv.Text = $"{size.Value:0.#} %";
            size.ValueChanged += (_, e) =>
            {
                MaskOverlay.BrushRadius = e.NewValue / 100.0;
                bv.Text = $"{e.NewValue:0.#} %";
            };
            sp.Children.Add(size);
            var brow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var erase = new CheckBox { Content = "Erase (or hold Alt)", IsChecked = MaskOverlay.BrushErase, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            erase.Click += (_, _) => MaskOverlay.BrushErase = erase.IsChecked == true;
            var clear = new Button { Content = "Clear strokes", Padding = new Thickness(8, 1, 8, 1) };
            clear.SetResourceReference(StyleProperty, "ToolButton");
            clear.Click += (_, _) =>
            {
                if (_pluginState.Masks.TryGetValue(key, out var m)) _pluginState.Masks[key] = m with { Strokes = new List<double[]>() };
                SyncMaskOverlaySpec();
                OnPluginUiChanged();
            };
            brow.Children.Add(erase);
            brow.Children.Add(clear);
            sp.Children.Add(brow);
        }
        else if (mask.Kind == MaskRender.Radial)
        {
            sp.Children.Add(LabelRow("Rotation", out var rv, muted));
            var rot = new Slider { Minimum = -90, Maximum = 90, Value = MaskRender.RadialAngle(mask), SmallChange = 1, LargeChange = 15 };
            rot.ToolTip = "Turns the ellipse (or drag the orange handle on the photo; Shift snaps to 15°). Double-click resets.";
            AutomationProperties.SetAutomationId(rot, "PluginMaskRotation_" + key);
            PhotoshopSlider.SetEnable(rot, true);
            rv.Text = $"{rot.Value:0.#}°";
            rot.MouseDoubleClick += (_, _) => rot.Value = 0;
            rot.ValueChanged += (_, e) =>
            {
                rv.Text = $"{e.NewValue:0.#}°";
                if (!_pluginState.Masks.TryGetValue(key, out var m)) return;
                var p = new double[6];
                double[] defs = { 0.5, 0.5, 0.3, 0.3, 0.5, 0 };
                for (int k = 0; k < 6; k++) p[k] = m.P is not null && m.P.Length > k ? m.P[k] : defs[k];
                p[5] = Math.Round(e.NewValue, 2);
                _pluginState.Masks[key] = m with { P = p };
                SyncMaskOverlaySpec();
                OnPluginUiChanged();
            };
            sp.Children.Add(rot);
        }
        else if (mask.Kind == MaskRender.Luma)
        {
            string[] names = { "Lower bound", "Upper bound", "Feather" };
            double[] defs = { 0.6, 1.0, 0.15 };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                double cur = mask.P is not null && mask.P.Length > i ? mask.P[i] : defs[i];
                sp.Children.Add(LabelRow(names[i], out var v, muted));
                var s = new Slider { Minimum = 0, Maximum = i == 2 ? 50 : 100, Value = cur * 100, SmallChange = 1, LargeChange = 10 };
                PhotoshopSlider.SetEnable(s, true);
                v.Text = $"{s.Value:0}";
                s.ValueChanged += (_, e) =>
                {
                    v.Text = $"{e.NewValue:0}";
                    if (!_pluginState.Masks.TryGetValue(key, out var m)) return;
                    var p = new double[3];
                    for (int k = 0; k < 3; k++) p[k] = m.P is not null && m.P.Length > k ? m.P[k] : defs[k];
                    p[idx] = e.NewValue / 100.0;
                    _pluginState.Masks[key] = m with { P = p };
                    OnPluginUiChanged();
                };
                sp.Children.Add(s);
            }
        }
        else if (mask.Kind.StartsWith(MaskRender.PluginPrefix, StringComparison.Ordinal))
        {
            var prov = _pluginReg.FindMask(mask.Kind.Substring(MaskRender.PluginPrefix.Length));
            if (prov is not null)
            {
                foreach (var d in prov.D.Params.Where(p => p.Kind is ParamKind.Slider or ParamKind.Toggle))
                {
                    var pd = d;
                    double cur = mask.Values is not null && mask.Values.TryGetValue(d.Id, out var cv) ? cv : d.Default;
                    if (d.Kind == ParamKind.Toggle)
                    {
                        var cb = new CheckBox { Content = d.Label, IsChecked = cur >= 0.5, Margin = new Thickness(0, 6, 0, 0) };
                        cb.Click += (_, _) => SetMaskValue(key, pd.Id, cb.IsChecked == true ? 1 : 0);
                        sp.Children.Add(cb);
                        continue;
                    }
                    sp.Children.Add(LabelRow(d.Label, out var v, muted));
                    double min = Math.Min(d.Min, d.Max), max = Math.Max(d.Min, d.Max);
                    var s = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(cur, min, max), SmallChange = Math.Max(0.001, d.Step), LargeChange = Math.Max(0.01, d.Step * 10) };
                    PhotoshopSlider.SetEnable(s, true);
                    v.Text = FormatParam(s.Value, d);
                    s.ValueChanged += (_, e) =>
                    {
                        v.Text = FormatParam(e.NewValue, pd);
                        SetMaskValue(key, pd.Id, e.NewValue);
                    };
                    sp.Children.Add(s);
                }
            }
        }
        return sp;
    }

    private void SetMaskValue(string key, string id, double v)
    {
        if (_pluginUiBuilding || !_pluginState.Masks.TryGetValue(key, out var m)) return;
        var vals = m.Values is null ? new Dictionary<string, double>() : new Dictionary<string, double>(m.Values);
        vals[id] = v;
        _pluginState.Masks[key] = m with { Values = vals };
        OnPluginUiChanged();
    }

    private void BeginMaskEdit(string key)
    {
        if (!_pluginState.Masks.TryGetValue(key, out var spec)) return;
        if (_cropMode) SetCropMode(false);
        _maskEditFilter = key;
        MaskOverlay.SetSpec(spec, IsInteractiveMask(spec.Kind));
        RefreshMaskOverlay();
        if (IsInteractiveMask(spec.Kind))
            SetStatus(spec.Kind == MaskRender.Brush
                ? "Mask brush: paint on the photo, Alt erases, wheel zooms"
                : "Mask: drag the handles on the photo", error: false);
    }

    private void EndMaskEdit()
    {
        _maskEditFilter = null;
        Interlocked.Increment(ref _maskPreviewSeq);
        MaskOverlay.Clear();
        MaskOverlay.Visibility = Visibility.Collapsed;
        MaskBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ends mask editing from the on-photo bar, Esc or Enter; the mask itself stays.</summary>
    private void FinishMaskEdit()
    {
        if (_maskEditFilter is null) return;
        EndMaskEdit();
        RebuildPluginUi();
        SetStatus("Mask editing finished", error: false);
    }

    private void OnMaskDoneClick(object sender, RoutedEventArgs e) => FinishMaskEdit();

    private void UpdateMaskBar()
    {
        if (MaskTarget(_maskEditFilter) is not { } t || !_pluginState.Masks.TryGetValue(t.Key, out var spec))
        {
            MaskBar.Visibility = Visibility.Collapsed;
            return;
        }
        string kind = spec.Kind switch
        {
            MaskRender.Linear => "linear gradient — drag the handles",
            MaskRender.Radial => "radial — drag the handles",
            MaskRender.Brush => "brush — paint, Alt erases",
            MaskRender.Luma => "luminance range",
            _ => "preview"
        };
        MaskBarText.Text = $"Mask: {t.Label}, {kind}";
        bool panel = t.Key == AdjustMask.Key;
        bool active = panel || _pluginReg.FindFilter(t.Key) is { } f && _pluginState.IsFilterActive(f);
        MaskBarHint.Text = panel
            ? "The STANDARD / DETAIL sliders and R/G/B curves act only inside the red area"
            : active
                ? $"Only the {t.Label} settings act inside the red area"
                : $"No effect yet: set the {t.Label} sliders (the main Adjustments have their own mask)";
        MaskBarHint.SetResourceReference(TextBlock.ForegroundProperty, active ? "MutedBrush" : "ErrorBrush");
        MaskBar.Visibility = Visibility.Visible;
    }

    private void OnMaskTintClick(object sender, RoutedEventArgs e) => UpdateMaskPreview();

    /// <summary>After undo/redo/.eye load: keep editing when the filter still has a mask.</summary>
    private void SyncMaskOverlaySpec()
    {
        if (_maskEditFilter is null) return;
        if (!_pluginState.Masks.TryGetValue(_maskEditFilter, out var spec) || MaskTarget(_maskEditFilter) is null)
        {
            EndMaskEdit();
            return;
        }
        if (!MaskOverlay.IsInteracting) MaskOverlay.SetSpec(spec, IsInteractiveMask(spec.Kind));
    }

    private void OnMaskSpecChanged(MaskSpec spec, bool final)
    {
        if (_maskEditFilter is null) return;
        _pluginState.Masks[_maskEditFilter] = spec;
        OnPluginUiChanged();
        if (final && spec.Kind == MaskRender.Radial) DeferRebuild(); // rotation slider follows the handle
    }

    private void RefreshMaskOverlay()
    {
        if (_maskEditFilter is null || _cropMode || _baseInfo is not ImageInfo bi)
        {
            if (MaskOverlay.Visibility == Visibility.Visible) MaskOverlay.Visibility = Visibility.Collapsed;
            MaskBar.Visibility = Visibility.Collapsed;
            return;
        }
        MaskOverlay.Visibility = Visibility.Visible;
        UpdateMaskBar();
        MaskOverlay.SetMapping(CurrentDisplayGeo(), bi.Width, bi.Height);
        UpdateMaskPreview();
    }

    private void UpdateMaskPreview()
    {
        var src = _unGeoSrc;
        var target = MaskTarget(_maskEditFilter);
        if (src is null || target is not { } t || !_pluginState.Masks.TryGetValue(t.Key, out var spec) || BtnMaskTint.IsChecked != true)
        {
            Interlocked.Increment(ref _maskPreviewSeq);
            MaskOverlay.SetPreview(null);
            return;
        }
        int seq = Interlocked.Increment(ref _maskPreviewSeq);
        var geo = CurrentDisplayGeo();
        var box = _unGeoBox.Width > 0 ? _unGeoBox : new Size(src.PixelWidth, src.PixelHeight);
        var reg = _pluginReg;
        var specCopy = PluginEditState.CloneMask(spec);
        var stage = t.Stage;
        var meta = CurrentMeta();
        int fullW = _baseInfo?.Width ?? src.PixelWidth, fullH = _baseInfo?.Height ?? src.PixelHeight;
        Task.Run(() =>
        {
            try
            {
                BitmapSource small = src;
                int ls = Math.Max(src.PixelWidth, src.PixelHeight);
                if (ls > 800)
                {
                    double k = 800.0 / ls;
                    var t = new TransformedBitmap(src, new ScaleTransform(k, k));
                    t.Freeze();
                    small = t;
                }
                var img = PluginPipeline.FromBitmap(small, out _, out _);
                bool lin = PluginPipeline.IsLinear(stage);
                if (lin) PluginPipeline.ToLinear(img);
                var ctx = new RenderContext
                {
                    Stage = stage,
                    Space = lin ? PluginColorSpace.LinearSrgb : PluginColorSpace.Srgb,
                    IsPreview = true,
                    Scale = Math.Clamp(img.Width / (double)Math.Max(1, fullW), 1e-4, 1.0),
                    FullWidth = fullW,
                    FullHeight = fullH,
                    Meta = meta
                };
                var m = MaskRender.Render(specCopy, img, ctx, reg);
                if (seq != Volatile.Read(ref _maskPreviewSeq)) return;
                int w = img.Width, h = img.Height;
                var buf = new byte[w * h * 4];
                for (int i = 0, o = 0; i < w * h; i++, o += 4)
                {
                    float a = m is null ? 1f : m[i];
                    buf[o] = 60;
                    buf[o + 1] = 60;
                    buf[o + 2] = 255;
                    buf[o + 3] = (byte)(Math.Clamp(a, 0f, 1f) * 140f);
                }
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
                bmp.Freeze();
                var (gb, _) = PhotoGeo.Apply(bmp, box, geo);
                Dispatcher.BeginInvoke(() =>
                {
                    if (seq == Volatile.Read(ref _maskPreviewSeq) && _maskEditFilter is not null) MaskOverlay.SetPreview(gb);
                });
            }
            catch (Exception ex)
            {
                Debug.Log($"MASK PREVIEW: [{ex.GetType().Name}] {ex.Message}");
            }
        });
    }

    // ------------------------------------------------------------------
    //  Presets
    // ------------------------------------------------------------------

    private void OnPresetsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var save = new MenuItem { Header = "Save current as preset…" };
        save.Click += (_, _) => SaveCurrentAsPreset();
        menu.Items.Add(save);

        List<UserPreset> user, plugin;
        try { user = PresetStore.LoadUser(); }
        catch (Exception) { user = new List<UserPreset>(); }
        try { plugin = PresetStore.FromRegistry(_pluginReg); }
        catch (Exception) { plugin = new List<UserPreset>(); }

        foreach (var g in user.Concat(plugin).GroupBy(p => string.IsNullOrWhiteSpace(p.Group) ? "Presets" : p.Group))
        {
            var gi = new MenuItem { Header = g.Key };
            foreach (var p in g.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var pi = new MenuItem { Header = p.Name, ToolTip = PresetSummary(p) };
                var pp = p;
                pi.Click += (_, _) => ApplyPreset(pp);
                gi.Items.Add(pi);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(gi);
        }
        if (user.Count > 0)
        {
            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = "Delete preset" };
            foreach (var p in user.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var di = new MenuItem { Header = p.Name };
                var pp = p;
                di.Click += (_, _) =>
                {
                    if (MessageBox.Show(this, $"Delete preset \"{pp.Name}\"?", "Presets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    try
                    {
                        PresetStore.Delete(pp);
                        SetStatus($"Preset deleted: {pp.Name}", error: false);
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Delete failed: {ex.Message}", error: true);
                    }
                };
                del.Items.Add(di);
            }
            menu.Items.Add(del);
        }
        menu.PlacementTarget = BtnPresets;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static string PresetSummary(UserPreset p)
    {
        int core = p.Values.Keys.Count(k => k.StartsWith("core.", StringComparison.Ordinal));
        int plug = p.Values.Count - core + p.Text.Count;
        return $"{core} basic settings, {plug} plugin settings" + (p.Active.Count > 0 ? $", {p.Active.Count} filters on" : "");
    }

    private void SaveCurrentAsPreset()
    {
        var name = PromptText("Save preset", "Preset name:", "My preset");
        if (name is null) return;
        var p = new UserPreset { Name = name };
        foreach (var c in CoreUi()) p.Values["core." + c.P.Key] = c.Slider.Value;
        p.Values["core.Mono"] = (int)_monoMode;
        foreach (var kv in _pluginState.Values) p.Values[kv.Key] = kv.Value;
        foreach (var kv in _pluginState.Text) p.Text[kv.Key] = kv.Value;
        p.Active.AddRange(_pluginState.Active.OrderBy(a => a, StringComparer.Ordinal));
        try
        {
            PresetStore.Save(p);
            SetStatus($"Preset saved: {name}", error: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Preset save failed: {ex.Message}", error: true);
        }
    }

    private void ApplyPreset(UserPreset p)
    {
        foreach (var c in CoreUi())
        {
            // "core.ShadowLift" (the field name) is accepted for "core.Shadows".
            if (!p.Values.TryGetValue("core." + c.P.Key, out var v) && !p.Values.TryGetValue("core." + c.P.Field, out v)) continue;
            c.Slider.Value = Math.Clamp(v, c.Slider.Minimum, c.Slider.Maximum);
            if (c.Panel is not null) c.Panel.Value = Math.Clamp(v, c.Panel.Minimum, c.Panel.Maximum);
        }
        if (p.Values.TryGetValue("core.Mono", out var mono))
        {
            _monoMode = (MonoMode)Math.Clamp((int)Math.Round(mono), 0, (int)MonoMode.TriX);
            UpdateMonoButton();
        }

        static string FilterOf(string key)
        {
            int slash = key.IndexOf('/');
            return slash < 0 ? key : key.Substring(0, slash);
        }
        var touched = p.Values.Keys.Where(k => !k.StartsWith("core.", StringComparison.Ordinal))
            .Concat(p.Text.Keys).Select(FilterOf).Concat(p.Active).ToHashSet(StringComparer.Ordinal);
        foreach (var fid in touched)
        {
            var prefix = fid + "/";
            foreach (var k in _pluginState.Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _pluginState.Values.Remove(k);
            foreach (var k in _pluginState.Text.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _pluginState.Text.Remove(k);
            _pluginState.Active.Remove(fid);
        }
        foreach (var kv in p.Values.Where(kv => !kv.Key.StartsWith("core.", StringComparison.Ordinal))) _pluginState.Values[kv.Key] = kv.Value;
        foreach (var kv in p.Text) if (kv.Value is not null) _pluginState.Text[kv.Key] = kv.Value;
        foreach (var a in p.Active) _pluginState.Active.Add(a);

        UpdatePanelLabels();
        RebuildPluginUi();
        OnPluginUiChanged();
        ReapplyAdjustments();
        var miss = _pluginState.MissingPlugins(_pluginReg);
        SetStatus($"Preset applied: {p.Name}" + (miss.Count > 0 ? $" (missing plugins: {string.Join(", ", miss)})" : ""), error: false);
    }

    private string? PromptText(string title, string label, string initial)
    {
        var w = new Window
        {
            Title = title,
            Owner = this,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        w.SetResourceReference(BackgroundProperty, "PanelBrush");
        w.SetResourceReference(ForegroundProperty, "ForegroundBrush");
        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock { Text = label });
        var tb = new TextBox { Text = initial, Width = 280, Margin = new Thickness(0, 6, 0, 0) };
        sp.Children.Add(tb);
        sp.Children.Add(DialogButtons(w));
        w.Content = sp;
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true && !string.IsNullOrWhiteSpace(tb.Text) ? tb.Text.Trim() : null;
    }

    private static StackPanel DialogButtons(Window w)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", MinWidth = 70, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 70, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.SetResourceReference(StyleProperty, "ToolButton");
        cancel.SetResourceReference(StyleProperty, "ToolButton");
        ok.Click += (_, _) => w.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        return buttons;
    }

    // ------------------------------------------------------------------
    //  Export / commands menus
    // ------------------------------------------------------------------

    private void UpdatePluginMenus()
    {
        if (MnuExport is null || MnuCommands is null) return;
        MnuExport.Items.Clear();
        foreach (var exp in _pluginReg.Exporters)
        {
            var it = new MenuItem
            {
                Header = exp.D.Label + (string.IsNullOrEmpty(exp.D.FileFilter) && exp.D.Options.Count == 0 ? "" : "…"),
                ToolTip = "Plugin " + exp.PluginName,
                IsEnabled = exp.Owner is not { Faulted: true }
            };
            AutomationProperties.SetAutomationId(it, "PluginExport_" + exp.FullId);
            var e = exp;
            it.Click += (_, _) => RunExport(e);
            MnuExport.Items.Add(it);
        }
        MnuExport.Visibility = MnuExport.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        MnuCommands.Items.Clear();
        foreach (var c in _pluginReg.Commands.Where(c => c.D.Placement.HasFlag(CommandPlacement.Menu)))
        {
            var it = new MenuItem
            {
                Header = c.D.Label,
                InputGestureText = c.D.Shortcut ?? "",
                ToolTip = "Plugin " + c.PluginName,
                IsEnabled = c.Owner is not { Faulted: true }
            };
            AutomationProperties.SetAutomationId(it, "PluginCmdMenu_" + c.FullId);
            var cc = c;
            it.Click += (_, _) => RunCommand(cc);
            MnuCommands.Items.Add(it);
        }
        MnuCommands.Visibility = MnuCommands.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        BuildOverlayMenu();
        BuildToolMenu();
        if (MnuPluginSettings is null) return;
        MnuPluginSettings.Items.Clear();
        foreach (var st in _pluginReg.Settings)
        {
            var tip = string.IsNullOrWhiteSpace(st.D.D.Description) ? "Plugin " + st.PluginName : st.D.D.Description + "\nPlugin " + st.PluginName;
            var group = new MenuItem { Header = st.D.D.Label, ToolTip = tip, IsEnabled = st.Owner is not { Faulted: true } };
            AutomationProperties.SetAutomationId(group, "PluginSetting_" + st.FullId);
            foreach (var c in st.D.D.Choices)
            {
                var it = new MenuItem { Header = c.Label, IsCheckable = true, IsChecked = st.D.Value == c.Id };
                var (ss, id) = (st, c.Id);
                it.Click += (_, _) => ChangePluginSetting(ss, id);
                group.Items.Add(it);
            }
            MnuPluginSettings.Items.Add(group);
        }
        MnuPluginSettings.Visibility = MnuPluginSettings.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Stores a plugin setting, tells the plugin, then rebuilds the panels and re-renders (stages may have moved).</summary>
    private void ChangePluginSetting(Registered<PluginSettingHandle> st, string id)
    {
        if (!st.D.Set(id))
        {
            UpdatePluginMenus();
            return;
        }
        try
        {
            st.D.D.Changed?.Invoke(id);
            PluginManager.ReportSuccess(st.Owner);
        }
        catch (Exception ex)
        {
            PluginManager.Instance.ReportFailure(st.Owner, $"setting {st.FullId}", ex);
            SetStatus($"{st.D.D.Label}: {ex.Message}", error: true);
        }
        _stageCache.Clear();
        Adjust16.ClearStageCaches();
        UpdatePluginMenus();
        RebuildPluginUi();
        UpdatePluginSectionBadges();
        RefreshPointMarkers();
        RequestRender();
        var label = st.D.D.Choices.FirstOrDefault(c => c.Id == id)?.Label ?? id;
        SetStatus($"{st.D.D.Label}: {label}", error: false);
    }

    private void BuildCommandShortcuts()
    {
        _pluginShortcuts.Clear();
        var conv = new KeyGestureConverter();
        foreach (var c in _pluginReg.Commands)
        {
            if (string.IsNullOrWhiteSpace(c.D.Shortcut)) continue;
            if (ReservedShortcuts.Any(r => string.Equals(r, c.D.Shortcut.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)))
            {
                Debug.Log($"PLUGINS: shortcut {c.D.Shortcut} of {c.FullId} is reserved");
                continue;
            }
            try
            {
                if (conv.ConvertFromInvariantString(c.D.Shortcut) is KeyGesture g) _pluginShortcuts.Add((g, c));
            }
            catch (Exception ex)
            {
                Debug.Log($"PLUGINS: bad shortcut '{c.D.Shortcut}' of {c.FullId}: {ex.Message}");
            }
        }
        BuildToolShortcuts();
    }

    /// <summary>Esc for point picking and plugin command shortcuts. Returns true when handled.</summary>
    private bool HandlePluginKey(KeyEventArgs e)
    {
        if (Tools.Pressed && e.Key == Key.Escape) return HandleToolKey(e, escOnly: true);
        if (e.Key == Key.Escape && _pointPick is null && _maskEditFilter is null && CancelBackgroundTasks())
        {
            e.Handled = true;
            return true;
        }
        if (_pointPick is not null && e.Key == Key.Escape)
        {
            CancelPointPick();
            SetStatus("Point pick cancelled", error: false);
            e.Handled = true;
            return true;
        }
        if (_maskEditFilter is not null && (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.FocusedElement is not TextBoxBase)))
        {
            FinishMaskEdit();
            e.Handled = true;
            return true;
        }
        if (_pointPick is null && _maskEditFilter is null && HandleToolKey(e, escOnly: false)) return true;
        if (_pluginShortcuts.Count == 0 || Keyboard.FocusedElement is TextBoxBase) return false;
        foreach (var (g, c) in _pluginShortcuts)
        {
            if (!g.Matches(null, e)) continue;
            RunCommand(c);
            e.Handled = true;
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    //  Export
    // ------------------------------------------------------------------

    private void RunExport(Registered<ExporterDescriptor> exp)
    {
        string? path = _currentPath;
        if (path is null || _baseInfo is null)
        {
            SetStatus("Open a photo first", error: true);
            return;
        }
        if (_saving) return;

        FilterParams opts;
        if (exp.D.Options.Count > 0)
        {
            var r = ShowExportOptions(exp);
            if (r is null) return;
            opts = r;
        }
        else
        {
            opts = new FilterParams(Array.Empty<ParamDef>(), new Dictionary<string, double>(), new Dictionary<string, string>());
        }

        string? dest = null;
        if (!string.IsNullOrEmpty(exp.D.FileFilter))
        {
            var dlg = new SaveFileDialog
            {
                InitialDirectory = Path.GetDirectoryName(path),
                FileName = Path.GetFileNameWithoutExtension(path) + (exp.D.DefaultSuffix ?? ""),
                Title = exp.D.Label
            };
            try { dlg.Filter = exp.D.FileFilter; }
            catch (ArgumentException) { dlg.Filter = "All files|*.*"; }
            if (dlg.ShowDialog(this) != true) return;
            dest = dlg.FileName;
        }

        string? cancel = null;
        try { cancel = PluginManager.Instance.RaiseBeforeSave(path, dest ?? "", exp.FullId); }
        catch (Exception ex) { Debug.Log($"PLUGINS: BeforeSave [{ex.GetType().Name}] {ex.Message}"); }
        if (cancel is not null)
        {
            SetStatus($"Export cancelled — {cancel}", error: true);
            return;
        }

        CommitHistNow();
        var snap = CaptureFullSnapshot(path);
        _saving = true;
        BtnSave.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        SetStatus($"Exporting: {exp.D.Label}…", error: false);
        _ = Task.Run(() =>
        {
            bool ok = false;
            string msg;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var res = RenderFull(snap);
                var img = res.R16 is not null ? PluginPipeline.FromRaw16(res.R16) : PluginPipeline.FromBitmap(res.Bmp!, out _, out _);
                var req = new ExportRequest
                {
                    Image = img,
                    SourcePath = path,
                    DestinationPath = dest,
                    Options = opts,
                    Meta = snap.Job.Meta,
                    Cancel = CancellationToken.None
                };
                try
                {
                    exp.D.Export(req);
                    PluginManager.ReportSuccess(exp.Owner);
                    ok = true;
                    msg = string.IsNullOrWhiteSpace(req.ResultMessage)
                        ? $"Exported ({exp.D.Label}): {(dest is not null ? Path.GetFileName(dest) : img.Width + "×" + img.Height)}"
                        : req.ResultMessage;
                }
                catch (Exception ex)
                {
                    PluginManager.Instance.ReportFailure(exp.Owner, $"export {exp.FullId}", ex);
                    msg = $"Export failed ({exp.PluginName}): {ex.Message}";
                }
                Debug.Log($"EXPORT: {exp.FullId} ok={ok} {sw.ElapsedMilliseconds} ms {img.Width}x{img.Height} dest={dest}");
                if (snap.Job.Report.Errors.Count > 0) msg += $"  (plugin errors: {string.Join("; ", snap.Job.Report.Errors.Distinct())})";
            }
            catch (Exception ex)
            {
                Debug.Log($"EXPORT ERROR: [{ex.GetType().Name}] {ex.Message}\n{ex.StackTrace}");
                msg = $"Export error: {ex.Message}";
            }
            try { PluginManager.Instance.RaiseAfterSave(path, dest ?? "", exp.FullId, ok, export: true); }
            catch (Exception ex) { Debug.Log($"PLUGINS: AfterExport [{ex.GetType().Name}] {ex.Message}"); }
            Dispatcher.BeginInvoke(() =>
            {
                _saving = false;
                Mouse.OverrideCursor = null;
                UpdateSaveState();
                SetStatus(msg, error: !ok);
            });
        });
    }

    private FilterParams? ShowExportOptions(Registered<ExporterDescriptor> exp) =>
        ShowParamsDialog(exp.D.Label, exp.D.Options, exp.FullId);
}
