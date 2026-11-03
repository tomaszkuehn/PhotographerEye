using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ImgViewer.Imaging;

public enum HistChannel { Rgb = 0, R = 1, G = 2, B = 3 }

/// <summary>
/// Tone curve of one color channel in display (sRGB-encoded) space, 0..1. Knots map an input level to an output level;
/// the ends are fixed at 0→0 and 1→1 and the curve is a monotone cubic (Fritsch–Carlson), so it never reverses tones.
/// </summary>
public sealed class ChannelCurve
{
    private readonly List<StretchKnot> _knots = new();
    private const double MinGap = 0.004;

    public IReadOnlyList<StretchKnot> Knots => _knots;
    public int Count => _knots.Count;

    public bool HasEffect
    {
        get
        {
            foreach (var k in _knots)
                if (Math.Abs(k.Out - k.In) > 0.002) return true;
            return false;
        }
    }

    public void Clear() => _knots.Clear();

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < _knots.Count) _knots.RemoveAt(index);
    }

    public ChannelCurve Clone()
    {
        var c = new ChannelCurve();
        c._knots.AddRange(_knots);
        return c;
    }

    /// <summary>Inserts a knot (or moves the one at the same input) keeping outputs ordered. Returns its index.</summary>
    public int Upsert(double @in, double @out)
    {
        @in = Math.Clamp(@in, MinGap, 1.0 - MinGap);
        for (int i = 0; i < _knots.Count; i++)
            if (Math.Abs(_knots[i].In - @in) < 0.01)
            {
                SetOut(i, @out);
                return i;
            }
        int idx = 0;
        while (idx < _knots.Count && _knots[idx].In < @in) idx++;
        _knots.Insert(idx, new StretchKnot(@in, @out));
        SetOut(idx, @out);
        return idx;
    }

    /// <summary>Moves the output of knot <paramref name="index"/>, clamped between its neighbours (monotone).</summary>
    public void SetOut(int index, double @out)
    {
        double lo = index > 0 ? _knots[index - 1].Out : 0.0;
        double hi = index < _knots.Count - 1 ? _knots[index + 1].Out : 1.0;
        _knots[index] = _knots[index] with { Out = Math.Clamp(@out, lo, hi) };
    }

    public void Restore(IEnumerable<StretchKnot> knots)
    {
        _knots.Clear();
        foreach (var k in knots)
        {
            if (!double.IsFinite(k.In) || !double.IsFinite(k.Out)) continue;
            Upsert(k.In, Math.Clamp(k.Out, 0.0, 1.0));
        }
    }

    /// <summary>Evaluates the curve at <paramref name="x"/> (0..1).</summary>
    public double Map(double x)
    {
        if (_knots.Count == 0) return Math.Clamp(x, 0.0, 1.0);
        var (xs, ys, ms) = Spline();
        return Eval(xs, ys, ms, x);
    }

    /// <summary>Input level that maps to <paramref name="y"/> (bisection; the curve is monotone).</summary>
    public double Inverse(double y)
    {
        if (_knots.Count == 0) return Math.Clamp(y, 0.0, 1.0);
        var (xs, ys, ms) = Spline();
        double lo = 0, hi = 1;
        for (int i = 0; i < 40; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Eval(xs, ys, ms, mid) < y) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>Lookup table over <paramref name="size"/> evenly spaced input levels, values 0..1.</summary>
    public double[] Table(int size)
    {
        var t = new double[size];
        if (_knots.Count == 0)
        {
            for (int i = 0; i < size; i++) t[i] = i / (double)(size - 1);
            return t;
        }
        var (xs, ys, ms) = Spline();
        for (int i = 0; i < size; i++) t[i] = Eval(xs, ys, ms, i / (double)(size - 1));
        return t;
    }

    private (double[] X, double[] Y, double[] M) Spline()
    {
        int n = _knots.Count + 2;
        var x = new double[n];
        var y = new double[n];
        x[0] = 0; y[0] = 0;
        for (int i = 0; i < _knots.Count; i++) { x[i + 1] = _knots[i].In; y[i + 1] = _knots[i].Out; }
        x[n - 1] = 1; y[n - 1] = 1;
        // Fritsch–Carlson monotone tangents.
        var d = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double h = Math.Max(1e-9, x[i + 1] - x[i]);
            d[i] = (y[i + 1] - y[i]) / h;
        }
        var m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++)
            m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (Math.Abs(d[i]) < 1e-12) { m[i] = 0; m[i + 1] = 0; continue; }
            double a = m[i] / d[i], b = m[i + 1] / d[i];
            double s = a * a + b * b;
            if (s > 9)
            {
                double t = 3 / Math.Sqrt(s);
                m[i] = t * a * d[i];
                m[i + 1] = t * b * d[i];
            }
        }
        return (x, y, m);
    }

    private static double Eval(double[] x, double[] y, double[] m, double v)
    {
        if (v <= 0) return 0;
        if (v >= 1) return 1;
        int k = 0;
        while (k < x.Length - 2 && v > x[k + 1]) k++;
        double h = x[k + 1] - x[k];
        if (h <= 1e-9) return y[k + 1];
        double t = (v - x[k]) / h, t2 = t * t, t3 = t2 * t;
        double r = (2 * t3 - 3 * t2 + 1) * y[k] + (t3 - 2 * t2 + t) * h * m[k] + (-2 * t3 + 3 * t2) * y[k + 1] + (t3 - t2) * h * m[k + 1];
        return Math.Clamp(r, 0.0, 1.0);
    }

    public double[][] ToPairs()
    {
        var p = new double[_knots.Count][];
        for (int i = 0; i < _knots.Count; i++) p[i] = new[] { _knots[i].In, _knots[i].Out };
        return p;
    }

    public void FromPairs(double[][]? pairs)
    {
        var list = new List<StretchKnot>();
        if (pairs is not null)
            foreach (var p in pairs)
                if (p is { Length: 2 }) list.Add(new StretchKnot(p[0], p[1]));
        Restore(list);
    }
}

