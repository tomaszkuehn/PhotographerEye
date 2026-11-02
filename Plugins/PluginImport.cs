using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>Bridges plugin importers into ImageLoader / ImageFolder.</summary>
public static class PluginImport
{
    private static readonly object CacheGate = new();
    private static (string Path, DateTime Stamp, int MaxSide, BitmapSource Bmp)? _last;

    public static Registered<ImporterDescriptor>? Find(string path)
    {
        try
        {
            var imp = PluginManager.Instance.Registry.FindImporter(path);
            return imp is { Owner.Faulted: true } ? null : imp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static ImageInfo GetInfo(Registered<ImporterDescriptor> imp, string path)
    {
        if (imp.D.Probe is not null)
        {
            try
            {
                var wh = imp.D.Probe(path);
                if (wh is { Length: >= 2 } && wh[0] > 0 && wh[1] > 0)
                {
                    PluginManager.ReportSuccess(imp.Owner);
                    return new ImageInfo(wh[0], wh[1], 1);
                }
            }
            catch (Exception ex)
            {
                PluginManager.Instance.ReportFailure(imp.Owner, $"{imp.FullId} Probe", ex);
            }
        }
        var bmp = Load(imp, path, 0);
        return new ImageInfo(bmp.PixelWidth, bmp.PixelHeight, 1);
    }

    /// <summary>Loads through the plugin; maxLongSide 0 = full size.</summary>
    public static BitmapSource Load(Registered<ImporterDescriptor> imp, string path, int maxLongSide)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (CacheGate)
        {
            if (_last is { } c && c.Path == path && c.Stamp == stamp && (c.MaxSide == 0 || (maxLongSide > 0 && c.MaxSide >= maxLongSide)))
                return Downscale(c.Bmp, maxLongSide);
        }
        ImportedImage img;
        try
        {
            img = imp.D.Load(path, maxLongSide) ?? throw new InvalidDataException("importer returned no image");
            int n = img.Width * img.Height;
            if (img.Width <= 0 || img.Height <= 0 || img.R is null || img.G is null || img.B is null ||
                img.R.Length < n || img.G.Length < n || img.B.Length < n)
                throw new InvalidDataException($"importer returned an invalid image {img.Width}x{img.Height}");
            PluginManager.ReportSuccess(imp.Owner);
        }
        catch (Exception ex)
        {
            PluginManager.Instance.ReportFailure(imp.Owner, $"{imp.FullId} Load", ex);
            throw new InvalidDataException($"{imp.PluginName}: {ex.Message}", ex);
        }
        var bmp = ToBitmap(img);
        lock (CacheGate) _last = (path, stamp, maxLongSide, bmp);
        return Downscale(bmp, maxLongSide);
    }

    public static BitmapSource ToBitmap(ImportedImage img)
    {
        int w = img.Width, h = img.Height;
        var buf = new byte[(long)w * h * 4];
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            int row = y * w;
            long o = (long)row * 4;
            for (int x = 0; x < w; x++, o += 4)
            {
                int i = row + x;
                float r = img.R[i], g = img.G[i], b = img.B[i];
                if (img.Linear)
                {
                    r = PluginImaging.LinearToSrgbF(Math.Clamp(r, 0f, 1f));
                    g = PluginImaging.LinearToSrgbF(Math.Clamp(g, 0f, 1f));
                    b = PluginImaging.LinearToSrgbF(Math.Clamp(b, 0f, 1f));
                }
                buf[o] = PluginPipeline.U8(b);
                buf[o + 1] = PluginPipeline.U8(g);
                buf[o + 2] = PluginPipeline.U8(r);
                buf[o + 3] = 255;
            }
        });
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource Downscale(BitmapSource src, int maxLongSide)
    {
        int ls = Math.Max(src.PixelWidth, src.PixelHeight);
        if (maxLongSide <= 0 || ls <= maxLongSide) return src;
        double k = (double)maxLongSide / ls;
        var t = new TransformedBitmap(src, new ScaleTransform(k, k));
        t.Freeze();
        return t;
    }
}
