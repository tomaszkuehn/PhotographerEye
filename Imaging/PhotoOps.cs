using System;

namespace ImgViewer.Imaging;

public sealed class PhotoAdjustments
{
    public double Exposure;
    public double Contrast;
    public double Temperature;
    public double Saturation = 1.0;
    public double Clarity;
    public double Sharpen;
    public double Highlights;
    public double Whites;
    public double Blacks;
    public double Dehaze;
    public double Denoise;
    public double ColorNoise;

    public bool IsDefault =>
        Math.Abs(Exposure) < 0.0005 &&
        Math.Abs(Contrast) < 0.0005 &&
        Math.Abs(Temperature) < 0.0005 &&
        Math.Abs(Saturation - 1.0) < 0.005 &&
        Math.Abs(Clarity) < 0.0005 &&
        Math.Abs(Sharpen) < 0.0005 &&
        Math.Abs(Highlights) < 0.0005 &&
        Math.Abs(Whites) < 0.0005 &&
        Math.Abs(Blacks) < 0.0005 &&
        Math.Abs(Dehaze) < 0.0005 &&
        Math.Abs(Denoise) < 0.0005 &&
        Math.Abs(ColorNoise) < 0.0005;

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
            Dehaze = dehaze / 100.0,
            Denoise = denoise / 100.0,
            ColorNoise = colorNoise / 100.0
        };
}

public static class PhotoOps
{
    public static (double R, double G, double B) TempGains(double t)
    {
        double g = 1.0 + 0.25 * t;
        return (g, 1.0, 1.0 - 0.25 * t);
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
        var (gR, gG, gB) = TempGains(a.Temperature);
        return (BuildChannelLut(gR, a.Dehaze), BuildChannelLut(gG, a.Dehaze), BuildChannelLut(gB, a.Dehaze));
    }

    private static float[] BuildToneLutCached(double contrast, double whites, double blacks, bool scene) =>
        ToneLutCache.GetOrAdd((scene, contrast, whites, blacks), _ =>
            scene ? BuildToneLutScene(contrast, whites, blacks) : BuildToneLutDisplay(contrast, whites, blacks));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(bool, double, double, double), float[]> ToneLutCache = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(bool, double, double, double, string), float[]> ToneStretchCache = new();

    public static float[] GetToneLut(double contrast, double whites, double blacks, bool scene, ManualStretchMap? manual = null)
    {
        float[] baseLut = BuildToneLutCached(Math.Clamp(contrast, -1.0, 1.0), Math.Clamp(whites, -1.0, 1.0), Math.Clamp(blacks, -1.0, 1.0), scene);
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

    public static double WbTemperatureFromSample(double mR, double mG, double mB)
    {
        if (mR < 1.0 || mG < 1.0 || mB < 1.0) return 0.0;
        double gR = mG / mR;
        double gB = mG / mB;
        double t = ((gR - 1.0) + (1.0 - gB)) / 0.5;
        return Math.Clamp(t, -1.0, 1.0);
    }

    private static byte ClampByte(float v)
    {
        int i = (int)(v + 0.5f);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }

    public static unsafe void ApplyChromaDenoise(byte[] buffer, int w, int h, double colorNoise)
    {
        double n = Math.Clamp(colorNoise, 0.0, 1.0);
        if (n <= 0.0005) return;
        int r = n < 0.34 ? 1 : (n < 0.67 ? 2 : 3);
        float blend = (float)(0.15 + 0.65 * n);

        var temp = new byte[buffer.Length];
        var blurred = new byte[buffer.Length];
        BoxBlurBgrPass(buffer, temp, w, h, r, vertical: false);
        BoxBlurBgrPass(temp, blurred, w, h, r, vertical: true);

        var lum0 = new float[w * h];
        var lumB = new float[w * h];
        ComputeLuma(buffer, lum0, w, h);
        ComputeLuma(blurred, lumB, w, h);

        fixed (byte* p = buffer)
        fixed (byte* bp = blurred)
        fixed (float* l0 = lum0, lb = lumB)
        {
            byte* end = p + buffer.Length;
            float* a = l0;
            float* b = lb;
            for (byte* q = p, s = bp; q < end; q += 4, s += 4, a++, b++)
            {
                for (int c = 0; c < 3; c++)
                {
                    float corrected = *a + (s[c] - *b);
                    q[c] = MixByte(q[c], (int)Math.Round(corrected), blend);
                }
            }
        }
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

    public static unsafe void ApplyDenoise(byte[] buffer, int w, int h, double denoise)
    {
        double n = Math.Clamp(denoise, 0.0, 1.0);
        if (n <= 0.0005) return;
        int r = n < 0.34 ? 1 : (n < 0.67 ? 2 : 3);
        float blend = (float)(0.15 + 0.55 * n);

        var temp = new byte[buffer.Length];
        var blurred = new byte[buffer.Length];
        BoxBlurBgrPass(buffer, temp, w, h, r, vertical: false);
        BoxBlurBgrPass(temp, blurred, w, h, r, vertical: true);

        fixed (byte* p = buffer)
        fixed (byte* bp = blurred)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p, s = bp; q < end; q += 4, s += 4)
            {
                q[0] = MixByte(q[0], s[0], blend);
                q[1] = MixByte(q[1], s[1], blend);
                q[2] = MixByte(q[2], s[2], blend);
            }
        }
    }

