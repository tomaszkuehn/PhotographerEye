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
using ImgViewer.Services;
using ImgViewer.Services.Ai;

namespace ImgViewer;

/// <summary>AI assistant (Ollama): the window, the editor the model works through, render waiting, one undo step per turn.</summary>
public partial class MainWindow
{
    private AssistantWindow? _assistant;

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
    internal void AiBeginTurn() => CommitHistNow();

    /// <summary>Records the assistant's changes as one labelled undo step (only while the same photo is shown).</summary>
    internal void AiEndTurn(string path, string label)
    {
        if (!string.Equals(path, _currentPath, StringComparison.Ordinal)) return;
        CommitStep(label); // no step when nothing changed
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
    }
}
