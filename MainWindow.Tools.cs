using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>
/// Plugin pointer tools (<see cref="IPluginHost.AddTool"/>): the Plugin tools menu and shortcuts, mouse input on the
/// photo mapped to the tool's space, one undo step per press (<see cref="PointerToolSession"/>), the tool's drawing.
/// </summary>
public partial class MainWindow
{
    private PointerToolSession? _toolSession;
    private PointerToolSession Tools => _toolSession ??= new PointerToolSession(new ToolHostBridge(this));
    private readonly List<(KeyGesture Gesture, Registered<ToolHandle> Tool)> _toolShortcuts = new();
    private Cursor? _toolCursor;

    private void InitTools()
    {
        Viewer.PreviewMouseDown += OnToolMouseDown;
        Viewer.PreviewMouseMove += OnToolMouseMove;
        Viewer.PreviewMouseUp += OnToolMouseUp;
        Viewer.LostMouseCapture += (_, _) => { if (Tools.Pressed) Tools.Cancel(); };
    }

    private Registered<ToolHandle>? ToolReg(ToolHandle? t) =>
        t is null ? null : _pluginReg.All<ToolHandle>().FirstOrDefault(r => ReferenceEquals(r.D, t));

    private Registered<ToolHandle>? ActiveToolReg() => ToolReg(Tools.Tool);

    /// <summary>Another mode owns the mouse (crop, point pick, eyedropper, mask editing) or nothing is open.</summary>
    private bool ToolInputBlocked =>
        _currentPath is null || _cropMode || _pointPick is not null || _wbPicking || _maskEditFilter is not null;

    /// <summary>Switches <paramref name="r"/> on (null or the active tool: off).</summary>
    private void SetActiveTool(Registered<ToolHandle>? r, bool quiet = false)
    {
        if (r is not null && ReferenceEquals(r.D, Tools.Tool)) r = null;
        if (r is { Owner.Faulted: true })
        {
            SetStatus($"{r.D.D.Label}: plugin {r.PluginName} is disabled", error: true);
            return;
        }
        if (r is not null && _currentPath is null)
        {
            SetStatus($"{r.D.D.Label}: open a photo first", error: true);
            return;
        }
        var old = ActiveToolReg();
        Tools.Activate(r?.D);
        _toolCursor = r is null ? null : ToolCursor(r.D.D.Cursor);
        Viewer.Cursor = _toolCursor ?? Cursors.Arrow;
        UpdateToolMenuChecks();
        RefreshPluginOverlays();
        if (quiet) return;
        if (r is not null) SetStatus($"{r.D.D.Label} — drag on the photo (Esc — off)", error: false);
        else if (old is not null) SetStatus($"{old.D.D.Label} off", error: false);
    }

    private void DeactivateTool() => SetActiveTool(null, quiet: true);

    private static Cursor ToolCursor(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "hand" => Cursors.Hand,
        "pen" => Cursors.Pen,
        "arrow" => Cursors.Arrow,
        "sizeall" => Cursors.SizeAll,
        "none" => Cursors.None,
        _ => Cursors.Cross
    };

    // ------------------------------------------------------------------
    //  Menu and shortcuts
    // ------------------------------------------------------------------

