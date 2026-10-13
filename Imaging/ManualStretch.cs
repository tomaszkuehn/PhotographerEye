using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ImgViewer.Imaging;

public sealed record StretchKnot(double In, double Out);

public sealed class ManualStretchMap
{
    private readonly List<StretchKnot> _knots = new();

    public IReadOnlyList<StretchKnot> Knots => _knots;
    public int Count => _knots.Count;

    private static double MinEv => PhotoOps.ToneEvMin;
    private static double MaxEv => PhotoOps.ToneEvMax;

    public bool HasEffect
    {
        get
        {
            foreach (var k in _knots)
                if (Math.Abs(k.Out - k.In) > 0.015)
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
        @in = ClampEv(@in);
        @out = ClampEv(@out);
        for (int i = 0; i < _knots.Count; i++)
        {
            if (Math.Abs(_knots[i].In - @in) < 0.01)
            {
                double lo = ClampLo(i), hi = ClampHi(i);
                _knots[i] = _knots[i] with { Out = Math.Clamp(@out, lo, hi) };
                return i;
            }
        }
        int idx = 0;
        while (idx < _knots.Count && _knots[idx].In < @in) idx++;
        double lo2 = idx > 0 ? _knots[idx - 1].Out : MinEv;
        double hi2 = idx < _knots.Count ? _knots[idx].Out : MaxEv;
        _knots.Insert(idx, new StretchKnot(@in, Math.Clamp(@out, lo2, hi2)));
        return idx;
    }

    private static double ClampEv(double ev) => Math.Clamp(ev, MinEv, MaxEv);

    private double ClampLo(int i) => i > 0 ? _knots[i - 1].Out : MinEv;
    private double ClampHi(int i) => i < _knots.Count - 1 ? _knots[i + 1].Out : MaxEv;

    public double Map(double ev)
    {
        if (_knots.Count == 0) return ev;
        if (ev <= MinEv) return ClampEv(ev);
        if (ev >= MaxEv) return ClampEv(ev);
        double pIn = MinEv, pOut = MinEv;
        foreach (var k in _knots)
        {
            if (ev < k.In)
            {
                double d = k.In - pIn;
                return d <= 1e-9 ? pOut : pOut + (k.Out - pOut) * ((ev - pIn) / d);
            }
            pIn = k.In;
            pOut = k.Out;
        }
        double dl = MaxEv - pIn;
        return dl <= 1e-9 ? pOut : pOut + (MaxEv - pOut) * ((ev - pIn) / dl);
    }

    public double Inverse(double evOut)
    {
        if (_knots.Count == 0) return evOut;
        if (evOut <= MinEv) return ClampEv(evOut);
        if (evOut >= MaxEv) return ClampEv(evOut);
        double pIn = MinEv, pOut = MinEv;
        foreach (var k in _knots)
        {
            if (evOut < k.Out)
            {
                double d = k.Out - pOut;
                return d <= 1e-9 ? k.In : pIn + (k.In - pIn) * ((evOut - pOut) / d);
            }
            pIn = k.In;
            pOut = k.Out;
        }
        double dl = MaxEv - pOut;
        return dl <= 1e-9 ? pIn : pIn + (MaxEv - pIn) * ((evOut - pOut) / dl);
    }

    public ManualStretchMap Clone()
    {
        var c = new ManualStretchMap();
        c._knots.AddRange(_knots);
        return c;
    }

    public string MapKey
    {
        get
        {
            if (_knots.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var k in _knots)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(k.In.ToString("0.####", CultureInfo.InvariantCulture));
                sb.Append(':');
                sb.Append(k.Out.ToString("0.####", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    public override string ToString()
    {
        if (_knots.Count == 0) return "neutral";
        var sb = new StringBuilder();
        foreach (var k in _knots)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append($"{k.In:0.###}->{k.Out:0.###}EV");
        }
        return sb.ToString();
    }
}