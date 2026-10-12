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

    public static double ToneMap(double x, double gain, double dehaze, double contrast, double whites)
    {
        double v = x * gain;
        if (dehaze > 0.0005)
        {
            double a = 0.25 * dehaze;
            v = (v - a) / (1.0 - a);
        }
        else if (dehaze < -0.0005)
        {
            double a = 0.35 * -dehaze;
            v = v * (1.0 - a) + a * 0.85;
        }
        if (contrast > 0.0001)
        {
            double k = 7.7 * contrast;
            double kn = Math.Tanh(k / 2.0);
            v = 0.5 + 0.5 * Math.Tanh(k * (v - 0.5)) / kn;
        }
        else if (contrast < -0.0001)
        {
            v = 0.5 + (v - 0.5) * (1.0 + 0.72 * contrast);
        }
        if (whites > 0.0005 || whites < -0.0005)
        {
            double m = WhRegion(v);
            v = whites > 0 ? v * (1.0 + 0.28 * whites * m) : v * (1.0 - 0.25 * -whites * m);
        }
        return v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
    }

    public static double WhRegion(double v) => Smooth((v - 0.50) / 0.28);

    private static double Smooth(double t)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        return t * t * (3.0 - 2.0 * t);
    }

    public static float SatFactor(PhotoAdjustments a)
    {
        double d = Math.Clamp(a.Dehaze, -1.0, 1.0);
        return (float)Math.Clamp(a.Saturation * (1.0 + 0.25 * d), 0.0, 2.5);
    }

    private static float Smooth01(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }

    public const float HighlightMaskLo = 0.78f;
    public const float HighlightMaskRange = 0.16f;
    public const float ShadowMaskLo = 0.78f;
    public const float ShadowMaskRange = 0.38f;
    public const float BlacksMaskLo = 0.28f;
    public const float BlacksMaskRange = 0.23f;

    public static float HighlightMask(float lum) => Smooth01((lum - HighlightMaskLo) / HighlightMaskRange);
    public static float ShadowMask(float lum) => Smooth01((ShadowMaskLo - lum) / ShadowMaskRange);
    public static float BlacksMask(float lum) => Smooth01((BlacksMaskLo - lum) / BlacksMaskRange);

    public static float HighlightCurve(float v, float h) =>
        h >= 0f ? v * (1f + 0.042f * h) : v * (1f - 0.078f * -h);

    public static float ShadowCurve(float v, float s) =>
        s >= 0f ? 1.0f - MathF.Pow(1.0f - v, 1.0f + 1.1f * s) : MathF.Pow(v, 1.0f + 0.83f * -s);

    public static float BlacksCurve(float v, float b)
    {
        if (b >= 0f)
        {
            float l = 0.135f * b;
            return l + (1f - l) * v;
        }
        float shift = 0.07f * -b * Smooth01((0.20f - v) / 0.20f);
        float r = v - shift;
        return r < 0f ? 0f : r;
    }

    public static float[] BuildRegionCurve(double strength, bool highlight)
    {
        var curve = new float[65536];
        float k = (float)Math.Clamp(strength, -1.0, 1.0);
        if (highlight)
            for (int i = 0; i < 65536; i++) curve[i] = HighlightCurve(i / 65535.0f, k);
        else
            for (int i = 0; i < 65536; i++) curve[i] = ShadowCurve(i / 65535.0f, k);
        return curve;
    }

    public static float[] BuildBlacksCurve(double blacks)
    {
        var curve = new float[65536];
        float k = (float)Math.Clamp(blacks, -1.0, 1.0);
        for (int i = 0; i < 65536; i++) curve[i] = BlacksCurve(i / 65535.0f, k);
        return curve;
    }

    public static double WbTemperatureFromSample(double mR, double mG, double mB)
    {
        if (mR < 1.0 || mG < 1.0 || mB < 1.0) return 0.0;
        double gR = mG / mR;
        double gB = mG / mB;
        double t = ((gR - 1.0) + (1.0 - gB)) / 0.5;
        return Math.Clamp(t, -1.0, 1.0);
    }

    public static void BuildChannelLuts(PhotoAdjustments a, out byte[] lutR, out byte[] lutG, out byte[] lutB)
    {
        var (gR, gG, gB) = TempGains(a.Temperature);
        double evGain = Math.Pow(2.0, a.Exposure);
        lutR = BuildChannelLut(gR * evGain, a.Dehaze, a.Contrast, a.Whites);
        lutG = BuildChannelLut(gG * evGain, a.Dehaze, a.Contrast, a.Whites);
        lutB = BuildChannelLut(gB * evGain, a.Dehaze, a.Contrast, a.Whites);
    }

    private static byte[] BuildChannelLut(double gain, double dehaze, double contrast, double whites)
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
            lut[i] = (byte)Math.Clamp(Math.Round(ToneMap(i / 255.0, gain, dehaze, contrast, whites) * 255.0), 0, 255);
        return lut;
    }

    public static unsafe void ApplyColorPoints(byte[] buffer, PhotoAdjustments a)
    {
        BuildChannelLuts(a, out byte[] lutR, out byte[] lutG, out byte[] lutB);
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

        var curve = BuildRegionCurve(v, highlight);
        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                float lr = q[2];
                float lg = q[1];
                float lb = q[0];
                float lum = (0.2126f * lr + 0.7152f * lg + 0.0722f * lb) / 255.0f;
                float mask = highlight ? HighlightMask(lum) : ShadowMask(lum);
                if (mask <= 0.0f) continue;
                float inv = 1.0f - mask;
                q[2] = ClampByte((lr / 255.0f * inv + curve[(int)(lr * 257.0f)] * mask) * 255.0f);
                q[1] = ClampByte((lg / 255.0f * inv + curve[(int)(lg * 257.0f)] * mask) * 255.0f);
                q[0] = ClampByte((lb / 255.0f * inv + curve[(int)(lb * 257.0f)] * mask) * 255.0f);
            }
        }
    }

    public static unsafe void ApplyBlacks(byte[] buffer, double blacks)
    {
        double v = Math.Clamp(blacks, -1.0, 1.0);
        if (Math.Abs(v) <= 0.0005) return;

        var curve = BuildBlacksCurve(v);
        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                float lr = q[2];
                float lg = q[1];
                float lb = q[0];
                float lum = (0.2126f * lr + 0.7152f * lg + 0.0722f * lb) / 255.0f;
                bool lift = v > 0.0;
                float mask = lift ? 1f : BlacksMask(lum);
                if (mask <= 0.0f) continue;
                float inv = 1.0f - mask;
                q[2] = ClampByte((lr / 255.0f * inv + curve[(int)(lr * 257.0f)] * mask) * 255.0f);
                q[1] = ClampByte((lg / 255.0f * inv + curve[(int)(lg * 257.0f)] * mask) * 255.0f);
                q[0] = ClampByte((lb / 255.0f * inv + curve[(int)(lb * 257.0f)] * mask) * 255.0f);
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