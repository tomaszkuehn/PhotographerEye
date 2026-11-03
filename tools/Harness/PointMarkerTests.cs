using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using ImgViewer.Controls;
using ImgViewer.Imaging;

internal static partial class Program
{
    private static void TestPointMarkers()
    {
        // Photo 3000x2000 shown in an 800x533 viewport; heal spot: target (0.25, 0.5), source (0.75, 0.5), radius 2% of 3000 px.
        var native = new Size(3000, 2000);
        var v = MakeViewer(new Size(800, 533), MakeSource(1500, 1000), native);
        var f = v.ImageRect;
        var o = new PointMarkerOverlay();
        o.Attach(v);
        o.SetMapping(GeoState.Default, 3000, 2000);
        PointMarkerOverlay.Marker[] Spot(Point t, Point s) => new[]
        {
            new PointMarkerOverlay.Marker("spot1|target", t, 0.02, false, "Spot 1"),
            new PointMarkerOverlay.Marker("spot1|source", s, 0.02, true, "", "spot1|target")
        };
        o.SetMarkers(Spot(new Point(0.25, 0.5), new Point(0.75, 0.5)));
        List<Ellipse> Drawn(PointMarkerOverlay ov) => ov.Children.OfType<Ellipse>().Where(e => !e.IsHitTestVisible).ToList();
        var circles = Drawn(o);
        double expR = 0.02 * 3000 * f.Width / 3000;
        bool Near(double a, double b, double tol = 0.6) => Math.Abs(a - b) <= tol;
        Ellipse? At(List<Ellipse> list, double cx, double cy) => list.FirstOrDefault(e =>
            Near(Canvas.GetLeft(e) + e.Width / 2, cx) && Near(Canvas.GetTop(e) + e.Height / 2, cy) && Near(e.Width / 2, expR));
        var tView = new Point(f.X + 0.25 * f.Width, f.Y + 0.5 * f.Height);
        var sView = new Point(f.X + 0.75 * f.Width, f.Y + 0.5 * f.Height);
        var tgt = At(circles, tView.X, tView.Y);
        var src = At(circles, sView.X, sView.Y);
        Check(circles.Count == 4 && tgt is not null && src is not null,
              $"markers: target and source circles at the mapped points with the spot radius ({circles.Count} shapes, r {expR:0.0})");
        Check(tgt?.StrokeDashArray is not { Count: > 0 } && src?.StrokeDashArray is { Count: > 0 }, "markers: source drawn dashed, target solid");
        Check(o.Children.OfType<Path>().Count() == 2 && o.Children.OfType<TextBlock>().Count(t => t.Text == "Spot 1") == 1,
              "markers: arrow from source to target and the spot label");
        Check(o.Children.OfType<Ellipse>().Count(e => e.IsHitTestVisible) == 2 && o.Background is null,
              "markers: only the marker discs take the mouse");

        // Hit test: inside the circle, outside, and the small minimum hit area.
        Check(o.HitTestMarker(tView + new Vector(expR * 0.8, 0)) == "spot1|target" && o.HitTestMarker(sView) == "spot1|source" &&
              o.HitTestMarker(new Point(f.X + 0.5 * f.Width, f.Y + 0.5 * f.Height)) is null,
              "markers: hit test finds the circle under the mouse");

        // Drag the source: grabbed off-centre, the point follows the mouse with the grab offset; events live + final.
        var moves = new List<(string Key, Point P, bool Final)>();
        o.MarkerMoved += (k, p, fin) => moves.Add((k, p, fin));
        var grab = sView + new Vector(5, -3);
        bool began = o.BeginDragAt(grab);
        o.DragTo(grab + new Vector(-100, 40));
        // Host re-sends the markers mid-drag with the old value: the dragged one stays under the mouse.
        o.SetMarkers(Spot(new Point(0.25, 0.5), new Point(0.75, 0.5)));
        o.DragTo(grab + new Vector(-200, 50));
        o.EndDrag();
        var expEnd = o.ToSrc(sView + new Vector(-200, 50));
        Check(began && moves.Count == 3 && moves.Take(2).All(m => !m.Final && m.Key == "spot1|source") && moves[2].Final &&
              Near(moves[2].P.X, expEnd.X, 1e-6) && Near(moves[2].P.Y, expEnd.Y, 1e-6),
              $"markers: drag moves the source with the grab offset ({moves.Count} events, end {moves.LastOrDefault().P.X:0.000},{moves.LastOrDefault().P.Y:0.000})");
        var srcNow = o.Find("spot1|source");
        Check(srcNow is not null && Near(srcNow.P.X, expEnd.X, 1e-6) && !o.IsDragging && o.Find("spot1|target")!.P == new Point(0.25, 0.5),
              "markers: target unchanged, drag ended");

        // Dragging past the edge clamps to the photo; a click without movement sends nothing.
        moves.Clear();
        o.BeginDragAt(tView);
        o.DragTo(new Point(-500, -500));
        o.EndDrag();
        Check(moves.Count == 2 && moves[1].P == new Point(0, 0), $"markers: drag clamps to the photo ({moves.LastOrDefault().P})");
        moves.Clear();
        o.BeginDragAt(o.ToView(o.Find("spot1|source")!.P));
        o.EndDrag();
        Check(moves.Count == 0, "markers: a click without movement changes nothing");

        // Grabbing reports the key (host selects that spot); a selected spot is drawn in the accent colour.
        string? grabbed = null;
        o.MarkerGrabbed += k => grabbed = k;
        o.BeginDragAt(o.ToView(o.Find("spot1|source")!.P));
        o.EndDrag();
        o.SetMarkers(Spot(new Point(0.25, 0.5), new Point(0.75, 0.5)).Select(m => m with { Selected = true }).ToArray());
        var sel = Drawn(o).Where(e => e.StrokeThickness < 2.5).ToList();
        Check(grabbed == "spot1|source" && sel.Count == 2 && sel.All(e => e.Stroke is System.Windows.Media.SolidColorBrush b && b.Color.B < 0xA0 && e.StrokeThickness >= 2),
              $"markers: grab reported ({grabbed}), selected spot drawn highlighted");
        o.SetMarkers(Spot(new Point(0.25, 0.5), o.Find("spot1|source")!.P));

        // Not interactive (point pick / eyedropper running): no hit areas, no drag.
        o.Interactive = false;
        Check(!o.Children.OfType<Ellipse>().Any(e => e.IsHitTestVisible) && !o.BeginDragAt(o.ToView(o.Find("spot1|source")!.P)),
              "markers: non-interactive mode only draws");
        o.Interactive = true;

        // Rotated 90°: the photo point (0.25, 0.5) lands at output (0.5, 0.25) in a 2000x3000 frame; dragging maps back.
        var vr = MakeViewer(new Size(533, 800), MakeSource(1000, 1500), new Size(2000, 3000));
        var fr = vr.ImageRect;
        var orr = new PointMarkerOverlay();
        orr.Attach(vr);
        var geo = GeoState.Default with { Rot90 = 1 };
        orr.SetMapping(geo, 3000, 2000);
        orr.SetMarkers(new[] { new PointMarkerOverlay.Marker("a|p", new Point(0.25, 0.5), 0.02, false, "") });
        var e1 = Drawn(orr).FirstOrDefault();
        var exp = PhotoGeo.MapToOutput(geo, 3000, 2000, new Point(0.25, 0.5));
        double rr = 0.02 * 3000 * fr.Height / 3000;
        Check(e1 is not null && Near(Canvas.GetLeft(e1) + e1.Width / 2, fr.X + exp.X * fr.Width) && Near(Canvas.GetTop(e1) + e1.Height / 2, fr.Y + exp.Y * fr.Height) &&
              Near(e1.Width / 2, rr, 1.0),
              $"markers: follow the rotation (r {e1?.Width / 2:0.0} vs {rr:0.0})");
        Point? last = null;
        orr.MarkerMoved += (_, p, _) => last = p;
        var c0 = orr.ToView(new Point(0.25, 0.5));
        orr.BeginDragAt(c0);
        orr.DragTo(c0 + new Vector(0, 30)); // down on screen = towards larger photo x when rotated 90° clockwise
        orr.EndDrag();
        Check(last is { } lp && lp.X > 0.25 + 0.005 && Near(lp.Y, 0.5, 0.002),
              $"markers: drag on the rotated view moves along the photo axis ({last})");

        // Zero radius -> cross; no markers -> empty.
        orr.SetMarkers(new[] { new PointMarkerOverlay.Marker("a|p", new Point(0.5, 0.5), 0, false, "") });
        Check(!Drawn(orr).Any() && orr.Children.OfType<Path>().Count() == 2, "markers: point without a radius drawn as a cross");
        orr.SetMarkers(Array.Empty<PointMarkerOverlay.Marker>());
        Check(orr.Children.Count == 0, "markers: cleared");
    }
}
