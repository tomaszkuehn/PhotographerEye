using System;
using System.Threading.Tasks;

namespace ImgViewer.Imaging;

public sealed class PhotoAdjustments
{
    public double Exposure;
    public double Contrast;
    public double Temperature;
    public double Tint;
    public double Saturation = 1.0;
    public double Clarity;
    public double Sharpen;
    public double Highlights;
    public double Whites;
    public double Blacks;
    public double Dehaze;
    public double Denoise;
    public double ColorNoise;
    /// <summary>Separate R/G/B curves from the histogram (display space), null when unused.</summary>
    public RgbCurves? Curves;

    public bool IsDefault =>
        Math.Abs(Exposure) < 0.0005 &&
        Math.Abs(Contrast) < 0.0005 &&
        Math.Abs(Temperature) < 0.0005 &&
        Math.Abs(Tint) < 0.0005 &&
        Math.Abs(Saturation - 1.0) < 0.005 &&
        Math.Abs(Clarity) < 0.0005 &&
        Math.Abs(Sharpen) < 0.0005 &&
        Math.Abs(Highlights) < 0.0005 &&
        Math.Abs(Whites) < 0.0005 &&
        Math.Abs(Blacks) < 0.0005 &&
        Math.Abs(Dehaze) < 0.0005 &&
        Math.Abs(Denoise) < 0.0005 &&
        Math.Abs(ColorNoise) < 0.0005 &&
        (Curves is null || !Curves.HasEffect);

    /// <summary>
    /// Dehaze slider position (-100..100) to effect strength in the same units: 4 gives 1 and 8 gives 2 (linear in between),
    /// above 8 the strength grows quadratically (slope continuous at 8) so 100 still gives the full 100; symmetric for negatives.
    /// </summary>
    public static double DehazeCurve(double pos)
    {
        double p = Math.Min(Math.Abs(pos), 100.0);
        double v = p <= 8.0 ? p / 4.0 : 2.0 + 0.25 * (p - 8.0) + 75.0 * Math.Pow((p - 8.0) / 92.0, 2);
        return Math.Sign(pos) * v;
    }

    public static PhotoAdjustments FromUi(double exposure, double contrast, double temperature, double saturation, double clarity, double sharpen, double highlights, double whites, double blacks, double dehaze, double denoise, double colorNoise) =>
        new()
        {
            Exposure = exposure,
            Contrast = contrast / 100.0,
            Temperature = temperature / 100.0,
            Saturation = 1.0 + saturation / 100.0,
            Clarity = clarity / 100.0,
            Sharpen = sharpen / 100.0,
            Highlights = highlights / 100.0,
            Whites = whites / 100.0,
            Blacks = blacks / 100.0,
            Dehaze = DehazeCurve(dehaze) / 100.0,
            Denoise = denoise / 100.0,
            ColorNoise = colorNoise / 100.0
        };
}

public static class PhotoOps
{
    // White balance in linear light. Temperature t (-1..1, + = warmer) is a log R/B half-ratio along a cubic curve:
    // fine control near 0, wide range at the ends (R/B ratio up to e^4 ~ 55x end to end, covering candle .. shade).
    // Tint u (-1..1, + = magenta) scales green. Gains are normalized to unit luminance so exposure stays put.
    private const double WbLin = 0.25, WbCub = 0.75;
    private const double TintLin = 0.15, TintCub = 0.45;

    public static double WbLog(double t) { t = Math.Clamp(t, -1.0, 1.0); return WbLin * t + WbCub * t * t * t; }
    public static double TintLog(double u) { u = Math.Clamp(u, -1.0, 1.0); return TintLin * u + TintCub * u * u * u; }

    public static (double R, double G, double B) WbGains(double temperature, double tint)
    {
        double l = WbLog(temperature), m = TintLog(tint);
        double r = Math.Exp(l), g = Math.Exp(-m), b = Math.Exp(-l);
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return (r / y, g / y, b / y);
    }

