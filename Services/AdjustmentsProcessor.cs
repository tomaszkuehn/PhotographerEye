using System;
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
    private static readonly float[] MonoDocX = { 0.00f, 0.05f, 0.15f, 0.30f, 0.50f, 0.70f, 0.85f, 0.95f, 1.00f };
    private static readonly float[] MonoDocY = { 0.013f, 0.045f, 0.105f, 0.275f, 0.520f, 0.765f, 0.905f, 0.978f, 1.000f };
    private static readonly float[] MonoTriXx = { 0.00f, 0.03f, 0.12f, 0.30f, 0.50f, 0.70f, 0.85f, 0.94f, 1.00f };
    private static readonly float[] MonoTriXy = { 0.008f, 0.032f, 0.098f, 0.300f, 0.545f, 0.800f, 0.930f, 0.985f, 1.000f };

    public static bool IsNeutral(double shadowLift, double gamma) =>
        Math.Abs(shadowLift) <= 0.0001 && Math.Abs(gamma - 1.0) < 0.0005;

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma) =>
        Apply(source, shadowLift, gamma, null, out _);

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma, out HistogramData? histogram)
        => Apply(source, shadowLift, gamma, null, out histogram);

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma, ManualStretchMap? manual, out HistogramData? histogram, MonoMode mono = MonoMode.None, int grainSeed = 0, PhotoAdjustments? extra = null)
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

        if (extra is not null && !extra.IsDefault)
        {
            if (extra.Denoise > 0.0005) PhotoOps.ApplyDenoise(buffer, w, h, extra.Denoise);
            if (extra.ColorNoise > 0.0005) PhotoOps.ApplyChromaDenoise(buffer, w, h, extra.ColorNoise);
            PhotoOps.ApplyColorPoints(buffer, extra);
            if (Math.Abs(extra.Highlights) > 0.0005) PhotoOps.ApplyRegionCurve(buffer, extra.Highlights, highlight: true);
            if (shadow) PhotoOps.ApplyRegionCurve(buffer, shadowLift, highlight: false);
            if (Math.Abs(extra.Clarity) > 0.0005) PhotoOps.ApplyClarity(buffer, w, h, extra.Clarity);
            if (extra.Sharpen > 0.0005) PhotoOps.ApplySharpen(buffer, w, h, extra.Sharpen);
            PhotoOps.ApplyTone(buffer, extra, manual);
        }
        else if (shadow || (manual is { HasEffect: true }))
        {
            if (shadow) PhotoOps.ApplyRegionCurve(buffer, shadowLift, highlight: false);
            if (manual is { HasEffect: true }) PhotoOps.ApplyTone(buffer, new PhotoAdjustments(), manual);
        }

        if (hasGamma) ApplyGamma(buffer, gamma);
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