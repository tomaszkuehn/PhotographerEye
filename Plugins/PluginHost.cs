using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgViewer.Imaging;
using ImgViewer.Plugins;

namespace ImgViewer;

/// <summary>IPluginHost given to one plugin. Validates and namespaces everything it registers.</summary>
internal sealed class PluginHostImpl : IPluginHost
{
    private readonly LoadedPlugin _p;
    private string? _dataDir;
    internal bool Sealed;
    internal HostEventsImpl EventsImpl { get; } = new();

    public PluginHostImpl(LoadedPlugin p)
    {
        _p = p;
    }

    public int ApiVersion => PluginApi.Version;
    public string HostVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0";

    public string DataDirectory
    {
        get
        {
            if (_dataDir is null)
            {
                var safe = string.Concat(_p.Id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                _dataDir = Path.Combine(PluginManager.PluginDataRoot, safe);
                Directory.CreateDirectory(_dataDir);
            }
            return _dataDir;
        }
    }

    public bool GpuAvailable => PluginImaging.GpuAvailable;
    public IHostEvents Events => EventsImpl;
    public IImagingServices Imaging => PluginImaging.Instance;

    public void Log(string message) => Debug.Log($"PLUGIN [{_p.Id}]: {message}");

    private string CheckId(string? id, string what, IEnumerable<string> existing)
    {
        if (Sealed) throw new InvalidOperationException("register only inside Initialize");
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(what + " id is empty");
        id = id.Trim();
        if (id.Contains(':') || id.Contains('/')) throw new ArgumentException($"{what} id '{id}' must not contain ':' or '/'");
        if (existing.Contains(id)) throw new ArgumentException($"duplicate {what} id '{id}'");
        return id;
    }

    private Registered<T> Wrap<T>(string id, T d, int index) => new()
    {
        PluginId = _p.Id,
        PluginName = _p.Name,
        FullId = _p.Id + ":" + id,
        LocalId = id,
        D = d,
        Owner = _p,
        Index = index
    };

    public void AddFilter(FilterDescriptor filter)
    {
        if (filter is null) throw new ArgumentNullException(nameof(filter));
        var id = CheckId(filter.Id, "filter", _p.Filters.Select(f => f.LocalId));
        if (filter.Render is null) throw new ArgumentException($"filter '{id}' has no Render");
        filter.Id = id;
        filter.Params ??= Array.Empty<ParamDef>();
        var ids = new HashSet<string>();
        foreach (var d in filter.Params)
            if (d is null || string.IsNullOrWhiteSpace(d.Id) || !ids.Add(d.Id) || d.Id.Contains('/'))
                throw new ArgumentException($"filter '{id}': invalid or duplicate parameter id");
        if (string.IsNullOrWhiteSpace(filter.Group)) filter.Group = "Plugins";
        if (string.IsNullOrWhiteSpace(filter.Label)) filter.Label = id;
        _p.Filters.Add(Wrap(id, filter, _p.Filters.Count));
    }

    public void AddMaskProvider(MaskProviderDescriptor provider)
    {
        var id = CheckId(provider?.Id, "mask", _p.Masks.Select(f => f.LocalId));
        if (provider!.CreateMask is null) throw new ArgumentException($"mask '{id}' has no CreateMask");
        provider.Id = id;
        provider.Params ??= Array.Empty<ParamDef>();
        _p.Masks.Add(Wrap(id, provider, _p.Masks.Count));
    }

    public void AddImporter(ImporterDescriptor importer)
    {
        var id = CheckId(importer?.Id, "importer", _p.Importers.Select(f => f.LocalId));
        if (importer!.Load is null) throw new ArgumentException($"importer '{id}' has no Load");
        importer.Id = id;
        importer.Extensions = (importer.Extensions ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => (e.StartsWith(".") ? e : "." + e).ToLowerInvariant())
            .ToArray();
        if (importer.Extensions.Count == 0) throw new ArgumentException($"importer '{id}' has no extensions");
        _p.Importers.Add(Wrap(id, importer, _p.Importers.Count));
    }

    public void AddExporter(ExporterDescriptor exporter)
    {
        var id = CheckId(exporter?.Id, "exporter", _p.Exporters.Select(f => f.LocalId));
        if (exporter!.Export is null) throw new ArgumentException($"exporter '{id}' has no Export");
        exporter.Id = id;
        exporter.Options ??= Array.Empty<ParamDef>();
        _p.Exporters.Add(Wrap(id, exporter, _p.Exporters.Count));
    }

    public void AddMetadataProvider(MetadataProviderDescriptor provider)
    {
        var id = CheckId(provider?.Id, "metadata provider", _p.MetaProviders.Select(f => f.LocalId));
        if (provider!.Provide is null) throw new ArgumentException($"metadata provider '{id}' has no Provide");
        provider.Id = id;
        _p.MetaProviders.Add(Wrap(id, provider, _p.MetaProviders.Count));
    }

    public void AddCommand(CommandDescriptor command)
    {
        var id = CheckId(command?.Id, "command", _p.Commands.Select(f => f.LocalId));
        if (command!.Execute is null) throw new ArgumentException($"command '{id}' has no Execute");
        command.Id = id;
        if (string.IsNullOrWhiteSpace(command.Label)) command.Label = id;
        _p.Commands.Add(Wrap(id, command, _p.Commands.Count));
    }

    public void AddPreset(PresetDescriptor preset)
    {
        var id = CheckId(preset?.Id, "preset", _p.Presets.Select(f => f.LocalId));
        preset!.Id = id;
        if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = id;
        preset.Values ??= new Dictionary<string, double>();
        preset.Text ??= new Dictionary<string, string>();
        preset.Active ??= new List<string>();
        _p.Presets.Add(Wrap(id, preset, _p.Presets.Count));
    }
}

internal sealed class HostEventsImpl : IHostEvents
{
    public event EventHandler<ImageEventArgs>? ImageOpened;
    public event EventHandler<SaveEventArgs>? BeforeSave;
    public event EventHandler<SaveEventArgs>? AfterSave;
    public event EventHandler<SaveEventArgs>? AfterExport;