    public static (double R, double G, double B) WbGains(PhotoAdjustments a) => WbGains(a.Temperature, a.Tint);

    public static (double R, double G, double B) TempGains(double t) => WbGains(t, 0.0);

    /// <summary>
    /// Temperature and tint (-1..1) that make a sampled linear-light color neutral. The sample must come from the image
    /// before white balance; returns false when the color cannot be neutralized within range (clamped result still returned).
    /// </summary>
    public static bool WbFromLinearSample(double r, double g, double b, out double temperature, out double tint)
    {
        temperature = 0; tint = 0;
        if (r <= 1e-6 || g <= 1e-6 || b <= 1e-6) return false;
        // Neutral: l + ln r = -m + ln g = -l + ln b.
        double l = (Math.Log(b) - Math.Log(r)) / 2.0;
        double m = Math.Log(g) - (Math.Log(r) + Math.Log(b)) / 2.0;
        temperature = InvertCubic(l, WbLin, WbCub);
        tint = InvertCubic(m, TintLin, TintCub);
        return Math.Abs(l) <= WbLin + WbCub + 1e-9 && Math.Abs(m) <= TintLin + TintCub + 1e-9;
    }

    private static double InvertCubic(double v, double a, double c)
    {
        if (v >= a + c) return 1.0;
        if (v <= -(a + c)) return -1.0;
        double lo = -1, hi = 1;
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            if (a * mid + c * mid * mid * mid < v) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    public const double ToneGrey = 0.18;
    public const int ToneEvN = 4096;
    public const double ToneEvMin = -14.0;
    public const double ToneEvMax = 10.0;

    private static float SmoothEV(float e0, float e1, float x)
    {
        float t = (x - e0) / (e1 - e0);
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }

    private static double Smooth(double t)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        return t * t * (3.0 - 2.0 * t);
    }

    public static double LumToEv(double lumLin) => Math.Log2(Math.Max(lumLin, 1e-10) / ToneGrey);

    public static double EvGainValue(double ev) => Math.Pow(2.0, ev);

    public static bool ToneNeutral(double contrast, double whites, double blacks) =>
        Math.Abs(contrast) <= 0.0005 && Math.Abs(whites) <= 0.0005 && Math.Abs(blacks) <= 0.0005;

    public static double ToneLutEv(double ev, float[] lut)
    {
        double greyMin = ToneGrey * Math.Pow(2.0, ToneEvMin);
        if (ev < ToneEvMin)
        {
            return lut[0] * ToneGrey * Math.Pow(2.0, ev) / greyMin;
        }
        double f = Math.Clamp((ev - ToneEvMin) / (ToneEvMax - ToneEvMin), 0.0, 1.0) * (lut.Length - 1);
        int i = (int)f;
        if (i >= lut.Length - 1) return lut[lut.Length - 1];
        double t = f - i;
        return lut[i] + (lut[i + 1] - lut[i]) * t;
    }

    public static float[] BuildToneLutScene(double contrast, double whites, double blacks)
    {
        var lut = new float[ToneEvN];
        double c = Math.Clamp(contrast, -1.0, 1.0);
        double w = Math.Clamp(whites, -1.0, 1.0);
        double b = Math.Clamp(blacks, -1.0, 1.0);
        double slope = c >= 0.0 ? 1.0 + 0.55 * c : 1.0 + 0.4 * c;
        double whiteEv = 2.9 - 1.6 * w;
        double wl = ToneGrey * Math.Pow(2.0, whiteEv);
        double pre = 1.0 + ToneGrey / wl;
        for (int i = 0; i < ToneEvN; i++)
        {
            double ev = ToneEvMin + (ToneEvMax - ToneEvMin) * i / (ToneEvN - 1);
            double y = ToneGrey * Math.Pow(2.0, ev * slope) * pre;
            double o = y * (1.0 + y / (wl * wl)) / (1.0 + y);
            o = o > 1.0 ? 1.0 : o;
            if (b < 0.0)
            {
                double k = -b * 0.035;
                double e = 0.004;
                double Soft(double v) => ((v - k) + Math.Sqrt((v - k) * (v - k) + e * e)) * 0.5;
                o = (Soft(o) - Soft(0.0)) / (Soft(1.0) - Soft(0.0));
            }
            else if (b > 0.0)
            {
                double k = b * 0.03;
                o = k + (1.0 - k) * o;
            }
            lut[i] = (float)o;
        }
        return lut;
    }