/// <summary>Separate R, G and B tone curves applied to the display-encoded result (after tone, color and gamma).</summary>
public sealed class RgbCurves
{
    public ChannelCurve R { get; } = new();
    public ChannelCurve G { get; } = new();
    public ChannelCurve B { get; } = new();

    public bool HasEffect => R.HasEffect || G.HasEffect || B.HasEffect;

    public ChannelCurve this[HistChannel ch] => ch switch
    {
        HistChannel.R => R,
        HistChannel.G => G,
        HistChannel.B => B,
        _ => throw new ArgumentOutOfRangeException(nameof(ch))
    };

    public RgbCurves Clone()
    {
        var c = new RgbCurves();
        c.R.Restore(R.Knots);
        c.G.Restore(G.Knots);
        c.B.Restore(B.Knots);
        return c;
    }

    public void Clear() { R.Clear(); G.Clear(); B.Clear(); }

    /// <summary>16-bit lookup table of one channel (identity when the channel has no effect).</summary>
    public int[] IntLut16(HistChannel ch)
    {
        var c = this[ch];
        var lut = new int[65536];
        if (!c.HasEffect)
        {
            for (int i = 0; i < lut.Length; i++) lut[i] = i;
            return lut;
        }
        var t = c.Table(65536);
        for (int i = 0; i < lut.Length; i++) lut[i] = (int)Math.Round(t[i] * 65535.0);
        return lut;
    }

    private static ushort[]? Lut16(ChannelCurve c)
    {
        if (!c.HasEffect) return null;
        var t = c.Table(65536);
        var lut = new ushort[65536];
        for (int i = 0; i < lut.Length; i++) lut[i] = (ushort)Math.Round(t[i] * 65535.0);
        return lut;
    }

    private static byte[]? Lut8(ChannelCurve c)
    {
        if (!c.HasEffect) return null;
        var t = c.Table(256);
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Round(t[i] * 255.0);
        return lut;
    }

    /// <summary>Applies the curves in place to a 16-bit display-encoded image.</summary>
    public void Apply16(Raw16Image img, ParallelOptions? opts = null)
    {
        opts ??= new ParallelOptions();
        Plane(img.R, Lut16(R), opts);
        Plane(img.G, Lut16(G), opts);
        Plane(img.B, Lut16(B), opts);
    }

    private static void Plane(ushort[] p, ushort[]? lut, ParallelOptions opts)
    {
        if (lut is null) return;
        const int Chunk = 1 << 16;
        int parts = (p.Length + Chunk - 1) / Chunk;
        Parallel.For(0, parts, opts, c =>
        {
            int e = Math.Min(p.Length, (c + 1) * Chunk);
            for (int i = c * Chunk; i < e; i++) p[i] = lut[p[i]];
        });
    }

    /// <summary>Applies the curves in place to a BGRA32 buffer.</summary>
    public void Apply8(byte[] bgra)
    {
        var lr = Lut8(R) ?? Identity8;
        var lg = Lut8(G) ?? Identity8;
        var lb = Lut8(B) ?? Identity8;
        int px = bgra.Length / 4;
        const int Chunk = 1 << 15;
        int parts = (px + Chunk - 1) / Chunk;
        Parallel.For(0, parts, c =>
        {
            int e = Math.Min(px, (c + 1) * Chunk) * 4;
            for (int i = c * Chunk * 4; i < e; i += 4)
            {
                bgra[i] = lb[bgra[i]];
                bgra[i + 1] = lg[bgra[i + 1]];
                bgra[i + 2] = lr[bgra[i + 2]];
            }
        });
    }

    private static readonly byte[] Identity8 = BuildIdentity8();
    private static byte[] BuildIdentity8()
    {
        var b = new byte[256];
        for (int i = 0; i < 256; i++) b[i] = (byte)i;
        return b;
    }
}