    private void BuildToolMenu()
    {
        var tools = _pluginReg.All<ToolHandle>();
        foreach (var t in tools)
        {
            var tt = t;
            t.D.OnRefresh = RefreshPluginOverlays;
            t.D.OnActiveRequested = (h, on) =>
            {
                if (on != ReferenceEquals(Tools.Tool, h)) SetActiveTool(on ? tt : null);
            };
        }
        if (Tools.Tool is { } cur && !tools.Any(r => ReferenceEquals(r.D, cur))) DeactivateTool();
        if (MnuPluginTools is null) return;
        MnuPluginTools.Items.Clear();
        foreach (var t in tools)
        {
            var it = new MenuItem
            {
                Header = t.D.D.Label,
                InputGestureText = t.D.D.Shortcut ?? "",
                IsCheckable = true,
                IsChecked = ReferenceEquals(t.D, Tools.Tool),
                Tag = t,
                ToolTip = "Plugin " + t.PluginName,
                IsEnabled = t.Owner is not { Faulted: true }
            };
            AutomationProperties.SetAutomationId(it, "PluginTool_" + t.FullId);
            var tt = t;
            it.Click += (_, _) => SetActiveTool(tt);
            MnuPluginTools.Items.Add(it);
        }
        MnuPluginTools.Visibility = MnuPluginTools.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateToolMenuChecks()
    {
        if (MnuPluginTools is null) return;
        foreach (var it in MnuPluginTools.Items.OfType<MenuItem>())
            it.IsChecked = it.Tag is Registered<ToolHandle> r && ReferenceEquals(r.D, Tools.Tool);
    }

    private void BuildToolShortcuts()
    {
        _toolShortcuts.Clear();
        var conv = new KeyGestureConverter();
        foreach (var t in _pluginReg.All<ToolHandle>())
        {
            var sc = t.D.D.Shortcut;
            if (string.IsNullOrWhiteSpace(sc)) continue;
            if (ReservedShortcuts.Any(r => string.Equals(r, sc.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)))
            {
                Debug.Log($"PLUGINS: shortcut {sc} of tool {t.FullId} is reserved");
                continue;
            }
            try
            {
                if (conv.ConvertFromInvariantString(sc) is KeyGesture g) _toolShortcuts.Add((g, t));
            }
            catch (Exception ex)
            {
                Debug.Log($"PLUGINS: bad shortcut '{sc}' of tool {t.FullId}: {ex.Message}");
            }
        }
    }

    /// <summary>Esc ends a press or switches the tool off; tool shortcuts toggle. Returns true when handled.</summary>
    private bool HandleToolKey(KeyEventArgs e, bool escOnly)
    {
        if (e.Key == Key.Escape && Tools.Tool is not null)
        {
            if (Tools.Pressed)
            {
                Tools.Cancel();
                if (Viewer.IsMouseCaptured) Viewer.ReleaseMouseCapture();
            }
            else SetActiveTool(null);
            e.Handled = true;
            return true;
        }
        if (escOnly || _toolShortcuts.Count == 0 || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return false;
        foreach (var (g, t) in _toolShortcuts)
        {
            if (!g.Matches(null, e)) continue;
            SetActiveTool(t);
            e.Handled = true;
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    //  Mouse
    // ------------------------------------------------------------------

    private static PointerButton ToolButton(MouseButton b) => b switch
    {
        MouseButton.Left => PointerButton.Left,
        MouseButton.Right => PointerButton.Right,
        MouseButton.Middle => PointerButton.Middle,
        _ => PointerButton.None
    };

    /// <summary>Pointer event at the mouse position in the active tool's space; null when nothing is shown.</summary>
    private PointerEvent? ToolEvent(MouseEventArgs e, PointerButton button)
    {
        if (Tools.Tool is not { } t || _baseInfo is not ImageInfo bi || (_lastDisplayed ?? _baseBitmap) is not { } disp) return null;
        if (disp.PixelWidth <= 0 || disp.PixelHeight <= 0) return null;
        var p = e.GetPosition(Viewer);
        bool inside = Viewer.TryScreenToPixel(p, out double bx, out double by);
        Viewer.TryScreenToPixel(new Point(p.X + 1, p.Y), out double bx2, out double by2);
        var dg = CurrentDisplayGeo();
        var f1 = new Point(bx / disp.PixelWidth, by / disp.PixelHeight);
        var f2 = new Point(bx2 / disp.PixelWidth, by2 / disp.PixelHeight);
        var q = PointerToolSession.Map(f1, dg, _geo, bi.Width, bi.Height, t.D.Space);
        var s1 = PointerToolSession.Map(f1, dg, _geo, bi.Width, bi.Height, OverlaySpace.Source);
        var s2 = PointerToolSession.Map(f2, dg, _geo, bi.Width, bi.Height, OverlaySpace.Source);
        var mods = Keyboard.Modifiers;
        return new PointerEvent
        {
            Button = button,
            X = q.X,
            Y = q.Y,
            Inside = inside,
            Shift = mods.HasFlag(ModifierKeys.Shift),
            Ctrl = mods.HasFlag(ModifierKeys.Control),
            Alt = mods.HasFlag(ModifierKeys.Alt),
            ScreenPixel = PointerToolSession.ScreenPixel(s1, s2, _geo, bi.Width, bi.Height, t.D.Space)
        };
    }

    private void OnToolMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Tools.Tool is null || ToolInputBlocked) return;
        var b = ToolButton(e.ChangedButton);
        if (b == PointerButton.None) return;
        if (Tools.Pressed)
        {
            e.Handled = true;
            return;
        }
        // Outside the photo the viewer keeps panning.
        if (ToolEvent(e, b) is not { Inside: true } ev) return;
        ev.ClickCount = e.ClickCount;
        if (!Tools.Down(ev)) return;
        e.Handled = true;
        if (Tools.Pressed) Viewer.CaptureMouse();
    }

    private void OnToolMouseMove(object sender, MouseEventArgs e)
    {
        if (Tools.Tool is not { } t) return;
        if (_toolCursor is not null && !ToolInputBlocked && !ReferenceEquals(Viewer.Cursor, _toolCursor) && Mouse.LeftButton == MouseButtonState.Released)
            Viewer.Cursor = _toolCursor;
        if (ToolInputBlocked || (!Tools.Pressed && !t.D.Hover)) return;
        if (ToolEvent(e, PointerButton.None) is not { } ev) return;
        bool pressed = Tools.Pressed;
        Tools.Move(ev);
        if (pressed) e.Handled = true;
    }

    private void OnToolMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!Tools.Pressed) return;
        var b = ToolButton(e.ChangedButton);
        var ev = ToolEvent(e, b) ?? new PointerEvent { Button = b };
        ev.Button = b;
        if (!Tools.Up(ev)) return;
        e.Handled = true;
        if (Viewer.IsMouseCaptured) Viewer.ReleaseMouseCapture();
    }

