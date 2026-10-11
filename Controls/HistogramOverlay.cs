using System;
using System.Windows;
using System.Windows.Media;

namespace ImgViewer.Controls;

public sealed class HistogramOverlay : FrameworkElement
{
    private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0x14, 0x14, 0x14)));
    private static readonly Brush BorderBrushH = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush RedFill = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0x3D, 0x3D)));
    private static readonly Brush GreenFill = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0x45, 0xFF, 0x45)));
    private static readonly Brush BlueFill = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0x4D, 0x5C, 0xFF)));
    private static readonly Brush LumStroke = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0xEC, 0xEC, 0xEC)));

    private static readonly Pen BorderPen = Freeze(new Pen(BorderBrushH, 1.0));
    private static readonly Pen LumPen = Freeze(new Pen(LumStroke, 1.2));

    private int[]? _lum;
    private int[]? _r;
    private int[]? _g;
    private int[]? _b;

    public void SetHistogram(int[] lum, int[] r, int[] g, int[] b)
    {
        _lum = lum;
        _r = r;
        _g = g;
        _b = b;
        InvalidateVisual();
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static Pen Freeze(Pen p)
    {
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 4 || h <= 4) return;

        var rect = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRoundedRectangle(BgBrush, BorderPen, rect, 8, 8);

        if (_lum is null || _r is null || _g is null || _b is null) return;

        int max = 1;
        foreach (var arr in new[] { _lum, _r, _g, _b })
        {
            foreach (var v in arr)
                if (v > max) max = v;
        }

        double pl = 7, pt = 7, pb = 7, pr = 7;
        double plotW = w - pl - pr;
        double plotH = h - pt - pb;
        double yb = h - pb;

        DrawFill(dc, _b, max, pl, plotW, plotH, yb, BlueFill);
        DrawFill(dc, _g, max, pl, plotW, plotH, yb, GreenFill);
        DrawFill(dc, _r, max, pl, plotW, plotH, yb, RedFill);
        dc.DrawGeometry(null, LumPen, Poly(_lum, max, pl, plotW, plotH, yb, closed: false));
    }

    private static void DrawFill(DrawingContext dc, int[] data, int max, double x0, double plotW, double plotH, double yb, Brush brush)
    {
        dc.DrawGeometry(brush, null, Poly(data, max, x0, plotW, plotH, yb, closed: true));
    }

    private static StreamGeometry Poly(int[] data, int max, double x0, double plotW, double plotH, double yb, bool closed)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double step = plotW / (data.Length - 1);
            ctx.BeginFigure(new Point(x0, yb), closed, closed);
            for (int i = 0; i < data.Length; i++)
            {
                double v = Math.Sqrt(data[i] / (double)max);
                double y = yb - v * plotH;
                ctx.LineTo(new Point(x0 + i * step, y), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }
}