    private static void Safe(LoadedPlugin p, string what, Action a)
    {
        if (p.Faulted) return;
        try
        {
            a();
        }
        catch (Exception ex)
        {
            PluginManager.Instance.ReportFailure(p, "event " + what, ex);
        }
    }

    public void RaiseImageOpened(LoadedPlugin p, ImageEventArgs e) { var h = ImageOpened; if (h is not null) Safe(p, "ImageOpened", () => h(this, e)); }
    public void RaiseBeforeSave(LoadedPlugin p, SaveEventArgs e) { var h = BeforeSave; if (h is not null) Safe(p, "BeforeSave", () => h(this, e)); }
    public void RaiseAfterSave(LoadedPlugin p, SaveEventArgs e) { var h = AfterSave; if (h is not null) Safe(p, "AfterSave", () => h(this, e)); }
    public void RaiseAfterExport(LoadedPlugin p, SaveEventArgs e) { var h = AfterExport; if (h is not null) Safe(p, "AfterExport", () => h(this, e)); }
}

/// <summary>Host imaging services for plugins (and built-ins). GPU blur when available.</summary>
public sealed class PluginImaging : IImagingServices
{
    public static PluginImaging Instance { get; } = new();

    public static bool GpuAvailable
    {
        get
        {
            try { return Adjust16.GpuEnabled && Adjust16.GpuAvailable; }
            catch (Exception) { return false; }
        }
    }

    public float[] GaussianBlur(float[] plane, int width, int height, float sigma)
    {
        if (plane is null) throw new ArgumentNullException(nameof(plane));
        int n = width * height;
        if (sigma <= 0.3f) return (float[])plane.Clone();
        var radii = BoxRadii(sigma, 3);
        if (Adjust16.TryBoxBlurGpu(plane, width, height, radii, out var gpu) && gpu is not null) return gpu;
        var a = new float[n];
        Array.Copy(plane, a, n);
        var b = new float[n];
        foreach (var r in radii)
        {
            BoxH(a, b, width, height, r);
            BoxV(b, a, width, height, r);
        }
        return a;
    }