    public static unsafe void ApplyRegionCurve(byte[] buffer, double strength, bool highlight)
    {
        double v = Math.Clamp(strength, -1.0, 1.0);
        if (Math.Abs(v) <= 0.0005) return;

        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                float lr = q[2];
                float lg = q[1];
                float lb = q[0];
                float lumEnc = (0.2126f * lr + 0.7152f * lg + 0.0722f * lb) / 255.0f;
                if (lumEnc <= 0.0f) continue;
                float yl = MathF.Pow(lumEnc, 2.2f);
                float ev = MathF.Log2(yl / 0.18f);
                float mask = highlight ? SmoothEV(-1.0f, 2.8f, ev) : 1.0f - SmoothEV(-4.8f, 0.3f, ev);
                if (mask <= 0.0f) continue;
                float delta = (float)v * 1.7f * mask * (highlight ? 1.0f : MathF.Sqrt(mask));
                float g = MathF.Pow(2.0f, delta);
                q[2] = ClampByte(lr * g);
                q[1] = ClampByte(lg * g);
                q[0] = ClampByte(lb * g);
            }
        }
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

    private static byte MixByte(int a, int b, float t)
    {
        int v = (int)(a + (b - a) * t + 0.5f);
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

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

    private static unsafe void BoxBlurBgrPass(byte[] src, byte[] dst, int w, int h, int r, bool vertical)
    {
        fixed (byte* srcP = src, dstP = dst)
        {
            int window = 2 * r + 1;
            if (!vertical)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = srcP + y * w * 4;
                    byte* outRow = dstP + y * w * 4;
                    long s0 = 0, s1 = 0, s2 = 0;
                    for (int x = -r; x <= r; x++)
                    {
                        byte* v = row + ClampI(x, 0, w - 1) * 4;
                        s0 += v[0]; s1 += v[1]; s2 += v[2];
                    }
                    for (int x = 0; x < w; x++)
                    {
                        outRow[x * 4 + 0] = (byte)(s0 / window);
                        outRow[x * 4 + 1] = (byte)(s1 / window);
                        outRow[x * 4 + 2] = (byte)(s2 / window);
                        byte* add = row + ClampI(x + r + 1, 0, w - 1) * 4;
                        byte* sub = row + ClampI(x - r, 0, w - 1) * 4;
                        s0 += add[0] - sub[0];
                        s1 += add[1] - sub[1];
                        s2 += add[2] - sub[2];
                    }
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    long s0 = 0, s1 = 0, s2 = 0;
                    for (int y = -r; y <= r; y++)
                    {
                        byte* v = srcP + ClampI(y, 0, h - 1) * w * 4 + x * 4;
                        s0 += v[0]; s1 += v[1]; s2 += v[2];
                    }
                    for (int y = 0; y < h; y++)
                    {
                        byte* o = dstP + y * w * 4 + x * 4;
                        o[0] = (byte)(s0 / window);
                        o[1] = (byte)(s1 / window);
                        o[2] = (byte)(s2 / window);
                        byte* add = srcP + ClampI(y + r + 1, 0, h - 1) * w * 4 + x * 4;
                        byte* sub = srcP + ClampI(y - r, 0, h - 1) * w * 4 + x * 4;
                        s0 += add[0] - sub[0];
                        s1 += add[1] - sub[1];
                        s2 += add[2] - sub[2];
                    }
                }
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