    // ------------------------------------------------------------------
    //  Host side of the session
    // ------------------------------------------------------------------

    private sealed class ToolHostBridge : IPointerToolHost
    {
        private readonly MainWindow _w;
        public ToolHostBridge(MainWindow w) => _w = w;

        public ICommandContext Begin(ToolHandle tool, bool press)
        {
            var r = _w.ToolReg(tool);
            // Earlier edits become their own step; a press is one step of its own.
            _w.CommitHistNow();
            if (press)
            {
                _w._histLastKey = null;
                _w._histCommitTimer?.Stop();
            }
            return new CommandContext(_w, r?.PluginId ?? "", r?.Owner, tool.D.Label) { IsPress = press };
        }

        public void Apply(ICommandContext ctx) => ((CommandContext)ctx).Apply();

        public void End(ICommandContext ctx)
        {
            var c = (CommandContext)ctx;
            // Hover, Activated, Deactivated: no step unless they changed something.
            if (!c.IsPress && !c.Changed)
            {
                _w.RefreshPluginOverlays();
                return;
            }
            _w.FinishCommand(c, c.Owner, "tool " + c.StepLabel);
        }

        public void Fail(ToolHandle tool, string what, Exception ex)
        {
            var r = _w.ToolReg(tool);
            PluginManager.Instance.ReportFailure(r?.Owner, $"tool {r?.FullId ?? tool.D.Id} {what}", ex);
            _w.SetStatus($"{tool.D.Label}: {ex.Message}", error: true);
            if (r?.Owner is { Faulted: true }) _w.Dispatcher.BeginInvoke(_w.DeactivateTool);
        }
    }
}
