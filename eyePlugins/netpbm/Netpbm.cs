using System;
using System.Globalization;
using System.IO;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.Netpbm;

// ---------------------------------------------------------------------
//  Netpbm importer (.ppm / .pgm, binary and ASCII, 8/16-bit)
// ---------------------------------------------------------------------

public sealed class NetpbmPlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "sample.netpbm",
        Name = "Netpbm import",
        Version = "2.0",
        Author = "PhotographerEye",
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
