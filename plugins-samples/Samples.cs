using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.Samples;

internal static class U
{
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    public static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
    public static float Smooth(float t) { t = Clamp01(t); return t * t * (3 - 2 * t); }

    public static void Rgb2Hsv(float r, float g, float b, out float h, out float s, out float v)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        v = max;
        float d = max - min;
        s = max > 1e-6f ? d / max : 0f;
        if (d < 1e-6f) { h = 0; return; }
        if (max == r) h = 60f * (((g - b) / d) % 6f);
        else if (max == g) h = 60f * ((b - r) / d + 2f);
        else h = 60f * ((r - g) / d + 4f);
        if (h < 0) h += 360f;
    }

    public static float HueDistance(float a, float b)
    {
        float d = Math.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }
}

// ---------------------------------------------------------------------
//  1. Negative: toolbar toggle + command with a shortcut
// ---------------------------------------------------------------------

public sealed class NegativePlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.negative",
        Name = "Negative",
        Version = "2.0",
        Author = "PhotographerEye samples",
        Description = "Inverts the image. Toolbar toggle, also Ctrl+Shift+N."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddFilter(new FilterDescriptor
        {
            Id = "negative",
            Label = "Negative",
            Description = "Invert colors",
            Placement = FilterPlacement.Toolbar,
            Stage = FilterStage.Display,
            SupportsMask = true,
            Render = (img, p, ctx) =>
            {
                Parallel.For(0, img.Height, y =>
                {
                    int row = y * img.Width;
                    for (int i = row; i < row + img.Width; i++)
                    {
                        img.R[i] = 1f - img.R[i];
                        img.G[i] = 1f - img.G[i];
                        img.B[i] = 1f - img.B[i];
                    }
                });
            }
        });
        host.AddCommand(new CommandDescriptor
        {
            Id = "toggle",
            Label = "Toggle negative",
            Shortcut = "Ctrl+Shift+N",
            Placement = CommandPlacement.Menu,
            Execute = c =>
            {
                bool on = !c.IsActive("negative");
                c.SetActive("negative", on);
                c.SetStatus(on ? "Negative on" : "Negative off");
            }
        });
    }
}

// ---------------------------------------------------------------------
//  2. Black & white with red mix: slider + choice + toggle, preset
// ---------------------------------------------------------------------

public sealed class BwRedPlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.bwred",
        Name = "B&W red mix",
        Version = "2.0",
        Author = "PhotographerEye samples",
        Description = "Monochrome conversion with a red channel mix (red filter look), optional color key."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddFilter(new FilterDescriptor
        {
            Id = "bwred",
            Label = "B&W red mix",
            Group = "Black & white",
            Stage = FilterStage.Display,
            Params = new[]
            {
                ParamDef.Slider("mix", "Red mix", 0, 100, 0),
                ParamDef.Choice("keep", "Keep color", "none",
                    new ParamChoice("none", "None"),
                    new ParamChoice("red", "Reds"),
                    new ParamChoice("yellow", "Yellows"),
                    new ParamChoice("blue", "Blues")),
                ParamDef.Toggle("strong", "Strong contrast")
            },
            IsIdentity = p => p.Get("mix") <= 0,
            Render = Render
        });
        host.AddPreset(new PresetDescriptor
        {
            Id = "red-filter",
            Name = "Red filter B&W",
            Values = new Dictionary<string, double> { ["bwred/mix"] = 80, ["bwred/strong"] = 1, ["core.Contrast"] = 10 }
        });
    }

    private static void Render(PluginImage img, FilterParams p, RenderContext ctx)
    {
        float mix = (float)(p.Get("mix") / 100.0);
        bool strong = p.GetBool("strong");
        string keep = p.GetText("keep");
        float keepHue = keep == "red" ? 0f : keep == "yellow" ? 55f : keep == "blue" ? 215f : -1f;
        Parallel.For(0, img.Height, y =>
        {
            int row = y * img.Width;
            for (int i = row; i < row + img.Width; i++)
            {
                float r = img.R[i], g = img.G[i], b = img.B[i];
                float l = U.Luma(r, g, b);
                float m = l + (r - l) * mix;
                if (strong) m = U.Clamp01(0.5f + (m - 0.5f) * 1.35f);
                float k = 0f;
                if (keepHue >= 0)
                {
                    U.Rgb2Hsv(r, g, b, out var h, out var s, out _);
                    k = (1f - U.Smooth(U.HueDistance(h, keepHue) / 30f)) * U.Smooth(s * 3f);
                }
                img.R[i] = m + k * (r - m);
                img.G[i] = m + k * (g - m);
                img.B[i] = m + k * (b - m);
            }
        });
    }
}