    /// <summary>Radii of n box blurs approximating a gaussian (Kovesi).</summary>
    public static int[] BoxRadii(double sigma, int n)
    {
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4 * wl - 4);
        int m = (int)Math.Round(mIdeal);
        var r = new int[n];
        for (int i = 0; i < n; i++) r[i] = ((i < m ? wl : wu) - 1) / 2;
        return r;
    }

    private static void BoxH(float[] src, float[] dst, int w, int h, int r)
    {
        if (r <= 0) { Array.Copy(src, dst, w * h); return; }
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            float acc = 0;
            for (int k = -r; k <= r; k++) acc += src[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = acc * inv;
                acc += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)];
            }
        });
    }

    private static void BoxV(float[] src, float[] dst, int w, int h, int r)
    {
        if (r <= 0) { Array.Copy(src, dst, w * h); return; }
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, w, x =>
        {
            float acc = 0;
            for (int k = -r; k <= r; k++) acc += src[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = acc * inv;
                acc += src[Math.Min(y + r + 1, h - 1) * w + x] - src[Math.Max(y - r, 0) * w + x];
            }
        });
    }

    public PluginImage Resize(PluginImage image, int maxLongSide)
    {
        int w = image.Width, h = image.Height;
        int ls = Math.Max(w, h);
        if (maxLongSide <= 0 || ls <= maxLongSide) return image;
        double s = (double)maxLongSide / ls;
        int nw = Math.Max(1, (int)Math.Round(w * s));
        int nh = Math.Max(1, (int)Math.Round(h * s));
        var dst = new PluginImage(nw, nh);
        double fx = (double)w / nw, fy = (double)h / nh;
        Parallel.For(0, nh, y =>
        {
            int y0 = (int)(y * fy), y1 = Math.Max(y0 + 1, Math.Min(h, (int)((y + 1) * fy)));
            for (int x = 0; x < nw; x++)
            {
                int x0 = (int)(x * fx), x1 = Math.Max(x0 + 1, Math.Min(w, (int)((x + 1) * fx)));
                double r = 0, g = 0, b = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int row = yy * w;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        r += image.R[row + xx];
                        g += image.G[row + xx];
                        b += image.B[row + xx];
                    }
                }
                double inv = 1.0 / ((y1 - y0) * (x1 - x0));
                int o = y * nw + x;
                dst.R[o] = (float)(r * inv);
                dst.G[o] = (float)(g * inv);
                dst.B[o] = (float)(b * inv);
            }
        });
        return dst;
    }

    public static BitmapSource ToBitmap(PluginImage img)
    {
        int w = img.Width, h = img.Height;
        var buf = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, o = i * 4;
                buf[o] = To8(img.B[i]);
                buf[o + 1] = To8(img.G[i]);
                buf[o + 2] = To8(img.R[i]);
                buf[o + 3] = 255;
            }
        });
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
        bmp.Freeze();
        return bmp;
    }

    public static byte To8(float v) => (byte)(v <= 0f ? 0 : v >= 1f ? 255 : (int)(v * 255f + 0.5f));

    public byte[] EncodeJpeg(PluginImage image, int quality)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        enc.Frames.Add(BitmapFrame.Create(ToBitmap(image)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public byte[] EncodePng(PluginImage image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(ToBitmap(image)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public float SrgbToLinear(float v) => SrgbToLinearF(v);
    public float LinearToSrgb(float v) => LinearToSrgbF(v);

    public static float SrgbToLinearF(float v)
    {
        if (v <= 0.04045f) return v / 12.92f;
        return MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    public static float LinearToSrgbF(float v)
    {
        if (v <= 0.0031308f) return v * 12.92f;
        return 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }
}
