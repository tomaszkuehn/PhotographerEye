using System;
using System.Linq;
using ImgViewer.Imaging;

internal static partial class Program
{
    private static void TestChannelCurves()
    {
        var c = new ChannelCurve();
        Check(!c.HasEffect && Math.Abs(c.Map(0.37) - 0.37) < 1e-9, "curves: empty curve is identity");

        int k = c.Upsert(0.5, 0.5);
        Check(!c.HasEffect && Math.Abs(c.Map(0.25) - 0.25) < 1e-6, "curves: knot on the diagonal has no effect");
        c.SetOut(k, 0.7);
        Check(c.HasEffect && Math.Abs(c.Map(0.5) - 0.7) < 1e-6 && c.Map(0.0) == 0.0 && c.Map(1.0) == 1.0,
              $"curves: lifted midpoint passes through the knot, ends fixed ({c.Map(0.5):0.000})");
        var t = c.Table(1024);
        Check(Enumerable.Range(1, t.Length - 1).All(i => t[i] >= t[i - 1] - 1e-12), "curves: monotone table");
        Check(Math.Abs(c.Map(c.Inverse(0.6)) - 0.6) < 1e-4, "curves: inverse round trip");

        // Steep S-curve stays monotone and inside 0..1 (Fritsch–Carlson).
        var s = new ChannelCurve();
        s.Upsert(0.25, 0.05);
        s.Upsert(0.75, 0.95);
        var ts = s.Table(2048);
        Check(Enumerable.Range(1, ts.Length - 1).All(i => ts[i] >= ts[i - 1] - 1e-12) && ts.All(v => v >= 0 && v <= 1),
              "curves: strong S-curve is monotone and in range");

        // Neighbour clamping keeps outputs ordered.
        var o = new ChannelCurve();
        o.Upsert(0.3, 0.3);
        int hi = o.Upsert(0.6, 0.6);
        o.SetOut(hi, 0.1);
        Check(o.Knots[1].Out >= o.Knots[0].Out, "curves: dragging below the left neighbour is clamped");

        // Pairs round trip.
        var back = new ChannelCurve();
        back.FromPairs(c.ToPairs());
        Check(back.Count == 1 && Math.Abs(back.Map(0.5) - 0.7) < 1e-9, "curves: pairs round trip");
        back.FromPairs(new[] { new[] { double.NaN, 0.5 }, new[] { 0.4 } });
        Check(back.Count == 0, "curves: invalid pairs ignored");

        // Red-only curve changes only R, in 16-bit and 8-bit.
        var rgb = new RgbCurves();
        rgb.R.Upsert(0.5, 0.75);
        Check(rgb.HasEffect && !rgb.G.HasEffect && !rgb.B.HasEffect, "curves: only R has effect");
        const int W = 256;
        var img = new Raw16Image(W, 1);
        for (int x = 0; x < W; x++) { ushort v = (ushort)Math.Round(x / 255.0 * 65535); img.R[x] = v; img.G[x] = v; img.B[x] = v; }
        var g0 = (ushort[])img.G.Clone();
        rgb.Apply16(img);
        Check(img.G.SequenceEqual(g0) && img.B.SequenceEqual(g0), "curves: 16-bit R curve leaves G and B");
        Check(Math.Abs(img.R[128] / 65535.0 - rgb.R.Map(128 / 255.0)) < 1.0 / 65535 + 1e-9 && img.R[128] > g0[128] + 10000 &&
              img.R[0] == 0 && img.R[W - 1] == 65535, $"curves: 16-bit R lifted mid ({img.R[128] / 65535.0:0.000}), ends kept");

        var buf = new byte[W * 4];
        for (int x = 0; x < W; x++) { buf[x * 4] = (byte)x; buf[x * 4 + 1] = (byte)x; buf[x * 4 + 2] = (byte)x; buf[x * 4 + 3] = 200; }
        rgb.Apply8(buf);
        Check(Enumerable.Range(0, W).All(x => buf[x * 4] == x && buf[x * 4 + 1] == x && buf[x * 4 + 3] == 200) &&
              buf[128 * 4 + 2] > 128 + 40, $"curves: 8-bit R curve (BGRA) changes only red ({buf[128 * 4 + 2]})");

        // Through the pipeline: PhotoAdjustments with curves is not default and tints a gray ramp red.
        var adj = new PhotoAdjustments { Curves = rgb.Clone() };
        Check(!adj.IsDefault && new PhotoAdjustments { Curves = new RgbCurves() }.IsDefault, "curves: IsDefault respects curve effect");
        var src = new Raw16Image(W, 1);
        for (int x = 0; x < W; x++) { ushort v = (ushort)Math.Round(Math.Pow(x / 255.0, 2.2) * 65535); src.R[x] = v; src.G[x] = v; src.B[x] = v; }
        var plain = Adjust16.Apply(src, 0, 1.0, null, out _, extra: null);
        var tinted = Adjust16.Apply(src, 0, 1.0, null, out var hist, extra: adj);
        Check(tinted.R[128] > plain.R[128] + 5000 && Math.Abs(tinted.G[128] - plain.G[128]) < 400 && Math.Abs(tinted.B[128] - plain.B[128]) < 400,
              $"curves: Adjust16 pipeline applies R curve (R {plain.R[128]}->{tinted.R[128]}, G {plain.G[128]}->{tinted.G[128]})");
        Check(hist is not null && hist.R.Skip(128).Sum() > hist.G.Skip(128).Sum(), "curves: histogram computed after the curves");
    }
}
