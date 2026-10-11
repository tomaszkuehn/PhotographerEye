using System;
using System.Collections.Generic;
using System.Text;

namespace ImgViewer.Imaging;

public sealed record StretchKnot(double In, double Out);

public sealed class ManualStretchMap
{
    private readonly List<StretchKnot> _knots = new();

    public IReadOnlyList<StretchKnot> Knots => _knots;
    public int Count => _knots.Count;

    public bool HasEffect
    {
        get
        {
            foreach (var k in _knots)
                if (Math.Abs(k.Out - k.In) > 0.002)
                    return true;
            return false;
        }
    }

    public void Clear() => _knots.Clear();

    public int SetOut(int index, double @out)
    {
        var k = _knots[index];
        _knots[index] = k with { Out = @out };
        return index;
    }

    public int Upsert(double @in, double @out)
    {
        for (int i = 0; i < _knots.Count; i++)
        {
            if (Math.Abs(_knots[i].In - @in) < 0.0005)
            {
                double lo = ClampLo(i), hi = ClampHi(i);
                _knots[i] = _knots[i] with { Out = Math.Clamp(@out, lo, hi) };
                return i;
            }
        }
        int idx = 0;
        while (idx < _knots.Count && _knots[idx].In < @in) idx++;
        double lo2 = idx > 0 ? _knots[idx - 1].Out : 0.0;
        double hi2 = idx < _knots.Count ? _knots[idx].Out : 1.0;
        _knots.Insert(idx, new StretchKnot(@in, Math.Clamp(@out, lo2, hi2)));
        return idx;
    }

    private double ClampLo(int i) => i > 0 ? _knots[i - 1].Out : 0.0;
    private double ClampHi(int i) => i < _knots.Count - 1 ? _knots[i + 1].Out : 1.0;

    public double Map(double x)
    {
        if (_knots.Count == 0) return x;
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double pIn = 0, pOut = 0;
        foreach (var k in _knots)
        {
            if (x < k.In)
            {
                double d = k.In - pIn;
                return d <= 1e-9 ? pOut : pOut + (k.Out - pOut) * ((x - pIn) / d);
            }
            pIn = k.In;
            pOut = k.Out;
        }
        double dl = 1 - pIn;
        return dl <= 1e-9 ? pOut : pOut + (1 - pOut) * ((x - pIn) / dl);
    }

    public double Inverse(double y)
    {
        if (_knots.Count == 0) return y;
        if (y <= 0) return 0;
        if (y >= 1) return 1;
        double pIn = 0, pOut = 0;
        foreach (var k in _knots)
        {
            if (y < k.Out)
            {
                double d = k.Out - pOut;
                return d <= 1e-9 ? k.In : pIn + (k.In - pIn) * ((y - pOut) / d);
            }
            pIn = k.In;
            pOut = k.Out;
        }
        double dl = 1 - pOut;
        return dl <= 1e-9 ? pIn : pIn + (1 - pIn) * ((y - pOut) / dl);
    }

    public ManualStretchMap Clone()
    {
        var c = new ManualStretchMap();
        c._knots.AddRange(_knots);
        return c;
    }

    public override string ToString()
    {
        if (_knots.Count == 0) return "neutral";
        var sb = new StringBuilder();
        foreach (var k in _knots)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append($"{k.In:0.###}->{k.Out:0.###}");
        }
        return sb.ToString();
    }
}