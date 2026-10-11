using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Services;

public readonly record struct ImageInfo(int Width, int Height, int Orientation)
{
    public Size DisplaySize => new(Width, Height);
    public bool RotatedQuarter => Orientation is 5 or 6 or 7 or 8;
}

public static class ImageLoader
{
    public static ImageInfo GetInfo(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        int w = frame.PixelWidth;
        int h = frame.PixelHeight;
        int orientation = 1;

        if (frame.Metadata is BitmapMetadata meta)
        {
            try
            {
                orientation = Convert.ToInt32(meta.GetQuery("/app1/ifd/{ushort=274}") ?? 1);
            }
            catch (Exception)
            {
                orientation = 1;
            }
        }

        if (orientation is 5 or 6 or 7 or 8)
        {
            (w, h) = (h, w);
        }

        return new ImageInfo(w, h, orientation);
    }

    public static BitmapSource LoadScaled(string path, ImageInfo info, int maxWidth, int maxHeight)
    {
        int rawW = info.RotatedQuarter ? info.Height : info.Width;
        int rawH = info.RotatedQuarter ? info.Width : info.Height;
        if (rawW <= 0 || rawH <= 0)
        {
            rawW = Math.Max(1, rawW);
            rawH = Math.Max(1, rawH);
        }

        double scale = Math.Min(1.0, Math.Min((double)maxWidth / info.Width, (double)maxHeight / info.Height));
        if (scale < 0.02) scale = 0.02;

        int dw = Math.Min(rawW, Math.Max(1, (int)Math.Round(rawW * scale)));
        int dh = Math.Min(rawH, Math.Max(1, (int)Math.Round(rawH * scale)));

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = dw;
        bmp.DecodePixelHeight = dh;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        BitmapSource result = bmp;
        var deg = info.Orientation switch
        {
            3 or 4 => 180.0,
            6 => 90.0,
            5 or 8 => 270.0,
            _ => 0.0
        };

        if (deg > 0)
        {
            result = ApplyRotation(bmp, deg);
        }

        return NormalizeDpi(result);
    }

    private static BitmapSource NormalizeDpi(BitmapSource source)
    {
        if (Math.Abs(source.DpiX - 96.0) < 0.01 && Math.Abs(source.DpiY - 96.0) < 0.01) return source;
        int w = source.PixelWidth;
        int h = source.PixelHeight;
        if (w <= 0 || h <= 0) return source;
        int stride = (w * source.Format.BitsPerPixel + 31) / 32 * 4;
        var buffer = new byte[(long)stride * h];
        source.CopyPixels(buffer, stride, 0);
        var normalized = BitmapSource.Create(w, h, 96, 96, source.Format, source.Palette, buffer, stride);
        normalized.Freeze();
        return normalized;
    }

    private static BitmapSource ApplyRotation(BitmapSource source, double degrees)
    {
        var t = new RotateTransform(degrees);
        t.Freeze();
        var rotated = new TransformedBitmap { Source = source, Transform = t };
        rotated.Freeze();
        return rotated;
    }
}