    public static float[] BuildToneLutDisplay(double contrast, double whites, double blacks)
    {
        var lut = new float[ToneEvN];
        double c = Math.Clamp(contrast, -1.0, 1.0);
        double w = Math.Clamp(whites, -1.0, 1.0);
        double b = Math.Clamp(blacks, -1.0, 1.0);
        double m = Math.Pow(ToneGrey, 1.0 / 2.2);
        for (int i = 0; i < ToneEvN; i++)
        {
            double ev = ToneEvMin + (ToneEvMax - ToneEvMin) * i / (ToneEvN - 1);
            double y = ToneGrey * Math.Pow(2.0, ev);
            double p = Math.Pow(y, 1.0 / 2.2);
            if (p <= 1.0)
            {
                p += c * 0.35 * (p - m) * (1.0 - Math.Pow(2.0 * p - 1.0, 2));
                double up = Smooth((p - 0.45) / 0.55);
                p += w * 0.12 * up * (1.0 - 0.5 * p);
                double lo = 1.0 - Smooth(p / 0.45);
                p += b * 0.07 * lo * Math.Min(2.0 * p, 1.0);
            }
            else
            {
                p += w * 0.12 * 0.5;
            }
            double o = Math.Pow(Math.Max(p, 0.0), 2.2);
            lut[i] = (float)(o < 0.0 ? 0.0 : o > 1.0 ? 1.0 : o);
        }
        return lut;
    }

    public static float SatFactor(PhotoAdjustments a)
    {
        double d = Math.Clamp(a.Dehaze, -1.0, 1.0);
        return (float)Math.Clamp(a.Saturation * (1.0 + 0.25 * d), 0.0, 2.5);
    }

    public static double DehazeLin(double lin, double dehaze)
    {
        if (dehaze > 0.0005)
        {
            double a = 0.25 * dehaze;
            return (lin - a) / (1.0 - a);
        }
        if (dehaze < -0.0005)
        {
            double a = 0.35 * -dehaze;
            return lin * (1.0 - a) + a * 0.85;
        }
        return lin;
    }

