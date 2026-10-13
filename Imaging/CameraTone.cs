using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Services;

namespace ImgViewer.Imaging;

public sealed class CameraToneCurve
{
    private double[] _x = { 0.0, 1.0 };
    private double[] _y = { 0.0, 1.0 };
    private float[]? _lut;

    public CameraToneCurve(double[][]? knots)
    {
        if (knots is null || knots.Length < 2) throw new ArgumentException("camera tone curve: bad knots");
        var xs = new double[knots.Length];
        var ys = new double[knots.Length];
        for (int i = 0; i < knots.Length; i++)
        {
            var k = knots[i];
            if (k is not { Length: 2 }) throw new ArgumentException("camera tone curve: bad knot");
            xs[i] = k[0];
            ys[i] = k[1];
        }
        Init(xs, ys);
    }

    public CameraToneCurve(double[] xs, double[] ys)
    {
        Init((double[])xs.Clone(), (double[])ys.Clone());
    }

    private void Init(double[] xs, double[] ys)
    {
        int n = Math.Min(xs.Length, ys.Length);
        var lx = new List<double>(n);
        var ly = new List<double>(n);
        for (int i = 0; i < n; i++)
        {
            double x = xs[i], y = ys[i];
            if (!IsFinite(x) || !IsFinite(y) || x <= 0.0 || x > 1.0 || y < 0.0 || y > 1.0) continue;
            if (lx.Count > 0 && x <= lx[lx.Count - 1] + 1e-9) continue;
            if (lx.Count > 0 && y < ly[ly.Count - 1] - 1e-9) continue;
            lx.Add(x);
            ly.Add(y);
        }
        _x = lx.ToArray();
        _y = ly.ToArray();
        if (_x.Length < 2) throw new ArgumentException("camera tone curve: needs 2 valid knots");
    }

    public int KnotCount => _x.Length;
    public double[] X => _x;
    public double[] Y => _y;

    public double[][] KnotPairs(int decimals = 5)
    {
        var res = new double[_x.Length][];
        for (int i = 0; i < _x.Length; i++)
            res[i] = new[] { Math.Round(_x[i], decimals), Math.Round(_y[i], decimals) };
        return res;
    }

    public CameraToneCurve Rounded()
    {
        var k = KnotPairs(5);
        return new CameraToneCurve(k);
    }

    public bool SameCurve(CameraToneCurve? other)
    {
        if (other is null || other._x.Length != _x.Length) return false;
        for (int i = 0; i < _x.Length; i++)
            if (Math.Abs(other._x[i] - _x[i]) > 1e-6 || Math.Abs(other._y[i] - _y[i]) > 1e-6) return false;
        return true;
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    public double Eval(double lum)
    {
        var x = _x;
        var y = _y;
        if (lum <= 0.0) return 0.0;
        if (lum <= x[0]) return y[0] * lum / x[0];
        int last = x.Length - 1;
        if (lum >= x[last])
        {
            double slope = (y[last] - y[last - 1]) / Math.Max(x[last] - x[last - 1], 1e-9);
            double v = y[last] + slope * (lum - x[last]);
            return v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
        }
        int lo = 0, hi = last;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (x[mid] <= lum) lo = mid; else hi = mid;
        }
        double t = (lum - x[lo]) / Math.Max(x[hi] - x[lo], 1e-12);
        return y[lo] + t * (y[hi] - y[lo]);
    }

    public float[] SceneLut
    {
        get
        {
            if (_lut is not null) return _lut;
            var lut = new float[PhotoOps.ToneEvN];
            for (int i = 0; i < lut.Length; i++)
            {
                double ev = PhotoOps.ToneEvMin + (PhotoOps.ToneEvMax - PhotoOps.ToneEvMin) * i / (PhotoOps.ToneEvN - 1);
                double yl = PhotoOps.ToneGrey * Math.Pow(2.0, ev);
                double o = Eval(yl);
                lut[i] = (float)(o < 0.0 ? 0.0 : o > 1.0 ? 1.0 : o);
            }
            _lut = lut;
            return lut;
        }
    }
}

public static class CameraToneFit
{
    private const int KnotBinsMax = 32;
    private const double AspectTolerance = 0.02;
    private const double ClipLevel = 0.995;
    private const double LowLevel = 0.003;
    private const int MinPairs = 256;
    private const double MinSpread = 0.02;
    private const double MinRank = 0.5;
    private const int ProxyWide = 96;

