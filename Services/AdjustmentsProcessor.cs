using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Imaging;

public sealed record HistogramData(int[] Lum, int[] R, int[] G, int[] B);

public enum MonoMode
{
    None = 0,
    Documentary = 1,
    TriX = 2
}

public static class AdjustmentsProcessor
{
    private static readonly ConditionalWeakTable<BitmapSource, StretchEntry> StretchCache = new();

    private sealed class StretchEntry
    {
        public byte[]? Value;
    }

    public static bool IsNeutral(double shadowLift, double gamma) =>
        Math.Abs(shadowLift) <= 0.0001 && Math.Abs(gamma - 1.0) < 0.0005;

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma, bool autoStretch) =>
        Apply(source, shadowLift, gamma, null, out _, autoStretch);

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma) =>
        Apply(source, shadowLift, gamma, null, out _);

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma, out HistogramData? histogram, bool autoStretch = true)
        => Apply(source, shadowLift, gamma, null, out histogram, autoStretch);

    private static readonly float[] MonoDocX = { 0.00f, 0.05f, 0.15f, 0.30f, 0.50f, 0.70f, 0.85f, 0.95f, 1.00f };
    private static readonly float[] MonoDocY = { 0.013f, 0.045f, 0.105f, 0.275f, 0.520f, 0.765f, 0.905f, 0.978f, 1.000f };
    private static readonly float[] MonoTriXx = { 0.00f, 0.03f, 0.12f, 0.30f, 0.50f, 0.70f, 0.85f, 0.94f, 1.00f };
    private static readonly float[] MonoTriXy = { 0.008f, 0.032f, 0.098f, 0.300f, 0.545f, 0.800f, 0.930f, 0.985f, 1.000f };

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma, ManualStretchMap? manual, out HistogramData? histogram, bool autoStretch = true, MonoMode mono = MonoMode.None, int grainSeed = 0, PhotoAdjustments? extra = null)
    {
        BitmapSource baseSource = EnsureBgra(source);
        int w = baseSource.PixelWidth;
        int h = baseSource.PixelHeight;
        if (w <= 0 || h <= 0)
        {
            histogram = null;
            return null;
        }

        int stride = w * 4;
        var buffer = new byte[(long)stride * h];
        baseSource.CopyPixels(buffer, stride, 0);

        if (extra is not null && !extra.IsDefault)
        {
            if (extra.Denoise > 0.0005) PhotoOps.ApplyDenoise(buffer, w, h, extra.Denoise);
            PhotoOps.ApplyColorPoints(buffer, extra);
            if (Math.Abs(extra.Clarity) > 0.0005) PhotoOps.ApplyClarity(buffer, w, h, extra.Clarity);
        }

        bool shadow = Math.Abs(shadowLift) > 0.0001;
        bool hasGamma = Math.Abs(gamma - 1.0) >= 0.0005;

        byte[]? stretch = null;
        if (shadow && autoStretch)
        {
            var entry = StretchCache.GetOrCreateValue(source);
            stretch = entry.Value;
            if (stretch is null)
            {
                stretch = ComputeStretchFromBuffer(buffer);
                entry.Value = stretch;
            }
        }
        if (stretch is not null)
        {
            float k = (float)Math.Min(1.0, Math.Abs(shadowLift) / 0.5);
            ApplyStretch(buffer, stretch, k);
        }

        if (shadow) ApplyShadow(buffer, shadowLift);
        if (hasGamma) ApplyGamma(buffer, gamma);
        if (manual is not null && manual.HasEffect) ApplyManual(buffer, manual);
        switch (mono)
        {
            case MonoMode.Documentary:
                ApplyMonoCore(buffer, 0.325f, 0.605f, 0.070f, MonoDocX, MonoDocY);
                break;
            case MonoMode.TriX:
                ApplyMonoCore(buffer, 0.345f, 0.600f, 0.055f, MonoTriXx, MonoTriXy);
                ApplyGrain(buffer, w, grainSeed);
                break;
        }

        histogram = ComputeFromBuffer(buffer, w);

        var result = new WriteableBitmap(w, h, 96, 96, baseSource.Format, null);
        result.WritePixels(new Int32Rect(0, 0, w, h), buffer, stride, 0);
        result.Freeze();
        return result;
    }

    public static byte[]? ComputeAutoStretch(BitmapSource source)
    {
        BitmapSource baseSource = EnsureBgra(source);
        int w = baseSource.PixelWidth;
        int h = baseSource.PixelHeight;
        if (w <= 0 || h <= 0) return null;
        int stride = w * 4;
        var buffer = new byte[(long)stride * h];
        baseSource.CopyPixels(buffer, stride, 0);
        return ComputeStretchFromBuffer(buffer);
    }

    private static BitmapSource EnsureBgra(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgra32) return source;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static unsafe byte[]? ComputeStretchFromBuffer(byte[] buffer)
    {
        Span<int> hl = stackalloc int[256];
        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                hl[(int)(0.2126f * q[2] + 0.7152f * q[1] + 0.0722f * q[0] + 0.5f)]++;
            }
        }

        long n = buffer.Length / 4;
        long loT = (long)(n * 0.005);
        if (loT < 1) loT = 1;
        long hiT = n - loT;

        long cum = 0;
        byte loV = 0;
        for (int v = 0; v < 256; v++)
        {
            cum += hl[v];
            if (cum >= loT)
            {
                loV = (byte)v;
                break;
            }
        }
        cum = 0;
        byte hiV = 255;
        for (int v = 0; v < 256; v++)
        {
            cum += hl[v];
            if (cum > hiT)
            {
                hiV = (byte)(v - 1);
                break;
            }
        }

        if (!(hiV > loV && hiV - loV >= 16 && (loV > 0 || 255.0f / (hiV - loV) > 1.01f)))
            return null;
        return new byte[] { loV, hiV };
    }

    private static unsafe void ApplyStretch(byte[] buffer, byte[] st, float k)
    {
        float lo = st[0];
        float scale = 255.0f / Math.Max(1, st[1] - st[0]);
        float ik = 1.0f - k;
        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    float x = q[c];
                    float s = (x - lo) * scale;
                    if (s < 0.0f) s = 0.0f;
                    else if (s > 255.0f) s = 255.0f;
                    int o = (int)(x * ik + s * k + 0.5f);
                    q[c] = (byte)(o < 0 ? 0 : o > 255 ? 255 : o);
                }
            }
        }
    }

    public static HistogramData? ComputeHistogram(BitmapSource source)
    {
        BitmapSource src = EnsureBgra(source);
        int w = src.PixelWidth;
        int h = src.PixelHeight;
        if (w <= 0 || h <= 0) return null;
        int stride = w * 4;
        var buffer = new byte[(long)stride * h];
        src.CopyPixels(buffer, stride, 0);
        return ComputeFromBuffer(buffer, w);
    }

    private static unsafe HistogramData ComputeFromBuffer(byte[] buffer, int w)
    {
        var lum = new int[256];
        var r = new int[256];
        var g = new int[256];
        var b = new int[256];
        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                b[q[0]]++;
                g[q[1]]++;
                r[q[2]]++;
                lum[(int)((0.2126 * q[2] + 0.7152 * q[1] + 0.0722 * q[0]) + 0.5)]++;
            }
        }
        return new HistogramData(lum, r, g, b);
    }

    private static unsafe void ApplyShadow(byte[] buffer, double strength)
    {
        var lift = new float[1024];
        float exp = 1.0f + (float)(2.0 * Math.Abs(strength));
        lift[0] = 0.0f;
        if (strength >= 0)
        {
            for (int i = 1; i < 1024; i++)
            {
                float x = i / 1023.0f;
                lift[i] = 1.0f - MathF.Pow(1.0f - x, exp);
            }
        }
        else
        {
            for (int i = 1; i < 1024; i++)
            {
                float x = i / 1023.0f;
                lift[i] = MathF.Pow(x, exp);
            }
        }

        const float threshold = 0.25f;
        const float softness = 0.15f;
        float lower = threshold - softness;
        float upper = threshold + softness;
        float denom = upper - lower;

        var srgbLin = new float[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            srgbLin[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }

        var linSrgb = new byte[1024];
        for (int i = 0; i < 1024; i++)
        {
            double c = i / 1023.0;
            double s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            linSrgb[i] = (byte)Math.Clamp(Math.Round(s * 255.0), 0, 255);
        }

        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                float lr = srgbLin[q[2]];
                float lg = srgbLin[q[1]];
                float lb = srgbLin[q[0]];

                float lum = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
                float ramp = (upper - lum) / denom;
                if (ramp < 0.0f) ramp = 0.0f;
                else if (ramp > 1.0f) ramp = 1.0f;
                float mask = ramp * ramp;
                if (mask <= 0.0f) continue;

                float inv = 1.0f - mask;
                q[2] = linSrgb[(int)((lr * inv + lift[LiftIndex(lr)] * mask) * 1023.0f + 0.5f)];
                q[1] = linSrgb[(int)((lg * inv + lift[LiftIndex(lg)] * mask) * 1023.0f + 0.5f)];
                q[0] = linSrgb[(int)((lb * inv + lift[LiftIndex(lb)] * mask) * 1023.0f + 0.5f)];
            }
        }
    }

    private static int LiftIndex(float lin)
    {
        int i = (int)(lin * 1023.0f + 0.5f);
        return i < 0 ? 0 : (i > 1023 ? 1023 : i);
    }

    private static unsafe void ApplyManual(byte[] buffer, ManualStretchMap manual)
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double v = manual.Map(i / 255.0);
            lut[i] = (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);
        }

        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                q[0] = lut[q[0]];
                q[1] = lut[q[1]];
                q[2] = lut[q[2]];
            }
        }
    }

    private static unsafe void ApplyMonoCore(byte[] buffer, float wr, float wg, float wb, float[] ax, float[] ay)
    {
        var lut = new byte[256];
        int seg = 0;
        for (int i = 0; i < 256; i++)
        {
            float x = i / 255.0f;
            while (seg + 2 < ax.Length && x > ax[seg + 1]) seg++;
            float t = (x - ax[seg]) / (ax[seg + 1] - ax[seg]);
            if (t < 0.0f) t = 0.0f;
            else if (t > 1.0f) t = 1.0f;
            float v = ay[seg] + t * (ay[seg + 1] - ay[seg]);
            lut[i] = (byte)Math.Clamp(Math.Round(v * 255.0f), 0, 255);
        }

        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                float g = wr * q[2] + wg * q[1] + wb * q[0];
                int gi = (int)(g + 0.5f);
                if (gi < 0) gi = 0;
                else if (gi > 255) gi = 255;
                byte o = lut[gi];
                q[0] = o;
                q[1] = o;
                q[2] = o;
            }
        }
    }

    private static unsafe void ApplyGrain(byte[] buffer, int w, int seed)
    {
        int pixels = buffer.Length / 4;
        if (w <= 0 || pixels == 0) return;

        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            byte* q = p;
            for (int i = 0; i < pixels; i++, q += 4)
            {
                uint n = (uint)i * 1597334677u ^ (uint)seed;
                n = (n ^ (n >> 13)) * 1274126177u;
                n ^= n >> 16;
                uint n2 = (n ^ (n >> 11)) * 2654434677u;
                n2 ^= n2 >> 15;
                float t = ((n & 0xFFFF) / 65535.0f) - ((n2 & 0xFFFF) / 65535.0f);
                int gray = (q[0] + q[1] + q[2]) / 3;
                float fg = gray / 255.0f;
                float weight = 4.0f * fg * (1.0f - fg);
                int v = gray + (int)MathF.Round(t * 17.5f * weight);
                if (v < 0) v = 0;
                else if (v > 255) v = 255;
                byte o = (byte)v;
                q[0] = o;
                q[1] = o;
                q[2] = o;
            }
        }
    }

    private static unsafe void ApplyGamma(byte[] buffer, double gamma)
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double v = Math.Pow(i / 255.0, 1.0 / Math.Max(gamma, 0.05));
            lut[i] = (byte)Math.Clamp(v * 255.0 + 0.5, 0, 255);
        }

        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                q[0] = lut[q[0]];
                q[1] = lut[q[1]];
                q[2] = lut[q[2]];
            }
        }
    }
}