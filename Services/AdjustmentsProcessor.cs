using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Imaging;

public sealed record HistogramData(int[] Lum, int[] R, int[] G, int[] B);

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
        Apply(source, shadowLift, gamma, out _, autoStretch);

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma) =>
        Apply(source, shadowLift, gamma, out _, autoStretch: true);

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma, out HistogramData? histogram, bool autoStretch = true)
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
        if (stretch is not null) ApplyStretch(buffer, stretch);

        if (shadow) ApplyShadow(buffer, shadowLift);
        if (hasGamma) ApplyGamma(buffer, gamma);

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

    private static unsafe void ApplyStretch(byte[] buffer, byte[] st)
    {
        float lo = st[0];
        float scale = 255.0f / Math.Max(1, st[1] - st[0]);
        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    int o = (int)((q[c] - lo) * scale + 0.5f);
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