    public static CameraToneCurve? FitFromThumb(Raw16Image raw, byte[] jpeg, double baseEv, string label = "")
    {
        try
        {
            var (jw, jh, jb) = DecodeJpegProxy(jpeg);
            double arRaw = (double)raw.W / raw.H;
            double arJpg = (double)jw / jh;
            if (Math.Abs(arRaw - arJpg) / arRaw > AspectTolerance)
            {
                Debug.Log($"CAMERA TONE: {label} reject aspect raw={raw.W}x{raw.H} jpeg={jw}x{jh}");
                return null;
            }

            int pw = ProxyWide;
            int ph = Math.Clamp((int)Math.Round(pw * jh / (double)jw), 8, 128);
            var sp = BoxAvgRaw(raw, pw, ph);
            var jp = BoxAvgJpeg(jb, jw, jh, pw, ph);
            int n = sp.Length / 3;

            var scene = new double[n];
            var disp = new double[n];
            int pairs = 0;
            for (int i = 0; i < n; i++)
            {
                double r = sp[i * 3] / 65535.0, g = sp[i * 3 + 1] / 65535.0, b = sp[i * 3 + 2] / 65535.0;
                if (r >= ClipLevel || g >= ClipLevel || b >= ClipLevel) continue;
                double sl = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                if (sl <= LowLevel) continue;
                double lr = PhotoOps.SrgbDecode(jp[i * 3] / 255.0);
                double lg = PhotoOps.SrgbDecode(jp[i * 3 + 1] / 255.0);
                double lb = PhotoOps.SrgbDecode(jp[i * 3 + 2] / 255.0);
                if (lr >= ClipLevel || lg >= ClipLevel || lb >= ClipLevel) continue;
                double dl = 0.2126 * lr + 0.7152 * lg + 0.0722 * lb;
                if (dl <= LowLevel || dl >= ClipLevel) continue;
                scene[pairs] = sl;
                disp[pairs] = dl;
                pairs++;
            }
            if (pairs < MinPairs)
            {
                Debug.Log($"CAMERA TONE: {label} reject pairs={pairs}");
                return null;
            }
            double sSpread = PairQuantile(scene, disp, pairs, 0.95, true) - PairQuantile(scene, disp, pairs, 0.05, true);
            double dSpread = PairQuantile(scene, disp, pairs, 0.95, false) - PairQuantile(scene, disp, pairs, 0.05, false);
            if (sSpread < MinSpread || dSpread < MinSpread)
            {
                Debug.Log($"CAMERA TONE: {label} reject spread scene={sSpread:0.000} disp={dSpread:0.000}");
                return null;
            }
            double rank = Spearman(scene, disp, pairs);
            if (rank < MinRank)
            {
                Debug.Log($"CAMERA TONE: {label} reject spearman={rank:0.00}");
                return null;
            }

            var ts = new List<double>(pairs);
            var td = new List<double>(pairs);
            for (int i = 0; i < pairs; i++)
            {
                if (i % 3 == 2) continue;
                ts.Add(scene[i]);
                td.Add(disp[i]);
            }
            int tn = ts.Count;
            var tsorted = ts.ToArray();
            var tsortedD = td.ToArray();
            Array.Sort(tsorted, tsortedD);
            Array.Sort(tsortedD);

            var xsB = new List<double>(KnotBinsMax);
            var ysB = new List<double>(KnotBinsMax);
            for (int b = 0; b < KnotBinsMax; b++)
            {
                int from = b * tn / KnotBinsMax;
                int to = (b + 1) * tn / KnotBinsMax;
                if (to <= from) continue;
                int midS = (from + to) / 2;
                xsB.Add(tsorted[midS]);
                int midD = (to - from) / 2;
                ysB.Add(tsortedD[from + midD]);
            }
            if (xsB.Count < 2)
            {
                Debug.Log($"CAMERA TONE: {label} reject knots={xsB.Count}");
                return null;
            }
            var curve = new CameraToneCurve(xsB.ToArray(), ysB.ToArray()).Rounded();

            double evGain = Math.Pow(2.0, baseEv);
            float[] defLut = PhotoOps.BuildToneLutScene(0, 0, 0);
            double encFit = 0, encDef = 0, linFit = 0, linDef = 0;
            int c = 0;
            for (int i = 0; i < pairs; i++)
            {
                if (i % 3 != 2) continue;
                double sl = scene[i];
                double target = Math.Clamp(disp[i], 0.0, 1.0);
                double pFit = curve.Eval(sl);
                double pDef = PhotoOps.ToneLutEv(Math.Log2(Math.Max(sl * evGain, 1e-10) / PhotoOps.ToneGrey), defLut);
                double ef = Enc(pFit) - Enc(target);
                double ed = Enc(pDef) - Enc(target);
                encFit += ef * ef;
                encDef += ed * ed;
                linFit += (pFit - target) * (pFit - target);
                linDef += (pDef - target) * (pDef - target);
                c++;
            }
            encFit /= c; encDef /= c; linFit /= c; linDef /= c;
            bool ok = encFit < encDef && linFit <= linDef && encFit <= 0.01;
            Debug.Log($"CAMERA TONE: {label} knots={curve.KnotCount} pairs={pairs} test={c} spearman={rank:0.00} encMSE fit={encFit:0.00000} def={encDef:0.00000} linMSE fit={linFit:0.00000} def={linDef:0.00000} -> {(ok ? "ACCEPT" : "reject")}");
            return ok ? curve : null;
        }
        catch (Exception ex)
        {
            Debug.Log($"CAMERA TONE: {label} fit error {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static double PairQuantile(double[] scene, double[] disp, int n, double q, bool byScene)
    {
        var src = new double[n];
        for (int i = 0; i < n; i++) src[i] = byScene ? scene[i] : disp[i];
        Array.Sort(src);
        return src[Math.Clamp((int)Math.Round((n - 1) * q), 0, n - 1)];
    }

    private static double Enc(double v)
    {
        v = Math.Clamp(v, 0.0, 1.0);
        return v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
    }

    private static double Spearman(double[] scene, double[] disp, int n)
    {
        var rs = Ranks(scene, n);
        var rd = Ranks(disp, n);
        double mean = (n - 1) / 2.0;
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = rs[i] - mean, dy = rd[i] - mean;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        if (sxx <= 0 || syy <= 0) return 0.0;
        return sxy / Math.Sqrt(sxx * syy);
    }

    private static double[] Ranks(double[] v, int n)
    {
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => v[a].CompareTo(v[b]));
        var ranks = new double[n];
        int i2 = 0;
        while (i2 < n)
        {
            int j = i2;
            while (j + 1 < n && v[idx[j + 1]] == v[idx[i2]]) j++;
            double r = (i2 + j) / 2.0;
            for (int k = i2; k <= j; k++) ranks[idx[k]] = r;
            i2 = j + 1;
        }
        return ranks;
    }

    private static (int W, int H, byte[] Bgra) DecodeJpegProxy(byte[] jpeg)
    {
        using var ms = new MemoryStream(jpeg, writable: false);
        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var conv = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        conv.Freeze();
        int w = conv.PixelWidth, h = conv.PixelHeight;
        if (w <= 0 || h <= 0 || (long)w * h > 4_000_000) throw new InvalidOperationException($"bad jpeg thumb {w}x{h}");
        int stride = w * 4;
        var data = new byte[(long)stride * h];
        conv.CopyPixels(data, stride, 0);
        return (w, h, data);
    }

    private static double[] BoxAvgRaw(Raw16Image raw, int pw, int ph)
    {
        var outp = new double[pw * ph * 3];
        for (int by = 0; by < ph; by++)
        {
            int y0 = by * raw.H / ph;
            int y1 = Math.Max((by + 1) * raw.H / ph, y0 + 1);
            for (int bx = 0; bx < pw; bx++)
            {
                int x0 = bx * raw.W / pw;
                int x1 = Math.Max((bx + 1) * raw.W / pw, x0 + 1);
                double sr = 0, sg = 0, sb = 0;
                int cnt = 0;
                for (int y = y0; y < y1; y++)
                {
                    int o = y * raw.W;
                    for (int x = x0; x < x1; x++)
                    {
                        int p = o + x;
                        sr += raw.R[p];
                        sg += raw.G[p];
                        sb += raw.B[p];
                        cnt++;
                    }
                }
                int o2 = (by * pw + bx) * 3;
                outp[o2] = sr / cnt;
                outp[o2 + 1] = sg / cnt;
                outp[o2 + 2] = sb / cnt;
            }
        }
        return outp;
    }

    private static double[] BoxAvgJpeg(byte[] bgra, int w, int h, int pw, int ph)
    {
        var outp = new double[pw * ph * 3];
        for (int by = 0; by < ph; by++)
        {
            int y0 = by * h / ph;
            int y1 = Math.Max((by + 1) * h / ph, y0 + 1);
            for (int bx = 0; bx < pw; bx++)
            {
                int x0 = bx * w / pw;
                int x1 = Math.Max((bx + 1) * w / pw, x0 + 1);
                double sr = 0, sg = 0, sb = 0;
                int cnt = 0;
                for (int y = y0; y < y1; y++)
                {
                    int o = y * w * 4;
                    for (int x = x0; x < x1; x++)
                    {
                        int p = o + x * 4;
                        sr += bgra[p + 2];
                        sg += bgra[p + 1];
                        sb += bgra[p];
                        cnt++;
                    }
                }
                int o2 = (by * pw + bx) * 3;
                outp[o2] = sr / cnt;
                outp[o2 + 1] = sg / cnt;
                outp[o2 + 2] = sb / cnt;
            }
        }
        return outp;
    }
}