// ---------------------------------------------------------------------
//  3. Color grade: two colors + curve, preset touching core sliders
// ---------------------------------------------------------------------

public sealed class ColorGradePlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.grade",
        Name = "Split-tone grade",
        Version = "2.0",
        Author = "PhotographerEye samples",
        Description = "Tints shadows and highlights with two colors and applies a tone curve."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddFilter(new FilterDescriptor
        {
            Id = "grade",
            Label = "Split-tone grade",
            Group = "Color",
            Stage = FilterStage.Display,
            Params = new[]
            {
                ParamDef.Slider("amount", "Amount", 0, 100, 0),
                ParamDef.Color("shadows", "Shadows", "#2E5C8A"),
                ParamDef.Color("highlights", "Highlights", "#F2A65A"),
                ParamDef.Slider("balance", "Balance", -100, 100, 0),
                ParamDef.Curve("curve", "Tone curve")
            },
            IsIdentity = p => p.Get("amount") <= 0 && p.GetCurve("curve").IsIdentity,
            Render = Render
        });
        host.AddPreset(new PresetDescriptor
        {
            Id = "cinematic",
            Name = "Cinematic teal/orange",
            Values = new Dictionary<string, double> { ["grade/amount"] = 55, ["core.Contrast"] = 12, ["core.Saturation"] = -8 },
            Text = new Dictionary<string, string> { ["grade/curve"] = "0,0.03;0.25,0.2;0.75,0.8;1,0.97" }
        });
    }

    private static void Render(PluginImage img, FilterParams p, RenderContext ctx)
    {
        float amount = (float)(p.Get("amount") / 100.0);
        p.GetColor("shadows", out var sr, out var sg, out var sb);
        p.GetColor("highlights", out var hr, out var hg, out var hb);
        float bal = (float)(p.Get("balance") / 100.0);
        var curve = p.GetCurve("curve");
        float[] lut = curve.IsIdentity ? null : curve.ToLut(4096);
        // Tints as offsets from their own luminance so the grade does not change brightness.
        float sl = U.Luma(sr, sg, sb), hl = U.Luma(hr, hg, hb);
        float sdr = sr - sl, sdg = sg - sl, sdb = sb - sl;
        float hdr = hr - hl, hdg = hg - hl, hdb = hb - hl;
        float pivot = 0.5f + 0.3f * bal;
        Parallel.For(0, img.Height, y =>
        {
            int row = y * img.Width;
            for (int i = row; i < row + img.Width; i++)
            {
                float r = img.R[i], g = img.G[i], b = img.B[i];
                if (lut != null)
                {
                    r = lut[(int)(U.Clamp01(r) * 4095f + 0.5f)];
                    g = lut[(int)(U.Clamp01(g) * 4095f + 0.5f)];
                    b = lut[(int)(U.Clamp01(b) * 4095f + 0.5f)];
                }
                if (amount > 0)
                {
                    float l = U.Luma(r, g, b);
                    float hw = U.Smooth((l - pivot + 0.35f) / 0.7f);
                    float sw = 1f - hw;
                    float a = amount * 0.6f;
                    r += a * (sw * sdr + hw * hdr);
                    g += a * (sw * sdg + hw * hdg);
                    b += a * (sw * sdb + hw * hdb);
                }
                img.R[i] = U.Clamp01(r);
                img.G[i] = U.Clamp01(g);
                img.B[i] = U.Clamp01(b);
            }
        });
    }
}

