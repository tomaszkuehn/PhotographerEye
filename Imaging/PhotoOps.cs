using System;

namespace ImgViewer.Imaging;

public sealed class PhotoAdjustments
{
    public double Exposure;
    public double Contrast;
    public double Temperature;
    public double Saturation = 1.0;
    public double Clarity;
    public double Dehaze;
    public double Denoise;

    public bool IsDefault =>
        Math.Abs(Exposure) < 0.0005 &&
        Math.Abs(Contrast) < 0.0005 &&
        Math.Abs(Temperature) < 0.0005 &&
        Math.Abs(Saturation - 1.0) < 0.005 &&
        Math.Abs(Clarity) < 0.0005 &&
        Math.Abs(Dehaze) < 0.0005 &&
        Math.Abs(Denoise) < 0.0005;
}

public static class PhotoOps
{
    public static void BuildChannelLuts(PhotoAdjustments a, out byte[] lutR, out byte[] lutG, out byte[] lutB)
    {
        double gainR = 1.0 + 0.30 * a.Temperature;
        double gainB = 1.0 - 0.30 * a.Temperature;
        double gainG = 1.0;
        if (a.Temperature < 0)
        {
            double k = -a.Temperature;
            gainR = 1.0 - 0.10 * k;
            gainB = 1.0 + 0.30 * k;
            gainG = 1.0 + 0.02 * k;
        }
        double evGain = Math.Pow(2.0, a.Exposure);
        double hazeA = 0.10 * Math.Clamp(a.Dehaze, 0.0, 1.0);

        lutR = BuildChannelLut(gainR * evGain, hazeA, a.Contrast);
        lutG = BuildChannelLut(gainG * evGain, hazeA, a.Contrast);
        lutB = BuildChannelLut(gainB * evGain, hazeA, a.Contrast);
    }

    private static byte[] BuildChannelLut(double gain, double hazeA, double contrast)
    {
        var lut = new byte[256];
        bool posC = contrast > 0.0001;
        bool negC = contrast < -0.0001;
        double k = 1.0 + Math.Max(contrast, 0.0) * 7.0;
        double kNorm = Math.Tanh(0.5 * k);
        double flatten = 1.0 + Math.Min(contrast, 0.0) * 0.55;

        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0 * gain;
            if (hazeA > 0.0) v = (v - hazeA) / (1.0 - hazeA);
            if (posC)
            {
                v = 0.5 + 0.5 * (Math.Tanh((v - 0.5) * k) / kNorm);
            }
            else if (negC)
            {
                v = 0.5 + (v - 0.5) * flatten;
            }
            lut[i] = (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);
        }
        return lut;
    }

    public static unsafe void ApplyColorPoints(byte[] buffer, PhotoAdjustments a)
    {
        BuildChannelLuts(a, out byte[] lutR, out byte[] lutG, out byte[] lutB);

        bool posC = a.Contrast > 0.0001;
        bool negC = a.Contrast < -0.0001;
        float dehazeBoost = (float)(Math.Clamp(a.Dehaze, 0.0, 1.0) * 0.30);
        float sat = (float)Math.Clamp(a.Saturation * (1.0 + dehazeBoost), 0.0, 3.0);

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

    private static byte MixByte(int a, int b, float t)
    {
        int v = (int)(a + (b - a) * t + 0.5f);
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    public static unsafe void ApplyClarity(byte[] buffer, int w, int h, double clarity)
    {
        double amt = Math.Clamp(clarity, -1.0, 1.0);
        if (Math.Abs(amt) <= 0.0005) return;
        int r = Math.Clamp((int)Math.Round(Math.Min(w, h) * 0.006), 2, 40);

        var lum = new float[w * h];
        fixed (byte* p = buffer)
        fixed (float* lp = lum)
        {
            float* l = lp;
            byte* srcEnd = p + buffer.Length;
            for (byte* q = p; q < srcEnd; q += 4, l++)
                *l = 0.2126f * q[2] + 0.7152f * q[1] + 0.0722f * q[0];
        }

        var low = new float[w * h];
        var low2 = new float[w * h];
        BoxBlurFloatPass(lum, low, w, h, r, vertical: false);
        BoxBlurFloatPass(low, low2, w, h, r, vertical: true);

        float a = (float)amt;
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

    private static int ClampI(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}