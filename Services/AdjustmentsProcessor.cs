using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Imaging;

public sealed record HistogramData(int[] Lum, int[] R, int[] G, int[] B);

public static class AdjustmentsProcessor
{
    public static bool IsNeutral(double shadowLift, double gamma) =>
        Math.Abs(shadowLift) <= 0.0001 && Math.Abs(gamma - 1.0) < 0.0005;

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma) =>
        Apply(source, shadowLift, gamma, out _);

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma, out HistogramData? histogram)
    {
        BitmapSource baseSource = source;
        if (baseSource.Format != PixelFormats.Bgr32 && baseSource.Format != PixelFormats.Bgra32)
        {
            baseSource = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            baseSource.Freeze();
        }

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
        if (shadow) ApplyShadow(buffer, shadowLift);
        if (hasGamma) ApplyGamma(buffer, gamma);

        histogram = ComputeFromBuffer(buffer, w);

        var result = new WriteableBitmap(w, h, 96, 96, baseSource.Format, null);
        result.WritePixels(new Int32Rect(0, 0, w, h), buffer, stride, 0);
        result.Freeze();
        return result;
    }

    public static HistogramData? ComputeHistogram(BitmapSource source)
    {
        BitmapSource src = source;
        if (src.Format != PixelFormats.Bgr32 && src.Format != PixelFormats.Bgra32)
        {
            src = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            src.Freeze();
        }
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
        float floorLift = strength > 0 ? (float)(0.02 * strength) : 0.0f;
        lift[0] = floorLift;
        if (strength >= 0)
        {
            for (int i = 1; i < 1024; i++)
            {
                float x = i / 1023.0f;
                lift[i] = MathF.Min(1.0f, floorLift + 1.0f - MathF.Pow(1.0f - x, exp));
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