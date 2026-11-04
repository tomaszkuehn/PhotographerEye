using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using ImgViewer;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;

internal static partial class Program
{
    /// <summary>Stand-in command context: every member returns its default.</summary>
    public class NullContextProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? m, object?[]? args) =>
            m is not null && m.ReturnType.IsValueType && m.ReturnType != typeof(void) ? Activator.CreateInstance(m.ReturnType) : null;
    }

    private sealed class FakeToolHost : IPointerToolHost
    {
        public readonly List<string> Log = new();
        public readonly List<ICommandContext> Contexts = new();
        public ICommandContext Begin(ToolHandle tool, bool press)
        {
            var c = DispatchProxy.Create<ICommandContext, NullContextProxy>();
            Contexts.Add(c);
            Log.Add(press ? "begin-press" : "begin");
            return c;
        }
        public void Apply(ICommandContext ctx) => Log.Add("apply" + Contexts.IndexOf(ctx));
        public void End(ICommandContext ctx) => Log.Add("end" + Contexts.IndexOf(ctx));
        public void Fail(ToolHandle tool, string what, Exception ex) => Log.Add("fail:" + what);
    }

    /// <summary>
    /// Pointer tools (<see cref="IPluginHost.AddTool"/>): one press shares one context and ends in one step, other
    /// buttons are ignored during a press, Hover only when asked, Cancel/deactivate end the press, plugin exceptions
    /// end it too, coordinates map to the Source and Shown spaces.
    /// </summary>
    private static void TestPointerTools()
    {
        var events = new List<(PointerPhase Phase, PointerButton Button, ICommandContext Ctx)>();
        bool throwNext = false;
        var d = new ToolDescriptor
        {
            Id = "brush", Label = "Brush",
            Pointer = (e, ctx) =>
            {
                events.Add((e.Phase, e.Button, ctx));
                if (throwNext) { throwNext = false; throw new InvalidOperationException("boom"); }
            }
        };
        int activated = 0, deactivated = 0;
        d.Activated = _ => activated++;
        d.Deactivated = _ => deactivated++;
        var tool = new ToolHandle(d);
        var host = new FakeToolHost();
        var s = new PointerToolSession(host);

        Check(!s.Down(new PointerEvent { Button = PointerButton.Left }) && events.Count == 0, "tool: no input before activation");
        s.Activate(tool);
        Check(tool.Active && s.Tool == tool && activated == 1 && host.Log.SequenceEqual(new[] { "begin", "end0" }),
              $"tool: activation runs Activated in its own context ({string.Join(" ", host.Log)})");
        host.Log.Clear();
        host.Contexts.Clear();

        s.Move(new PointerEvent { X = 0.1 });
        Check(events.Count == 0 && host.Log.Count == 0, "tool: no Hover unless the tool asks for it");

        Check(s.Down(new PointerEvent { Button = PointerButton.Left, X = 0.2, Y = 0.2 }) && s.Pressed, "tool: Down taken");
        s.Move(new PointerEvent { X = 0.3, Y = 0.3 });
        Check(!s.Down(new PointerEvent { Button = PointerButton.Right }), "tool: second button ignored during a press");
        Check(!s.Up(new PointerEvent { Button = PointerButton.Right }) && s.Pressed, "tool: Up of another button does not end the press");
        s.Move(new PointerEvent { X = 0.4, Y = 0.4 });
        Check(s.Up(new PointerEvent { Button = PointerButton.Left }) && !s.Pressed, "tool: Up ends the press");
        Check(events.Select(e => e.Phase).SequenceEqual(new[] { PointerPhase.Down, PointerPhase.Move, PointerPhase.Move, PointerPhase.Up }),
              $"tool: phases Down Move Move Up ({string.Join(" ", events.Select(e => e.Phase))})");
        Check(events.All(e => e.Button == PointerButton.Left) && events.All(e => ReferenceEquals(e.Ctx, events[0].Ctx)),
              "tool: one context and the press button for the whole press");
        Check(host.Log.SequenceEqual(new[] { "begin-press", "apply0", "apply0", "apply0", "end0" }),
              $"tool: preview after each event, one step at Up ({string.Join(" ", host.Log)})");

        events.Clear(); host.Log.Clear(); host.Contexts.Clear();
        s.Down(new PointerEvent { Button = PointerButton.Right });
        s.Cancel();
        Check(events.Select(e => e.Phase).SequenceEqual(new[] { PointerPhase.Down, PointerPhase.Cancel }) && events[1].Button == PointerButton.Right &&
              host.Log.Last() == "end0" && !s.Pressed, "tool: Cancel ends the press and keeps its step");

        events.Clear(); host.Log.Clear(); host.Contexts.Clear();
        s.Down(new PointerEvent { Button = PointerButton.Left });
        throwNext = true;
        s.Move(new PointerEvent());
        Check(!s.Pressed && host.Log.SequenceEqual(new[] { "begin-press", "apply0", "fail:pointer Move", "end0" }),
              $"tool: an exception ends the press ({string.Join(" ", host.Log)})");
        s.Move(new PointerEvent());
        Check(events.Count == 2, "tool: no more moves after the failed press");

        d.Hover = true;
        events.Clear(); host.Log.Clear(); host.Contexts.Clear();
        s.Move(new PointerEvent { X = 0.5 });
        Check(events.Count == 1 && events[0].Phase == PointerPhase.Hover && events[0].Button == PointerButton.None &&
              host.Log.SequenceEqual(new[] { "begin", "end0" }), "tool: Hover in its own context when asked for");

        events.Clear(); host.Log.Clear(); host.Contexts.Clear();
        s.Down(new PointerEvent { Button = PointerButton.Left });
        s.Deactivate();
        Check(!tool.Active && s.Tool is null && !s.Pressed && deactivated == 1 &&
              events.Select(e => e.Phase).SequenceEqual(new[] { PointerPhase.Down, PointerPhase.Cancel }),
              "tool: deactivating cancels the press and runs Deactivated");

        var other = new ToolHandle(new ToolDescriptor { Id = "o", Pointer = (_, _) => { } });
        s.Activate(tool);
        s.Activate(other);
        Check(!tool.Active && other.Active && deactivated == 2, "tool: one active tool at a time");
        s.Activate(null);
        Check(!other.Active && s.Tool is null, "tool: Activate(null) switches off");

        // --- coordinates ---
        double w = 4000, h = 3000;
        var plain = new GeoState();
        var p = PointerToolSession.Map(new Point(0.25, 0.75), plain, plain, w, h, OverlaySpace.Source);
        Check(Math.Abs(p.X - 0.25) < 1e-9 && Math.Abs(p.Y - 0.75) < 1e-9, "tool map: no geometry is identity");
        var crop = new GeoState { CropX = 0.5, CropY = 0.5, CropW = 0.5, CropH = 0.5 };
        p = PointerToolSession.Map(new Point(0.5, 0.5), crop, crop, w, h, OverlaySpace.Source);
        Check(Math.Abs(p.X - 0.75) < 1e-6 && Math.Abs(p.Y - 0.75) < 1e-6, $"tool map: cropped view to Source ({p.X:0.###}, {p.Y:0.###})");
        p = PointerToolSession.Map(new Point(0.5, 0.5), crop, crop, w, h, OverlaySpace.Shown);
        Check(Math.Abs(p.X - 0.5) < 1e-6 && Math.Abs(p.Y - 0.5) < 1e-6, "tool map: cropped view to Shown is the view fraction");
        // Crop tool open: the display is uncropped, Shown still follows the edit's crop.
        p = PointerToolSession.Map(new Point(0.75, 0.75), plain, crop, w, h, OverlaySpace.Shown);
        Check(Math.Abs(p.X - 0.5) < 1e-6 && Math.Abs(p.Y - 0.5) < 1e-6, "tool map: uncropped display maps into the crop for Shown");
        var rot = new GeoState { Rot90 = 1 };
        var back = PhotoGeo.MapToOutput(rot, w, h, PointerToolSession.Map(new Point(0.2, 0.3), rot, rot, w, h, OverlaySpace.Source));
        Check(Math.Abs(back.X - 0.2) < 1e-6 && Math.Abs(back.Y - 0.3) < 1e-6, "tool map: quarter turn round trip");
        double px = PointerToolSession.ScreenPixel(new Point(0.5, 0.5), new Point(0.5 + 1.0 / 4000, 0.5), plain, w, h, OverlaySpace.Source);
        Check(Math.Abs(px - 1.0 / 4000) < 1e-12, "tool map: ScreenPixel in Source width");
        px = PointerToolSession.ScreenPixel(new Point(0.5, 0.5), new Point(0.5 + 1.0 / 4000, 0.5), crop, w, h, OverlaySpace.Shown);
        Check(Math.Abs(px - 1.0 / 2000) < 1e-12, "tool map: ScreenPixel in Shown width (half-width crop)");

        var store = new RegistrationStore();
        store.Add("tool", new Registered<ToolHandle> { PluginId = "p", PluginName = "P", FullId = "p:brush", LocalId = "brush", D = tool });
        Check(store.Summary().Contains("1 tool(s)"), "tool: Plugins window summary lists tools");
    }
}