// ---------------------------------------------------------------------
//  4. Vignette: Output stage (post-crop), point parameter
// ---------------------------------------------------------------------

public sealed class VignettePlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.vignette",
        Name = "Post-crop vignette",
        Version = "2.0",
        Author = "PhotographerEye samples",
        Description = "Darkens or lightens the frame edges after crop. The center can be picked on the photo."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddFilter(new FilterDescriptor
        {
            Id = "vignette",
            Label = "Vignette",
            Group = "Effects",
            Stage = FilterStage.Output,
            SupportsMask = false,
            Params = new[]
            {
                ParamDef.Slider("amount", "Amount", -100, 100, 0),
                ParamDef.Slider("size", "Midpoint", 0, 100, 50),
                ParamDef.Slider("feather", "Feather", 0, 100, 50),
                ParamDef.Point("center", "Center")
            },
            IsIdentity = p => Math.Abs(p.Get("amount")) < 0.5,
            Render = (img, p, ctx) =>
            {
                float amount = (float)(p.Get("amount") / 100.0);
                double mid = 0.25 + 0.6 * p.Get("size") / 100.0;
                double feather = 0.05 + 0.9 * p.Get("feather") / 100.0;
                p.GetPoint("center", out var cx, out var cy);
                int w = img.Width, h = img.Height;
                double aspect = (double)w / h;
                Parallel.For(0, h, y =>
                {
                    int row = y * w;
                    double dy = ((y + 0.5) / h - cy) * 2;
                    for (int x = 0; x < w; x++)
                    {
                        double dx = ((x + 0.5) / w - cx) * 2 * Math.Sqrt(aspect);
                        double d = Math.Sqrt(dx * dx + dy * dy / Math.Sqrt(aspect) * Math.Sqrt(aspect) / aspect) / Math.Sqrt(2);
                        float t = U.Smooth((float)((d - mid + feather / 2) / feather));
                        int i = row + x;
                        if (amount < 0)
                        {
                            float k = 1f + amount * t;
                            img.R[i] *= k;
                            img.G[i] *= k;
                            img.B[i] *= k;
                        }
                        else
                        {
                            float k = amount * t;
                            img.R[i] += (1f - img.R[i]) * k;
                            img.G[i] += (1f - img.G[i]) * k;
                            img.B[i] += (1f - img.B[i]) * k;
                        }
                    }
                });
            }
        });
    }
}

// ---------------------------------------------------------------------
//  5. Netpbm importer (.ppm / .pgm, binary and ASCII, 8/16-bit)
// ---------------------------------------------------------------------

