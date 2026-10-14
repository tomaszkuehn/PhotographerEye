using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Imaging;

public sealed record GeoState
{
    public int Rot90 { get; init; }
    public double Angle { get; init; }
    public double CropX { get; init; }
    public double CropY { get; init; }
    public double CropW { get; init; } = 1.0;
    public double CropH { get; init; } = 1.0;
    public string Aspect { get; init; } = "original";

    public static GeoState Default { get; } = new();

    public bool IsDefault => Rot90 % 4 == 0 && Math.Abs(Angle) < 0.005 &&
        CropX < 0.0001 && CropY < 0.0001 && CropW > 0.9999 && CropH > 0.9999;

    public GeoState WithRotation(int quarterTurnsCw)
    {
        int rot = ((Rot90 + quarterTurnsCw) % 4 + 4) % 4;
        var (x, y, w, h) = RotateCropFractions(CropX, CropY, CropW, CropH, quarterTurnsCw);
        return this with { Rot90 = rot, CropX = x, CropY = y, CropW = w, CropH = h };
    }

    public static (double X, double Y, double W, double H) RotateCropFractions(double x, double y, double w, double h, int turnsCw)
    {
        turnsCw = ((turnsCw % 4) + 4) % 4;
        for (int i = 0; i < turnsCw; i++)
        {
            (x, y, w, h) = (1.0 - (y + h), x, h, w);
        }
        return (x, y, w, h);
    }
}

public static class PhotoGeo
{
    public const double MinFraction = 0.02;
    private const double AngleEps = 0.005;

    public static double InsetScale(double w, double h, double degrees)
    {
        double rad = Math.Clamp(degrees, -45.0, 45.0) * Math.PI / 180.0;
        double c = Math.Abs(Math.Cos(rad));
        double s = Math.Abs(Math.Sin(rad));
        double d1 = w * c + h * s;
        double d2 = w * s + h * c;
        if (d1 < 1e-9 || d2 < 1e-9) return 1.0;
        return Math.Min(w / d1, h / d2);
    }

    public static bool IsHeavy(GeoState g) => Math.Abs(g.Angle) >= AngleEps;

    public static (BitmapSource Src, Size Box) Apply(BitmapSource src, Size nativeBox, GeoState g)
    {
        if (g.IsDefault) return (src, nativeBox);

        int rot = ((g.Rot90 % 4) + 4) % 4;
        if (rot != 0)
        {
            src = Rotate90Cw(src, rot);
            if (rot == 1 || rot == 3) nativeBox = new Size(nativeBox.Height, nativeBox.Width);
        }

        if (Math.Abs(g.Angle) >= AngleEps)
        {
            double t = InsetScale(src.PixelWidth, src.PixelHeight, g.Angle);
            src = RotateFree(src, g.Angle);
            nativeBox = new Size(nativeBox.Width * t, nativeBox.Height * t);
        }

        var (x, y, w, h) = ClampFractions(g);
        if (x > 0.0001 || y > 0.0001 || w < 0.9999 || h < 0.9999)
        {
            int px = (int)Math.Round(x * src.PixelWidth, MidpointRounding.AwayFromZero);
            int py = (int)Math.Round(y * src.PixelHeight, MidpointRounding.AwayFromZero);
            int pw = Math.Max(1, Math.Min(src.PixelWidth - px, (int)Math.Round((x + w) * src.PixelWidth, MidpointRounding.AwayFromZero) - px));
            int ph = Math.Max(1, Math.Min(src.PixelHeight - py, (int)Math.Round((y + h) * src.PixelHeight, MidpointRounding.AwayFromZero) - py));
            var crop = new CroppedBitmap(src, new Int32Rect(px, py, pw, ph));
            crop.Freeze();
            src = crop;
            nativeBox = new Size(nativeBox.Width * w, nativeBox.Height * h);
        }
        return (src, nativeBox);
    }

    public static Raw16Image Apply(Raw16Image img, GeoState g)
    {
        if (g.IsDefault) return img;

        int rot = ((g.Rot90 % 4) + 4) % 4;
        if (rot != 0) img = Rotate16(img, rot);

        if (Math.Abs(g.Angle) >= AngleEps) img = Rotate16Free(img, g.Angle);

        var (x, y, w, h) = ClampFractions(g);
        if (x > 0.0001 || y > 0.0001 || w < 0.9999 || h < 0.9999)
        {
            int px = (int)Math.Round(x * img.W, MidpointRounding.AwayFromZero);
            int py = (int)Math.Round(y * img.H, MidpointRounding.AwayFromZero);
            int pw = Math.Max(1, Math.Min(img.W - px, (int)Math.Round((x + w) * img.W, MidpointRounding.AwayFromZero) - px));
            int ph = Math.Max(1, Math.Min(img.H - py, (int)Math.Round((y + h) * img.H, MidpointRounding.AwayFromZero) - py));
            img = Crop16(img, px, py, pw, ph);
        }
        return img;
    }

