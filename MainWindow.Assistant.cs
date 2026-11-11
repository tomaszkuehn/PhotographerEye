using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;
using ImgViewer.Services.Ai;

namespace ImgViewer;

/// <summary>AI assistant (Ollama): the window, the editor the model works through, render waiting, one undo step per turn.</summary>
public partial class MainWindow
{
    private AssistantWindow? _assistant;

    /// <summary>
    /// Assistant conversations per photo path. Kept here, not in the window, so closing and reopening the chat window
    /// (or browsing to another photo and back) continues the same conversation; they last until the app closes.
    /// </summary>
    internal readonly Dictionary<string, AiSession> AiSessions = new(StringComparer.OrdinalIgnoreCase);

    private void OnAssistantClick(object sender, RoutedEventArgs e)
    {
        if (_assistant is not null)
        {
            if (_assistant.WindowState == WindowState.Minimized) _assistant.WindowState = WindowState.Normal;
            _assistant.Activate();
            return;
        }
        _assistant = new AssistantWindow(this) { Owner = this };
        _assistant.Closed += (_, _) => _assistant = null;
        _assistant.Show();
    }

    /// <summary>Path of the photo shown (null: none).</summary>
    internal string? AiPhotoPath => _currentPath;

    /// <summary>Called when another photo is about to be shown: a running turn stops (it must not edit the next photo).</summary>
    private void AssistantPhotoSwitching() => _assistant?.CancelTurn();

    private void AssistantPhotoChanged() => _assistant?.OnPhotoChanged(_currentPath);

    /// <summary>Earlier edits become their own undo step, so the assistant's changes form one step of their own.</summary>
    internal void AiBeginTurn()
    {
        // the assistant changes the photo as a whole: crop mode, a mask on the photo and the sliders showing a mask's
        // values are closed first, so nothing on screen holds a state the turn replaces
        if (_cropMode) SetCropMode(false);
        if (_maskEditFilter is not null)
        {
            EndMaskEdit();
            RebuildPluginUi();
        }
        else if (AdjEditMask)
        {
            ShowAdjSet(false);
            RebuildPluginUi();
        }
        CommitHistNow();
    }

    /// <summary>Records the assistant's changes as one labelled undo step (only while the same photo is shown).</summary>
    internal void AiEndTurn(string path, string label)
    {
        if (!string.Equals(path, _currentPath, StringComparison.Ordinal)) return;
        CommitStep(label); // no step when nothing changed
        AiConversationChanged(); // a turn without edits still belongs in the .eye file
    }

    /// <summary>The conversation of the shown photo changed (a turn ended, a new one was started): the .eye file follows.</summary>
    internal void AiConversationChanged()
    {
        if (_currentPath is null || !_eyeAutoSave.Enabled) return;
        SyncSessionHist();
    }

    /// <summary>The shown photo's conversation added to a state written to its .eye file.</summary>
    private EyeState WithAssistant(EyeState s) =>
        _currentPath is not null && AiSessions.TryGetValue(_currentPath, out var a) && a.Export() is { } saved
            ? s with { Assistant = saved }
            : s with { Assistant = null };

    private bool HasAssistantConversation =>
        _currentPath is not null && AiSessions.TryGetValue(_currentPath, out var a) && a.Transcript.Count > 0;

    /// <summary>
    /// A photo's .eye file was read: its stored conversation continues (unless one is already open in this run). The
    /// returned state has no conversation — it goes into the history, which must not carry it.
    /// </summary>
    private EyeState TakeAssistant(string path, EyeState st)
    {
        if (st.Assistant is null) return st;
        if (!AiSessions.ContainsKey(path))
        {
            try
            {
                AiSessions[path] = AiSession.Import(st.Assistant);
                _assistant?.OnPhotoChanged(path); // the window switched before the file was read
            }
            catch (Exception ex)
            {
                Debug.Log($"AI: stored conversation of {Path.GetFileName(path)} not restored: {ex.Message}");
            }
        }
        return st with { Assistant = null };
    }

    internal IAiEditor AiEditorFor(string path, bool sendMetadata) => new AiEditor(this, path, sendMetadata);