    private static byte[] BuildChannelLut(double tempGain, double dehaze)
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double lin = Math.Pow(i / 255.0, 2.2);
            double v = DehazeLin(tempGain * lin, dehaze);
            double enc = v <= 0.0 ? 0.0 : Math.Pow(Math.Max(v, 0.0), 1.0 / 2.2);
            lut[i] = (byte)Math.Clamp(Math.Round(enc * 255.0), 0, 255);
        }
        return lut;
    }

    public static (byte[] R, byte[] G, byte[] B) BuildChannelLuts(PhotoAdjustments a)
    {
        var (gR, gG, gB) = WbGains(a);
        return (BuildChannelLut(gR, a.Dehaze), BuildChannelLut(gG, a.Dehaze), BuildChannelLut(gB, a.Dehaze));
    }

    private static float[] BuildToneLutCached(double contrast, double whites, double blacks, bool scene) =>
        ToneLutCache.GetOrAdd((scene, contrast, whites, blacks), _ =>
            scene ? BuildToneLutScene(contrast, whites, blacks) : BuildToneLutDisplay(contrast, whites, blacks));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(bool, double, double, double), float[]> ToneLutCache = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(bool, double, double, double, string), float[]> ToneStretchCache = new();

    public static float[] GetToneLut(double contrast, double whites, double blacks, bool scene, ManualStretchMap? manual = null, CameraToneCurve? cameraTone = null)
    {
        float[] baseLut = scene && cameraTone is not null
            ? cameraTone.GetSceneLut(contrast, whites, blacks)
            : BuildToneLutCached(Math.Clamp(contrast, -1.0, 1.0), Math.Clamp(whites, -1.0, 1.0), Math.Clamp(blacks, -1.0, 1.0), scene);
        if (manual is null || !manual.HasEffect) return baseLut;
        double c = Math.Clamp(contrast, -1.0, 1.0);
        double w = Math.Clamp(whites, -1.0, 1.0);
        double b = Math.Clamp(blacks, -1.0, 1.0);
        string key = manual.MapKey;
        return ToneStretchCache.GetOrAdd((scene, c, w, b, key), _ =>
        {
            var lut = new float[baseLut.Length];
            for (int i = 0; i < lut.Length; i++)
            {
                double o = baseLut[i];
                if (o <= 1e-7)
                {
                    continue;
                }
                double ev = Math.Log2(Math.Min(o, 1.0) / ToneGrey);
                double ev2 = manual.Map(Math.Clamp(ev, ToneEvMin, ToneEvMax));
                lut[i] = ev2 <= ToneEvMin ? 0f : (float)(ToneGrey * Math.Pow(2.0, Math.Min(ev2, ToneEvMax)));
            }
            return lut;
        });
    }

    public static double SrgbDecode(double u) =>
        u <= 0.04045 ? u / 12.92 : Math.Pow((u + 0.055) / 1.055, 2.4);

    public static double DisplayFractionToEv(double u, double gamma)
    {
        double e = Math.Pow(Math.Clamp(u, 0.0, 1.0), Math.Max(gamma, 0.05));
        double lin = SrgbDecode(e);
        if (lin <= 1e-10) return ToneEvMin;
        return Math.Clamp(Math.Log2(lin / ToneGrey), ToneEvMin, ToneEvMax);
    }

    public static double EvToDisplayFraction(double ev, double gamma)
    {
        double lin = ToneGrey * Math.Pow(2.0, Math.Clamp(ev, ToneEvMin, ToneEvMax));
        if (lin <= 0.0) return 0.0;
        if (lin >= 1.0) return 1.0;
        double e = lin <= 0.0031308 ? lin * 12.92 : 1.055 * Math.Pow(lin, 1.0 / 2.4) - 0.055;
        double d = Math.Pow(e, 1.0 / Math.Max(gamma, 0.05));
        return Math.Clamp(d, 0.0, 1.0);
    }

    public static (double Exposure, double Highlights, double Shadows, double Whites, double Blacks) KnotsToSliders(IReadOnlyList<StretchKnot> knots)
    {
        double exp = 0.0, hl = 0.0, sh = 0.0, wh = 0.0, bl = 0.0;
        if (knots is not null)
        {
            foreach (var k in knots)
            {
                double d = k.Out - k.In;
                if (k.In >= 2.2) wh += d / 1.6;
                else if (k.In >= 0.8) hl += d / (1.7 * MaskHigh(k.In));
                else if (k.In > -0.8) exp += d;
                else if (k.In > -3.0) sh += d / (1.7 * Math.Sqrt(MaskShadow(k.In)));
                else bl += d / 1.2;
            }
        }
        return (Math.Clamp(exp, -5.0, 5.0),
                Math.Clamp(hl, -1.0, 1.0) * 100.0,
                Math.Clamp(sh, -1.0, 1.0) * 100.0,
                Math.Clamp(wh, -1.0, 1.0) * 100.0,
                Math.Clamp(bl, -1.0, 1.0) * 100.0);
    }

    private static double MaskHigh(double ev)
    {
        double t = (ev + 1.0) / 3.8;
        return Smooth01(t);
    }

    private static double MaskShadow(double ev)
    {
        double t = (ev + 4.8) / 5.1;
        return 1.0 - Smooth01(t);
    }

    private static double Smooth01(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    public static unsafe void ApplyColorPoints(byte[] buffer, PhotoAdjustments a)
    {
        var luts = BuildChannelLuts(a);
        byte[] lutR = luts.R, lutG = luts.G, lutB = luts.B;
        float sat = SatFactor(a);

        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                float b = lutB[q[0]];
                float g = lutG[q[1]];
                float r = lutR[q[2]];
                if (sat != 1.0f)
                {
                    float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    b = lum + (b - lum) * sat;
                    g = lum + (g - lum) * sat;
                    r = lum + (r - lum) * sat;
                }
                q[0] = ClampByte(b);
                q[1] = ClampByte(g);
                q[2] = ClampByte(r);
            }
        }
    }

    public static unsafe void ApplyTone(byte[] buffer, PhotoAdjustments a, ManualStretchMap? manual = null)
    {
        double ev = Math.Clamp(a.Exposure, -5.0, 5.0);
        bool manualActive = manual is { HasEffect: true };
        bool toneFlat = Math.Abs(ev) <= 0.0005 && ToneNeutral(a.Contrast, a.Whites, a.Blacks);
        if (toneFlat && !manualActive) return;
        float gain = (float)Math.Pow(2.0, ev);

        if (toneFlat)
        {
            float[] lut0 = GetToneLut(a.Contrast, a.Whites, a.Blacks, scene: false, manual);

            var linLut0 = new float[256];
            for (int i = 0; i < 256; i++) linLut0[i] = (float)Math.Pow(i / 255.0, 2.2);
            const int encN0 = 4097;
            var encLut0 = new float[encN0];
            for (int i = 0; i <= 4096; i++) encLut0[i] = (float)Math.Pow(i / 4096.0, 1.0 / 2.2);

            fixed (byte* p = buffer)
            {
                byte* end = p + buffer.Length;
                for (byte* q = p; q < end; q += 4)
                {
                    int lr = q[2];
                    int lg = q[1];
                    int lb = q[0];
                    float yl = 0.2126f * linLut0[lr] + 0.7152f * linLut0[lg] + 0.0722f * linLut0[lb];
                    float o = ToneLutEvF(yl, lut0);
                    float scale = o <= 0.0f ? 0.0f : o / MathF.Max(yl, 1e-7f);
                    q[2] = ClampByte(enc(lr, linLut0, encLut0, scale) * 255f);
                    q[1] = ClampByte(enc(lg, linLut0, encLut0, scale) * 255f);
                    q[0] = ClampByte(enc(lb, linLut0, encLut0, scale) * 255f);
                }
            }
            return;
        }

        float[] lut = GetToneLut(a.Contrast, a.Whites, a.Blacks, scene: false, manual);

        var linLut = new float[256];
        for (int i = 0; i < 256; i++) linLut[i] = (float)Math.Pow(i / 255.0, 2.2);
        const int encN = 4097;
        var encLut = new float[encN];
        for (int i = 0; i <= 4096; i++) encLut[i] = (float)Math.Pow(i / 4096.0, 1.0 / 2.2);

        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                int lr = q[2];
                int lg = q[1];
                int lb = q[0];
                float yl = 0.2126f * linLut[lr] + 0.7152f * linLut[lg] + 0.0722f * linLut[lb];
                float yl2 = yl * gain;
                float o = ToneLutEvF(yl2, lut);
                float scale = o <= 0.0f ? 0.0f : o / MathF.Max(yl, 1e-7f);
                q[2] = ClampByte(enc(lr, linLut, encLut, scale) * 255f);
                q[1] = ClampByte(enc(lg, linLut, encLut, scale) * 255f);
                q[0] = ClampByte(enc(lb, linLut, encLut, scale) * 255f);
            }
        }
    }

    private static float enc(int v, float[] linLut, float[] encLut, float scale)
    {
        float lin = linLut[v] * scale;
        float f = lin * 4096f;
        int idx = (int)f;
        if (idx < 0) return 0f;
        if (idx >= encLut.Length - 1) return 1f;
        float t = f - idx;
        return encLut[idx] + (encLut[idx + 1] - encLut[idx]) * t;
    }

    public static float ToneLutEvF(float ylLin, float[] lut)
    {
        double ev = Math.Log2(Math.Max(ylLin, 1e-10) / ToneGrey);
        if (ev < ToneEvMin)
        {
            return (float)(lut[0] * ylLin / (ToneGrey * Math.Pow(2.0, ToneEvMin)));
        }
        double f = Math.Clamp((ev - ToneEvMin) / (ToneEvMax - ToneEvMin), 0.0, 1.0) * (lut.Length - 1);
        int i = (int)f;
        if (i >= lut.Length - 1) return lut[lut.Length - 1];
        double t = f - i;
        return (float)(lut[i] + (lut[i + 1] - lut[i]) * t);
    }

    private static byte ClampByte(float v)
    {
        int i = (int)(v + 0.5f);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }

    private static unsafe void ComputeLuma(byte[] buffer, float[] lum, int w, int h)
    {
        fixed (byte* p = buffer)
        fixed (float* lp = lum)
        {
            float* l = lp;
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4, l++)
                *l = 0.2126f * q[2] + 0.7152f * q[1] + 0.0722f * q[0];
        }
    }

    public static void ApplyShadowsHighlights(byte[] buffer, int w, int h, double highlights, double shadows)
    {
        float hl = (float)Math.Clamp(highlights, -1.0, 1.0);
        float sh = (float)Math.Clamp(shadows, -1.0, 1.0);
        if (Math.Abs(hl) <= 0.0005f && Math.Abs(sh) <= 0.0005f) return;

        int s = ToneRegions.Factor(w, h);
        int lw = (w + s - 1) / s;
        int lh = (h + s - 1) / s;
        var lumEnc = new float[w * h];
        var logL = new float[w * h];
        var low = new float[lw * lh];
        Parallel.For(0, lh, ly =>
        {
            var acc = new float[lw];
            var cnt = new int[lw];
            int ye = Math.Min(h, (ly + 1) * s);
            for (int y = ly * s; y < ye; y++)
            {
                int o = y * w * 4;
                for (int x = 0; x < w; x++, o += 4)
                {
                    float le = (0.2126f * buffer[o + 2] + 0.7152f * buffer[o + 1] + 0.0722f * buffer[o]) / 255.0f;
                    float l = ToneRegions.LogLum(MathF.Pow(le, 2.2f));
                    lumEnc[y * w + x] = le;
                    logL[y * w + x] = l;
                    acc[x / s] += l;
                    cnt[x / s]++;
                }
            }
            for (int lx = 0; lx < lw; lx++) low[ly * lw + lx] = acc[lx] / Math.Max(cnt[lx], 1);
        });
        var grid = ToneRegions.Solve(low, lw, lh, s, w, h);

        Parallel.For(0, h, y =>
        {
            int o = y * w * 4;
            for (int x = 0; x < w; x++, o += 4)
            {
                float le = lumEnc[y * w + x];
                if (le <= 0.0f) continue;
                float d = ToneRegions.DeltaEv(ToneRegions.Base(grid, x, y, logL[y * w + x]), hl, sh);
                if (MathF.Abs(d) < 1e-4f) continue;
                // Gain acts on encoded values (display-referred strength), masks on linear EV.
                float t0 = ToneRegions.TargetLum(le, d);
                float scale = t0 / le;
                float r = buffer[o + 2] * scale;
                float g = buffer[o + 1] * scale;
                float b = buffer[o] * scale;
                float mx = MathF.Max(r, MathF.Max(g, b));
                if (mx > 255f)
                {
                    float o8 = MathF.Min(t0, 1.0f) * 255f;
                    float t = (mx - 255f) / MathF.Max(mx - o8, 1e-6f);
                    r += (o8 - r) * t;
                    g += (o8 - g) * t;
                    b += (o8 - b) * t;
                }
                buffer[o + 2] = ClampByte(r);
                buffer[o + 1] = ClampByte(g);
                buffer[o] = ClampByte(b);
            }
        });
    }

    public static unsafe void ApplySharpen(byte[] buffer, int w, int h, double sharpen)
    {
        double amt = Math.Clamp(sharpen, 0.0, 1.5);
        if (amt <= 0.0005) return;
        int r = Math.Clamp((int)Math.Round(Math.Min(w, h) * 0.0005), 1, 3);

        var lum = new float[w * h];
        ComputeLuma(buffer, lum, w, h);
        var low = new float[w * h];
        var low2 = new float[w * h];
        BoxBlurFloatPass(lum, low, w, h, r, vertical: false);
        BoxBlurFloatPass(low, low2, w, h, r, vertical: true);

        float a = (float)(amt * 1.15);
        fixed (byte* p = buffer)
        fixed (float* lp = lum, op = low2)
        {
            float* l = lp;
            float* o = op;
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4, l++, o++)
            {
                float delta = (*l - *o) * a;
                q[0] = ClampByte(q[0] + delta);
                q[1] = ClampByte(q[1] + delta);
                q[2] = ClampByte(q[2] + delta);
            }
        }
    }

    public static double ClarityRadius(int w, int h) =>
        Math.Clamp((int)Math.Round(Math.Min(w, h) * 0.012), 3, 60);

    public static unsafe void ApplyClarity(byte[] buffer, int w, int h, double clarity)
    {
        double amt = Math.Clamp(clarity, -1.0, 1.0);
        if (Math.Abs(amt) <= 0.0005) return;
        int r = (int)ClarityRadius(w, h);

        var lum = new float[w * h];
        ComputeLuma(buffer, lum, w, h);
        var low = new float[w * h];
        var low2 = new float[w * h];
        BoxBlurFloatPass(lum, low, w, h, r, vertical: false);
        BoxBlurFloatPass(low, low2, w, h, r, vertical: true);

        float a = (float)(1.5 * amt);
        fixed (byte* p = buffer)
        fixed (float* lp = lum, op = low2)
        {
            float* l = lp;
            float* o = op;
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4, l++, o++)
            {
                float v = *l / 255.0f;
                float mid = 1.0f - (2.0f * v - 1.0f) * (2.0f * v - 1.0f);
                float delta = (*l - *o) * a * (0.25f + 0.75f * mid);
                q[0] = ClampByte(q[0] + delta);
                q[1] = ClampByte(q[1] + delta);
                q[2] = ClampByte(q[2] + delta);
            }
        }
    }

    private static unsafe void BoxBlurFloatPass(float[] src, float[] dst, int w, int h, int r, bool vertical)
    {
        fixed (float* srcP = src, dstP = dst)
        {
            float wf = 2 * r + 1;
            if (!vertical)
            {
                for (int y = 0; y < h; y++)
                {
                    float* row = srcP + y * w;
                    float* outRow = dstP + y * w;
                    float s = 0;
                    for (int x = -r; x <= r; x++) s += row[ClampI(x, 0, w - 1)];
                    for (int x = 0; x < w; x++)
                    {
                        outRow[x] = s / wf;
                        s += row[ClampI(x + r + 1, 0, w - 1)] - row[ClampI(x - r, 0, w - 1)];
                    }
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int y = -r; y <= r; y++) s += srcP[ClampI(y, 0, h - 1) * w + x];
                    for (int y = 0; y < h; y++)
                    {
                        dstP[y * w + x] = s / wf;
                        s += srcP[ClampI(y + r + 1, 0, h - 1) * w + x] - srcP[ClampI(y - r, 0, h - 1) * w + x];
                    }
                }
            }
        }
    }

    private static int ClampI(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}