public sealed class NetpbmPlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.netpbm",
        Name = "Netpbm import",
        Version = "2.0",
        Author = "PhotographerEye samples",
        Description = "Opens .ppm and .pgm images (P2/P3/P5/P6, 8 or 16 bit)."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddImporter(new ImporterDescriptor
        {
            Id = "netpbm",
            Label = "Netpbm image",
            Extensions = new[] { ".ppm", ".pgm", ".pnm" },
            Probe = path =>
            {
                using (var fs = File.OpenRead(path))
                {
                    var hdr = ReadHeader(fs);
                    return new[] { hdr.W, hdr.H };
                }
            },
            Load = (path, max) => Load(path)
        });
    }

    private struct Header { public char Type; public int W, H, Max; }

    private static Header ReadHeader(Stream s)
    {
        string magic = Token(s);
        if (magic.Length != 2 || magic[0] != 'P' || "2356".IndexOf(magic[1]) < 0) throw new InvalidDataException("not a P2/P3/P5/P6 netpbm file");
        var h = new Header { Type = magic[1] };
        h.W = int.Parse(Token(s), CultureInfo.InvariantCulture);
        h.H = int.Parse(Token(s), CultureInfo.InvariantCulture);
        h.Max = int.Parse(Token(s), CultureInfo.InvariantCulture);
        if (h.W <= 0 || h.H <= 0 || h.Max <= 0 || h.Max > 65535) throw new InvalidDataException("bad netpbm header");
        return h;
    }

    private static string Token(Stream s)
    {
        var sb = new System.Text.StringBuilder();
        int c;
        while ((c = s.ReadByte()) >= 0)
        {
            if (c == '#') { while ((c = s.ReadByte()) >= 0 && c != '\n') { } continue; }
            if (char.IsWhiteSpace((char)c)) { if (sb.Length > 0) break; continue; }
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    private static ImportedImage Load(string path)
    {
        using (var fs = new BufferedStream(File.OpenRead(path)))
        {
            var h = ReadHeader(fs);
            int n = h.W * h.H;
            bool color = h.Type == '3' || h.Type == '6';
            bool ascii = h.Type == '2' || h.Type == '3';
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            float inv = 1f / h.Max;
            int bytes = h.Max > 255 ? 2 : 1;
            Func<float> next;
            if (ascii) next = () => int.Parse(Token(fs), CultureInfo.InvariantCulture) * inv;
            else if (bytes == 1) next = () => { int v = fs.ReadByte(); if (v < 0) throw new EndOfStreamException(); return v * inv; };
            else next = () => { int hi = fs.ReadByte(), lo = fs.ReadByte(); if (lo < 0) throw new EndOfStreamException(); return ((hi << 8) | lo) * inv; };
            for (int i = 0; i < n; i++)
            {
                if (color) { r[i] = next(); g[i] = next(); b[i] = next(); }
                else { r[i] = g[i] = b[i] = next(); }
            }
            return new ImportedImage { Width = h.W, Height = h.H, R = r, G = g, B = b, Linear = false };
        }
    }
}

// ---------------------------------------------------------------------
//  6. Web export + "publish" to an outbox folder (no dialog)
// ---------------------------------------------------------------------

public sealed class WebExportPlugin : IEyePlugin
{
    private IPluginHost _host;

    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.web",
        Name = "Web export",
        Version = "2.0",
        Author = "PhotographerEye samples",
        Description = "Resized JPEG for the web, and a one-click publish to an outbox folder.",
        Permissions = new[] { PluginPermissions.FileSystem }
    };

    public void Initialize(IPluginHost host)
    {
        _host = host;
        var options = new[]
        {
            ParamDef.Slider("size", "Long side (px)", 640, 4096, 2048, 32),
            ParamDef.Slider("quality", "JPEG quality", 50, 100, 85)
        };
        host.AddExporter(new ExporterDescriptor
        {
            Id = "web",
            Label = "Web JPEG…",
            FileFilter = "JPEG image (*.jpg)|*.jpg",
            DefaultSuffix = "_web",
            Options = options,
            Export = r => Write(r, r.DestinationPath)
        });
        host.AddExporter(new ExporterDescriptor
        {
            Id = "outbox",
            Label = "Publish to outbox",
            FileFilter = null,
            Options = options,
            Export = r =>
            {
                var dir = Path.Combine(_host.DataDirectory, "outbox");
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(r.SourcePath) + "_web.jpg");
                Write(r, dest);
                r.ResultMessage = "Published to " + dest;
            }
        });
    }

    private void Write(ExportRequest r, string dest)
    {
        var img = _host.Imaging.Resize(r.Image, (int)r.Options.Get("size"));
        r.Cancel.ThrowIfCancellationRequested();
        File.WriteAllBytes(dest, _host.Imaging.EncodeJpeg(img, (int)r.Options.Get("quality")));
        if (r.ResultMessage == null) r.ResultMessage = $"Exported {img.Width}x{img.Height} to {Path.GetFileName(dest)}";
    }
}

// ---------------------------------------------------------------------
//  7. Metadata provider: EV100, megapixels, 35 mm hint
// ---------------------------------------------------------------------