    /// <summary>True once the shown photo reflects the current settings (nothing rendering, queued or throttled).</summary>
    private bool RenderIdle => !_renderBusy && !_renderQueued && !_renderDirty && !_adjustTimer.IsEnabled && _lastDisplayed is not null;

    private sealed class AiEditor : IAiEditor
    {
        private readonly MainWindow _w;
        private readonly string _path;
        private readonly bool _meta;

        public AiEditor(MainWindow w, string path, bool sendMetadata)
        {
            _w = w;
            _path = path;
            _meta = sendMetadata;
        }

        private void EnsurePhoto()
        {
            if (!string.Equals(_w._currentPath, _path, StringComparison.Ordinal))
                throw new InvalidOperationException("the photo was changed; the turn stops");
        }

        public IReadOnlyDictionary<string, double> GetCore()
        {
            EnsurePhoto();
            var st = _w.CaptureEyeState();
            return CoreParams.All.ToDictionary(p => p.Key, p => p.Get(st), StringComparer.OrdinalIgnoreCase);
        }

        public void SetCore(IReadOnlyDictionary<string, double> values)
        {
            EnsurePhoto();
            // the plugin command context: same validation and state handling as a plugin's SetCore, applied for preview
            // without an undo step (the turn records one at its end)
            var ctx = new CommandContext(_w, "ai", null, null);
            foreach (var kv in values) ctx.SetCore(kv.Key, kv.Value);
            ctx.Apply();
            _w.UpdateSaveState();
        }

        public async Task WaitRenderedAsync(CancellationToken ct)
        {
            EnsurePhoto();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int calm = 0;
            // let queued input-priority work (RequestRender) run first
            await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            while (sw.ElapsedMilliseconds < 20000)
            {
                ct.ThrowIfCancellationRequested();
                calm = _w.RenderIdle ? calm + 1 : 0;
                if (calm >= 3) return;
                await Task.Delay(60, ct);
            }
            Debug.Log("AI: render wait timed out");
        }

        public ImageStats? Analyze()
        {
            EnsurePhoto();
            var px = _w.RenderedPixels(768);
            return px?.Image is { } img ? ImageProbe.Analyze(img) : null;
        }

