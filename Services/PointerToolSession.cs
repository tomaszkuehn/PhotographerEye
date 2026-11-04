using System;
using System.Windows;
using ImgViewer.Imaging;
using ImgViewer.Plugins;

namespace ImgViewer.Services;

/// <summary>What <see cref="PointerToolSession"/> needs from the main window.</summary>
public interface IPointerToolHost
{
    /// <summary>
    /// New command context for the tool. <paramref name="press"/>: a press starts, earlier edits become their own
    /// undo step and the press's changes are collected into one step.
    /// </summary>
    ICommandContext Begin(ToolHandle tool, bool press);
    /// <summary>Applies the changes collected so far for preview, without recording a step.</summary>
    void Apply(ICommandContext ctx);
    /// <summary>Applies the changes and records them as one undo step (nothing when unchanged).</summary>
    void End(ICommandContext ctx);
    /// <summary>The plugin threw.</summary>
    void Fail(ToolHandle tool, string what, Exception ex);
}

/// <summary>
/// Input state of the active pointer tool, independent of WPF: one press (Down, Move.., Up or Cancel) shares one
/// command context and becomes one undo step; other buttons during a press are ignored; Hover moves only when the
/// tool asks for them. Coordinates are mapped with <see cref="Map"/>.
/// </summary>
public sealed class PointerToolSession
{
    private readonly IPointerToolHost _host;
    private ICommandContext? _press;
    private PointerButton _button;

    public PointerToolSession(IPointerToolHost host) => _host = host;

    public ToolHandle? Tool { get; private set; }
    public bool Pressed => _press is not null;

    /// <summary>Switches to <paramref name="tool"/> (null: none). The previous tool is cancelled and deactivated.</summary>
    public void Activate(ToolHandle? tool)
    {
        if (ReferenceEquals(tool, Tool)) return;
        Deactivate();
        if (tool is null) return;
        Tool = tool;
        tool.SetActiveState(true);
        if (tool.D.Activated is { } a) RunSingle(tool, "activate", ctx => a(ctx));
    }

    public void Deactivate()
    {
        var t = Tool;
        if (t is null) return;
        Cancel();
        Tool = null;
        t.SetActiveState(false);
        if (t.D.Deactivated is { } d) RunSingle(t, "deactivate", ctx => d(ctx));
    }

    /// <summary>A button went down. Returns true when the tool took it (the caller captures the mouse).</summary>
    public bool Down(PointerEvent e)
    {
        if (Tool is not { } t || _press is not null || e.Button == PointerButton.None) return false;
        e.Phase = PointerPhase.Down;
        _button = e.Button;
        _press = _host.Begin(t, press: true);
        if (!Deliver(t, e)) EndPress();
        else _host.Apply(_press!);
        return true;
    }

    /// <summary>The mouse moved: a Move during a press, a Hover otherwise (when asked for).</summary>
    public void Move(PointerEvent e)
    {
        if (Tool is not { } t) return;
        if (_press is { } ctx)
        {
            e.Phase = PointerPhase.Move;
            e.Button = _button;
            if (!Deliver(t, e)) EndPress();
            else _host.Apply(ctx);
            return;
        }
        if (!t.D.Hover) return;
        e.Phase = PointerPhase.Hover;
        e.Button = PointerButton.None;
        RunSingle(t, "hover", ctx => t.D.Pointer(e, ctx));
    }

    /// <summary>A button went up; ends the press when it is the press's button. Returns true when it did.</summary>
    public bool Up(PointerEvent e)
    {
        if (Tool is not { } t || _press is null || e.Button != _button) return false;
        e.Phase = PointerPhase.Up;
        Deliver(t, e);
        EndPress();
        return true;
    }

    /// <summary>Cancels the press, if any (Esc, capture lost); the changes made so far stay as one step.</summary>
    public void Cancel()
    {
        if (Tool is not { } t || _press is null) return;
        Deliver(t, new PointerEvent { Phase = PointerPhase.Cancel, Button = _button, ClickCount = 0 });
        EndPress();
    }

    private bool Deliver(ToolHandle t, PointerEvent e)
    {
        try
        {
            t.D.Pointer(e, _press!);
            return true;
        }
        catch (Exception ex)
        {
            _host.Fail(t, "pointer " + e.Phase, ex);
            return false;
        }
    }

    private void EndPress()
    {
        var ctx = _press;
        _press = null;
        _button = PointerButton.None;
        if (ctx is not null) _host.End(ctx);
    }

    private void RunSingle(ToolHandle t, string what, Action<ICommandContext> a)
    {
        var ctx = _host.Begin(t, press: false);
        try
        {
            a(ctx);
        }
        catch (Exception ex)
        {
            _host.Fail(t, what, ex);
        }
        _host.End(ctx);
    }

    /// <summary>
    /// Maps a point given in fractions of the displayed bitmap (drawn with <paramref name="displayGeo"/>) to the tool's
    /// space: <see cref="OverlaySpace.Source"/> is the un-cropped, un-rotated photo, <see cref="OverlaySpace.Shown"/>
    /// the image after <paramref name="geo"/> (the edit's crop and rotation, also while the crop tool shows everything).
    /// </summary>
    public static Point Map(Point displayFraction, GeoState displayGeo, GeoState geo, double srcW, double srcH, OverlaySpace space)
    {
        var src = PhotoGeo.MapFromOutput(displayGeo, srcW, srcH, displayFraction);
        return space == OverlaySpace.Shown ? PhotoGeo.MapToOutput(geo, srcW, srcH, src) : src;
    }

    /// <summary>
    /// Length of one screen pixel as a fraction of the width of the tool's space: <paramref name="a"/> and
    /// <paramref name="b"/> are two points one screen pixel apart, mapped to source fractions.
    /// </summary>
    public static double ScreenPixel(Point a, Point b, GeoState geo, double srcW, double srcH, OverlaySpace space)
    {
        double dx = (b.X - a.X) * srcW, dy = (b.Y - a.Y) * srcH;
        double width = space == OverlaySpace.Shown ? PhotoGeo.OutputWidth(geo, srcW, srcH) : srcW;
        return width > 0 ? Math.Sqrt(dx * dx + dy * dy) / width : 0;
    }
}