public sealed class ExposureInfoPlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.exposure",
        Name = "Exposure info",
        Version = "2.0",
        Author = "PhotographerEye samples",
        Description = "Adds EV100 and megapixels to the photo info tooltip."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddMetadataProvider(new MetadataProviderDescriptor
        {
            Id = "ev",
            Label = "Exposure",
            Provide = m =>
            {
                var list = new List<KeyValuePair<string, string>>();
                if (m.Aperture > 0 && m.ShutterSeconds > 0)
                {
                    double ev = Math.Log(m.Aperture.Value * m.Aperture.Value / m.ShutterSeconds.Value, 2);
                    if (m.Iso > 0) ev -= Math.Log(m.Iso.Value / 100.0, 2);
                    list.Add(new KeyValuePair<string, string>("EV100", ev.ToString("0.0", CultureInfo.InvariantCulture)));
                }
                if (m.Width > 0 && m.Height > 0)
                    list.Add(new KeyValuePair<string, string>("Megapixels", (m.Width * (double)m.Height / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MP"));
                return list;
            }
        });
    }
}

// ---------------------------------------------------------------------
//  8. Sky mask provider (hue + brightness range)
// ---------------------------------------------------------------------

public sealed class SkyMaskPlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.sky",
        Name = "Sky mask",
        Version = "2.0",
        Author = "PhotographerEye samples",
        Description = "Mask provider selecting bright blue areas (usable with any maskable filter)."
    };

    public void Initialize(IPluginHost host)
    {
        host.AddMaskProvider(new MaskProviderDescriptor
        {
            Id = "sky",
            Label = "Sky (blue hue)",
            Params = new[]
            {
                ParamDef.Slider("hue", "Hue", 170, 260, 215),
                ParamDef.Slider("tolerance", "Tolerance", 10, 90, 35),
                ParamDef.Slider("brightness", "Min brightness", 0, 100, 30)
            },
            CreateMask = (img, p, ctx) =>
            {
                float hue = (float)p.Get("hue"), tol = (float)p.Get("tolerance"), minV = (float)(p.Get("brightness") / 100.0);
                bool lin = ctx.Space == PluginColorSpace.LinearSrgb;
                var m = new float[img.Length];
                Parallel.For(0, img.Height, y =>
                {
                    int row = y * img.Width;
                    for (int i = row; i < row + img.Width; i++)
                    {
                        float r = img.R[i], g = img.G[i], b = img.B[i];
                        if (lin) { r = (float)Math.Pow(U.Clamp01(r), 1 / 2.2); g = (float)Math.Pow(U.Clamp01(g), 1 / 2.2); b = (float)Math.Pow(U.Clamp01(b), 1 / 2.2); }
                        U.Rgb2Hsv(r, g, b, out var h, out var s, out var v);
                        float hw = 1f - U.Smooth(U.HueDistance(h, hue) / tol);
                        float vw = U.Smooth((v - minV) / 0.15f + 0.5f);
                        float sw = U.Smooth(s * 6f);
                        m[i] = hw * vw * sw;
                    }
                });
                return m;
            }
        });
    }
}

// ---------------------------------------------------------------------
//  9. Event logger (host events) writing to its data directory
// ---------------------------------------------------------------------

public sealed class EventLogPlugin : IEyePlugin
{
    private string _log;

    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.eventlog",
        Name = "Event log",
        Version = "2.0",
        Author = "PhotographerEye samples",
        Description = "Writes opened, saved and exported files to events.log in its data folder."
    };

    public void Initialize(IPluginHost host)
    {
        _log = Path.Combine(host.DataDirectory, "events.log");
        host.Events.ImageOpened += (s, e) => Write($"opened  {e.Path}  {e.Meta.Width}x{e.Meta.Height}");
        host.Events.BeforeSave += (s, e) => Write($"saving  {e.SourcePath} -> {e.DestinationPath} ({e.Format})");
        host.Events.AfterSave += (s, e) => Write($"saved   {e.DestinationPath} ok={e.Success}");
        host.Events.AfterExport += (s, e) => Write($"export  {e.DestinationPath} via {e.Format} ok={e.Success}");
    }

    private void Write(string line)
    {
        lock (this)
            File.AppendAllText(_log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
    }
}