        public string? PreviewJpegBase64(int maxLongSide)
        {
            EnsurePhoto();
            if (_w._lastDisplayed is not BitmapSource bmp) return null;
            var small = Shrink(bmp, maxLongSide);
            var bgr = new FormatConvertedBitmap(small, PixelFormats.Bgr24, null, 0);
            bgr.Freeze();
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bgr));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }

        public JsonObject PhotoInfo()
        {
            EnsurePhoto();
            var m = _w.CurrentMeta();
            var o = new JsonObject
            {
                ["type"] = Path.GetExtension(_path).TrimStart('.').ToUpperInvariant(),
                ["raw"] = m.IsRaw,
                ["width"] = m.Width,
                ["height"] = m.Height,
                ["orientation"] = m.Width >= m.Height ? "landscape" : "portrait"
            };
            if (_meta)
            {
                if (!string.IsNullOrWhiteSpace(m.Camera)) o["camera"] = m.Camera;
                if (!string.IsNullOrWhiteSpace(m.Date)) o["date"] = m.Date;
                if (m.Iso is { } iso) o["iso"] = iso;
                if (m.Aperture is { } f) o["aperture"] = f;
                if (m.ShutterSeconds is { } t) o["shutter_s"] = t;
                if (m.FocalLength is { } fl) o["focal_mm"] = fl;
            }
            return o;
        }

        // ---------------- stage 2: plugin filters and presets ----------------

        private void Applied(CommandContext ctx)
        {
            ctx.Apply();
            _w.UpdateSaveState();
        }

        private Registered<FilterDescriptor> Filter(string id) =>
            _w._pluginReg.FindFilter(id) is { } f && !IsFaulted(f) ? f : throw new ArgumentException($"unknown filter '{id}'");

        private static string? KindOf(ParamDef d) => d.Kind switch
        {
            ParamKind.Slider => "slider",
            ParamKind.Toggle => "toggle",
            ParamKind.Choice => "choice",
            ParamKind.Color => "color",
            _ => null // curves, points and files are left to the user
        };

        private static bool ToolbarLike(Registered<FilterDescriptor> f) => f.D.Placement == FilterPlacement.Toolbar || f.D.Params.Count == 0;

        /// <summary>The key of the mask the filter works in: a mask it follows, its own shape (its own id), or null (whole photo).</summary>
        private string? MaskOf(Registered<FilterDescriptor> f)
        {
            if (!f.D.SupportsMask || !_w._pluginState.Masks.TryGetValue(f.FullId, out var m) || string.IsNullOrEmpty(m.Kind)) return null;
            return m.Kind == MaskRender.Link ? m.Ref : f.FullId;
        }

        public IReadOnlyList<AiFilter> Filters()
        {
            EnsurePhoto();
            var st = _w._pluginState;
            var list = new List<AiFilter>();
            foreach (var f in _w._pluginReg.Filters)
            {
                if (IsFaulted(f)) continue;
                var ps = new List<AiParam>();
                foreach (var d in f.D.Params)
                {
                    if (KindOf(d) is not { } kind) continue;
                    ps.Add(d.IsText
                        ? new AiParam(d.Id, d.Label, kind, 0, 0, 0, 0, st.GetText(f, d), d.Kind == ParamKind.Choice ? d.Choices.Select(c => c.Id).ToList() : null)
                        : new AiParam(d.Id, d.Label, kind, d.Min, d.Max, d.Default, st.GetValue(f, d)));
                }
                bool toolbar = ToolbarLike(f);
                if (ps.Count == 0 && !toolbar) continue; // nothing the assistant could set
                list.Add(new AiFilter(f.FullId, f.D.Label, f.D.Group, f.D.Description ?? "", toolbar, f.D.SupportsMask,
                    st.IsFilterOn(f), MaskOf(f), ps));
            }
            return list;
        }

        public void SetFilter(string id, IReadOnlyDictionary<string, double> values, IReadOnlyDictionary<string, string> text)
        {
            EnsurePhoto();
            var f = Filter(id);
            var ctx = new CommandContext(_w, "ai", null, null);
            foreach (var kv in values) ctx.SetValue(f.FullId, kv.Key, kv.Value);
            foreach (var kv in text) ctx.SetText(f.FullId, kv.Key, kv.Value);
            Applied(ctx);
        }

        public void SetFilterActive(string id, bool active)
        {
            EnsurePhoto();
            var f = Filter(id);
            var ctx = new CommandContext(_w, "ai", null, null);
            ctx.SetActive(f.FullId, active);
            Applied(ctx);
        }

        public void ResetFilter(string id)
        {
            EnsurePhoto();
            var f = Filter(id);
            _w._pluginState.ResetFilter(f, keepMask: true);
            Applied(new CommandContext(_w, "ai", null, null) { Dirty = true });
        }

        private List<(AiPreset Ai, UserPreset P)> AllPresets()
        {
            var list = new List<(AiPreset, UserPreset)>();
            foreach (var p in PresetStore.LoadUser())
                list.Add((new AiPreset("user:" + p.Name, p.Name, p.Group), p));
            var reg = _w._pluginReg;
            var fromReg = PresetStore.FromRegistry(reg);
            for (int i = 0; i < reg.Presets.Count && i < fromReg.Count; i++)
            {
                var r = reg.Presets[i];
                if (r.Owner is { Faulted: true }) continue;
                list.Add((new AiPreset(r.PluginId + ":" + r.D.Id, fromReg[i].Name, fromReg[i].Group), fromReg[i]));
            }
            return list;
        }

        public IReadOnlyList<AiPreset> Presets()
        {
            EnsurePhoto();
            return AllPresets().Select(x => x.Ai).ToList();
        }

        public void ApplyPreset(string id)
        {
            EnsurePhoto();
            var hit = AllPresets().FirstOrDefault(x => string.Equals(x.Ai.Id, id, StringComparison.OrdinalIgnoreCase));
            if (hit.P is null) throw new ArgumentException($"unknown preset '{id}'");
            _w._histSuppress = true; // the turn records one step at its end
            try
            {
                _w.ApplyPreset(hit.P);
            }
            finally
            {
                _w._histSuppress = false;
            }
            _w._histCommitTimer?.Stop();
            _w.UpdateSaveState();
        }

        // ---------------- stage 3: crop and straighten ----------------

        public AiGeometry GetGeometry()
        {
            EnsurePhoto();
            var g = new CommandContext(_w, "ai", null, null).GetGeometry();
            return new AiGeometry(g.Angle, g.CropX, g.CropY, g.CropW, g.CropH, g.Aspect ?? "original", _w.IntrinsicFrameAspect());
        }

        public void SetGeometry(AiGeometry g)
        {
            EnsurePhoto();
            var ctx = new CommandContext(_w, "ai", null, null);
            var cur = ctx.GetGeometry();
            ctx.SetGeometry(new PhotoGeometry
            {
                Rot90 = cur.Rot90, Angle = g.Angle, CropX = g.CropX, CropY = g.CropY, CropW = g.CropW, CropH = g.CropH,
                Aspect = PluginEditOps.CropAspects.Contains(g.Aspect) ? g.Aspect : "original"
            });
            Applied(ctx);
            _w.RefreshDimsStatus();
            _w.RefreshMaskPins();
        }

        // ---------------- stage 4: local masks ----------------

        private (double W, double H) Src() =>
            _w._baseInfo is ImageInfo i && i.Width > 0 && i.Height > 0 ? (i.Width, i.Height) : (1.0, 1.0);

        public (double X, double Y) ViewToPhoto(double x, double y)
        {
            EnsurePhoto();
            var (w, h) = Src();
            var p = PhotoGeo.MapFromOutput(_w._geo, w, h, new Point(x, y));
            return (p.X, p.Y);
        }

        public (double X, double Y) PhotoToView(double x, double y)
        {
            EnsurePhoto();
            var (w, h) = Src();
            var p = PhotoGeo.MapToOutput(_w._geo, w, h, new Point(x, y));
            return (p.X, p.Y);
        }

        public (int W, int H) PhotoPixelSize()
        {
            EnsurePhoto();
            var (w, h) = Src();
            return ((int)w, (int)h);
        }

        public IReadOnlyList<AiMask> Masks()
        {
            EnsurePhoto();
            var st = _w._pluginState;
            var list = new List<AiMask>();
            foreach (var kv in st.Masks.OrderBy(kv => PluginEditState.IsLibraryKey(kv.Key) ? 0 : 1)
                         .ThenBy(kv => PluginEditState.IsLibraryKey(kv.Key) ? PluginEditState.LibraryIndex(kv.Key) : 0)
                         .ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var m = kv.Value;
                if (m is null || string.IsNullOrEmpty(m.Kind) || m.Kind == MaskRender.Link) continue;
                var users = st.LinksTo(kv.Key);
                if (!PluginEditState.IsLibraryKey(kv.Key)) users.Insert(0, kv.Key); // a tool's own mask: the tool uses it
                list.Add(new AiMask(kv.Key, m.Kind, m.P?.ToArray() ?? Array.Empty<double>(), m.Invert,
                    m.Adjust is { Count: > 0 } a ? new Dictionary<string, double>(a) : null, users));
            }
            return list;
        }

        /// <summary>The sliders and the on-photo mask editor must not hold a mask the assistant changes.</summary>
        private void Release(string key)
        {
            if (_w.AdjEditMask && _w._adjMaskKey == key) _w.ShowAdjSet(false);
            if (_w._maskEditFilter == key) _w.EndMaskEdit();
        }

        private void MasksChanged()
        {
            _w.RebuildPluginUi();
            _w.OnPluginUiChanged();
            _w.RefreshMaskPins();
            _w._histCommitTimer?.Stop();
            _w.UpdateSaveState();
        }

        private static MaskSpec Spec(string kind, double[] p, bool invert) => kind switch
        {
            "radial" => new MaskSpec { Kind = MaskRender.Radial, P = p, Invert = invert },
            "linear" => new MaskSpec { Kind = MaskRender.Linear, P = p, Invert = invert },
            "luma" => new MaskSpec { Kind = MaskRender.Luma, P = p, Invert = invert },
            _ => throw new ArgumentException($"unknown mask kind '{kind}'")
        };

        public string CreateMask(string kind, double[] p, bool invert)
        {
            EnsurePhoto();
            var key = _w._pluginState.NewLibraryMaskKey();
            _w._pluginState.Masks[key] = Spec(kind, p, invert);
            MasksChanged();
            return key;
        }

        public void UpdateMask(string key, string kind, double[] p, bool invert)
        {
            EnsurePhoto();
            var st = _w._pluginState;
            if (!st.Masks.TryGetValue(key, out var old) || old.Kind == MaskRender.Link) throw new ArgumentException($"unknown mask '{key}'");
            Release(key);
            // the shape changes; its own values and the tools that follow it stay
            st.Masks[key] = Spec(kind, p, invert) with { Adjust = PluginEditState.CopyAdjust(old.Adjust), Plugin = old.Plugin };
            MasksChanged();
        }

        public void DeleteMask(string key)
        {
            EnsurePhoto();
            var st = _w._pluginState;
            if (!st.Masks.ContainsKey(key)) throw new ArgumentException($"unknown mask '{key}'");
            Release(key);
            // the assistant's meaning: tools limited to it work on the whole photo again (no hidden copies of the shape)
            foreach (var l in st.LinksTo(key)) st.Masks.Remove(l);
            _w.DeleteMask(key);
            _w._histCommitTimer?.Stop();
            _w.UpdateSaveState();
        }

        public void SetMaskValues(string key, IReadOnlyDictionary<string, double> values)
        {
            EnsurePhoto();
            var st = _w._pluginState;
            if (!st.Masks.TryGetValue(key, out var m) || m.Kind == MaskRender.Link) throw new ArgumentException($"unknown mask '{key}'");
            Release(key);
            var d = values.Where(kv => Math.Abs(kv.Value) > 1e-9).ToDictionary(kv => kv.Key, kv => kv.Value);
            st.Masks[key] = m with { Adjust = d.Count > 0 ? d : null };
            MasksChanged();
            _w.ReapplyAdjustments();
        }

        public void SetToolMask(string filterId, string? maskKey)
        {
            EnsurePhoto();
            var f = Filter(filterId);
            if (!f.D.SupportsMask) throw new ArgumentException($"'{f.FullId}' cannot be limited to a mask");
            Release(f.FullId);
            if (maskKey is null)
            {
                if (_w._pluginState.Masks.ContainsKey(f.FullId)) _w.RemoveToolMask(f.FullId);
            }
            else
            {
                if (!_w._pluginState.Masks.TryGetValue(maskKey, out var t) || string.IsNullOrEmpty(t.Kind) || t.Kind == MaskRender.Link)
                    throw new ArgumentException($"unknown mask '{maskKey}'");
                _w.LinkMask(f.FullId, maskKey);
            }
            _w._histCommitTimer?.Stop();
            _w.UpdateSaveState();
        }

        // ---------------- stage 5: curves and levels ----------------

        /// <summary>The master curve is the histogram stretch in display fractions; the rest are the channel curves and levels.</summary>
        private static CurveKind CurveKindOf(string id) => id switch
        {
            "rgb" => CurveKind.Stretch,
            "red" => CurveKind.Red,
            "green" => CurveKind.Green,
            "blue" => CurveKind.Blue,
            "levels_red" => CurveKind.LevelsRed,
            "levels_green" => CurveKind.LevelsGreen,
            "levels_blue" => CurveKind.LevelsBlue,
            _ => throw new ArgumentException($"unknown curve '{id}'")
        };

        public double[][] GetCurve(string id)
        {
            EnsurePhoto();
            return new CommandContext(_w, "ai", null, null).GetCurve(CurveKindOf(id));
        }

        public void SetCurve(string id, double[][] points)
        {
            EnsurePhoto();
            var ctx = new CommandContext(_w, "ai", null, null);
            ctx.SetCurve(CurveKindOf(id), points);
            Applied(ctx);
        }
    }
}
