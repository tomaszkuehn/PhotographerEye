using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Imaging;

public static class AdjustmentsProcessor
{
    public static bool IsNeutral(double shadowLift, double gamma) =>
        shadowLift <= 0.0001 && Math.Abs(gamma - 1.0) < 0.0005;

    public static WriteableBitmap? Apply(BitmapSource source, double shadowLift, double gamma)
    {
        BitmapSource baseSource = source;
        if (baseSource.Format != PixelFormats.Bgr32 && baseSource.Format != PixelFormats.Bgra32)
        {
            baseSource = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            baseSource.Freeze();
        }

        int w = baseSource.PixelWidth;
        int h = baseSource.PixelHeight;
        if (w <= 0 || h <= 0) return null;

        int stride = w * 4;
        var buffer = new byte[(long)stride * h];
        baseSource.CopyPixels(buffer, stride, 0);

        bool shadow = shadowLift > 0.0001;
        bool hasGamma = Math.Abs(gamma - 1.0) >= 0.0005;
        if (shadow) ApplyShadow(buffer, shadowLift);
        if (hasGamma) ApplyGamma(buffer, gamma);

        var result = new WriteableBitmap(w, h, 96, 96, baseSource.Format, null);
        result.WritePixels(new Int32Rect(0, 0, w, h), buffer, stride, 0);
        result.Freeze();
        return result;
    }

    private static unsafe void ApplyShadow(byte[] buffer, double strength)
    {
        var weight = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double x = 1.0 - i / 255.0;
            weight[i] = x * x * x * x;
        }

        fixed (byte* p = buffer)
        {
            byte* end = p + buffer.Length;
            for (byte* q = p; q < end; q += 4)
            {
                double lum = (0.0722 * q[0] + 0.7152 * q[1] + 0.2126 * q[2]) / 255.0;
                double wl = weight[Math.Min(255, (int)(lum * 255.0 + 0.5))];
                double lumNew = lum + strength * wl * (1.0 - lum);
                if (lum <= 0.004)
                {
                    byte gray = (byte)Math.Clamp(lumNew * 255.0 + 0.5, 0, 255);
                    q[0] = gray;
                    q[1] = gray;
                    q[2] = gray;
                    continue;
                }
                double scale = lumNew / lum;
                q[0] = (byte)Math.Min(255.0, q[0] * scale + 0.5);
                q[1] = (byte)Math.Min(255.0, q[1] * scale + 0.5);
                q[2] = (byte)Math.Min(255.0, q[2] * scale + 0.5);
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