    private static (double X, double Y, double W, double H) ClampFractions(GeoState g)
    {
        double w = Math.Clamp(g.CropW, MinFraction, 1.0);
        double h = Math.Clamp(g.CropH, MinFraction, 1.0);
        double x = Math.Clamp(g.CropX, 0.0, Math.Max(0.0, 1.0 - w));
        double y = Math.Clamp(g.CropY, 0.0, Math.Max(0.0, 1.0 - h));
        return (x, y, w, h);
    }

    private static BitmapSource Rotate90Cw(BitmapSource src, int turnsCw)
    {
        var t = new RotateTransform(turnsCw * 90.0);
        t.Freeze();
        var tb = new TransformedBitmap();
        tb.BeginInit();
        tb.Source = src;
        tb.Transform = t;
        tb.EndInit();
        tb.Freeze();
        return tb;
    }

    public static BitmapSource RotateFree(BitmapSource src, double degrees)
    {
        if (Math.Abs(degrees) < AngleEps) return src;
        if (src.Format != PixelFormats.Bgra32)
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            conv.Freeze();
            src = conv;
        }
        int w = src.PixelWidth;
        int h = src.PixelHeight;
        double t = InsetScale(w, h, degrees);
        int ow = Math.Max(1, (int)Math.Round(w * t));
        int oh = Math.Max(1, (int)Math.Round(h * t));
        double rad = Math.Clamp(degrees, -45.0, 45.0) * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        var inBuf = new byte[(long)w * 4 * h];
        src.CopyPixels(inBuf, w * 4, 0);
        var outBuf = new byte[(long)ow * 4 * oh];
        double ocx = ow / 2.0;
        double ocy = oh / 2.0;
        double scx = w / 2.0;
        double scy = h / 2.0;
        unsafe
        {
            fixed (byte* inp = inBuf, outp = outBuf)
            {
                for (int oy = 0; oy < oh; oy++)
                {
                    byte* orow = outp + (long)oy * ow * 4;
                    double dy = oy + 0.5 - ocy;
                    for (int ox = 0; ox < ow; ox++)
                    {
                        double dx = ox + 0.5 - ocx;
                        double sx = cos * dx + sin * dy + scx;
                        double sy = -sin * dx + cos * dy + scy;
                        SampleBgra(inp, w, h, sx, sy, orow + ox * 4);
                    }
                }
            }
        }
        var res = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgra32, null, outBuf, ow * 4);
        res.Freeze();
        return res;
    }

    private static unsafe void SampleBgra(byte* inp, int w, int h, double sx, double sy, byte* dst)
    {
        double x0 = Math.Floor(sx - 0.5);
        double y0 = Math.Floor(sy - 0.5);
        double tx = sx - 0.5 - x0;
        double ty = sy - 0.5 - y0;
        int ix0 = Math.Clamp((int)x0, 0, w - 1);
        int ix1 = Math.Clamp((int)x0 + 1, 0, w - 1);
        int iy0 = Math.Clamp((int)y0, 0, h - 1);
        int iy1 = Math.Clamp((int)y0 + 1, 0, h - 1);
        double w00 = (1.0 - tx) * (1.0 - ty);
        double w10 = tx * (1.0 - ty);
        double w01 = (1.0 - tx) * ty;
        double w11 = tx * ty;
        byte* p00 = inp + ((long)iy0 * w + ix0) * 4;
        byte* p10 = inp + ((long)iy0 * w + ix1) * 4;
        byte* p01 = inp + ((long)iy1 * w + ix0) * 4;
        byte* p11 = inp + ((long)iy1 * w + ix1) * 4;
        for (int c = 0; c < 4; c++)
        {
            double v = p00[c] * w00 + p10[c] * w10 + p01[c] * w01 + p11[c] * w11;
            int iv = (int)(v + 0.5);
            dst[c] = (byte)(iv < 0 ? 0 : iv > 255 ? 255 : iv);
        }
    }

    private static Raw16Image Rotate16(Raw16Image s, int turnsCw)
    {
        int w = s.W;
        int h = s.H;
        int nw = h;
        int nh = w;
        var r = new ushort[nw * nh];
        var g = new ushort[nw * nh];
        var b = new ushort[nw * nh];
        for (int y = 0; y < nh; y++)
        {
            for (int x = 0; x < nw; x++)
            {
                int si, sj;
                if (turnsCw == 1) { si = h - 1 - x; sj = y; }
                else if (turnsCw == 2) { si = h - 1 - y; sj = w - 1 - x; }
                else { si = x; sj = w - 1 - y; }
                int di = y * nw + x;
                int srci = si * w + sj;
                r[di] = s.R[srci];
                g[di] = s.G[srci];
                b[di] = s.B[srci];
            }
        }
        return new Raw16Image(r, g, b, nw, nh);
    }

    private static unsafe Raw16Image Rotate16Free(Raw16Image s, double degrees)
    {
        int w = s.W;
        int h = s.H;
        double t = InsetScale(w, h, degrees);
        int ow = Math.Max(1, (int)Math.Round(w * t));
        int oh = Math.Max(1, (int)Math.Round(h * t));
        double rad = Math.Clamp(degrees, -45.0, 45.0) * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double ocx = ow / 2.0;
        double ocy = oh / 2.0;
        double scx = w / 2.0;
        double scy = h / 2.0;
        var r = new ushort[ow * oh];
        var g = new ushort[ow * oh];
        var b = new ushort[ow * oh];
        fixed (ushort* rp = s.R, gp = s.G, bp = s.B, dr = r, dg = g, db = b)
        {
            for (int oy = 0; oy < oh; oy++)
            {
                double dy = oy + 0.5 - ocy;
                long orow = (long)oy * ow;
                for (int ox = 0; ox < ow; ox++)
                {
                    double dx = ox + 0.5 - ocx;
                    double sx = cos * dx + sin * dy + scx;
                    double sy = -sin * dx + cos * dy + scy;
                    double x0 = Math.Floor(sx - 0.5);
                    double y0 = Math.Floor(sy - 0.5);
                    double tx = sx - 0.5 - x0;
                    double ty = sy - 0.5 - y0;
                    int ix0 = Math.Clamp((int)x0, 0, w - 1);
                    int ix1 = Math.Clamp((int)x0 + 1, 0, w - 1);
                    int iy0 = Math.Clamp((int)y0, 0, h - 1);
                    int iy1 = Math.Clamp((int)y0 + 1, 0, h - 1);
                    double w00 = (1.0 - tx) * (1.0 - ty);
                    double w10 = tx * (1.0 - ty);
                    double w01 = (1.0 - tx) * ty;
                    double w11 = tx * ty;
                    long i00 = (long)iy0 * w + ix0;
                    long i10 = (long)iy0 * w + ix1;
                    long i01 = (long)iy1 * w + ix0;
                    long i11 = (long)iy1 * w + ix1;
                    long oi = orow + ox;
                    dr[oi] = Clamp16(rp[i00] * w00 + rp[i10] * w10 + rp[i01] * w01 + rp[i11] * w11);
                    dg[oi] = Clamp16(gp[i00] * w00 + gp[i10] * w10 + gp[i01] * w01 + gp[i11] * w11);
                    db[oi] = Clamp16(bp[i00] * w00 + bp[i10] * w10 + bp[i01] * w01 + bp[i11] * w11);
                }
            }
        }
        return new Raw16Image(r, g, b, ow, oh);
    }

    private static ushort Clamp16(double v)
    {
        long iv = (long)(v + 0.5);
        if (iv < 0) return 0;
        if (iv > 65535) return 65535;
        return (ushort)iv;
    }

    private static Raw16Image Crop16(Raw16Image s, int px, int py, int pw, int ph)
    {
        var r = new ushort[pw * ph];
        var g = new ushort[pw * ph];
        var b = new ushort[pw * ph];
        for (int y = 0; y < ph; y++)
        {
            long src = (long)(py + y) * s.W + px;
            long dst = (long)y * pw;
            Array.Copy(s.R, src, r, dst, pw);
            Array.Copy(s.G, src, g, dst, pw);
            Array.Copy(s.B, src, b, dst, pw);
        }
        return new Raw16Image(r, g, b, pw, ph);
    }
}