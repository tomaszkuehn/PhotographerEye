using System;
using System.Buffers;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Imaging;

public sealed class Raw16Image
{
    internal static readonly float[] Bayer =
    {
        -0.43750f, +0.31250f, -0.31250f, +0.43750f,
        +0.31250f, -0.43750f, +0.43750f, -0.31250f,
        -0.31250f, +0.43750f, -0.43750f, +0.31250f,
        +0.43750f, -0.31250f, +0.31250f, -0.43750f
    };

    public Raw16Image(int w, int h)
    {
        W = w;
        H = h;
        R = new ushort[w * h];
        G = new ushort[w * h];
        B = new ushort[w * h];
    }

    public Raw16Image(ushort[] r, ushort[] g, ushort[] b, int w, int h)
    {
        R = r;
        G = g;
        B = b;
        W = w;
        H = h;
    }

    public int W { get; }
    public int H { get; }
    public ushort[] R { get; }
    public ushort[] G { get; }
    public ushort[] B { get; }

    public static Raw16Image FromInterleaved(ushort[] packed, int w, int h)
    {
        int n = w * h;
        var r = new ushort[n];
        var g = new ushort[n];
        var b = new ushort[n];
        for (int i = 0, o = 0; i < n; i++, o += 3)
        {
            r[i] = packed[o];
            g[i] = packed[o + 1];
            b[i] = packed[o + 2];
        }
        return new Raw16Image(r, g, b, w, h);
    }

    public ushort[] ToInterleaved()
    {
        int n = W * H;
        var dst = new ushort[n * 3];
        for (int i = 0, o = 0; i < n; i++, o += 3)
        {
            dst[o] = R[i];
            dst[o + 1] = G[i];
            dst[o + 2] = B[i];
        }
        return dst;
    }

    /// <summary>
    /// Area-averaged copy whose longer side is <paramref name="targetLong"/> (the image itself when that is not smaller).
    /// Used for the live preview, which only needs as many pixels as the screen shows.
    /// </summary>
    public Raw16Image Downscale(int targetLong)
    {
        int srcLong = Math.Max(W, H);
        if (targetLong <= 0 || targetLong >= srcLong) return this;
        double f = srcLong / (double)targetLong;
        int dw = Math.Max(1, (int)Math.Round(W / f));
        int dh = Math.Max(1, (int)Math.Round(H / f));
        var dst = new Raw16Image(dw, dh);
        int sw = W, sh = H;
        var sr = R;
        var sg = G;
        var sb = B;
        Parallel.For(0, dh, y =>
        {
            int y0 = (int)((long)y * sh / dh);
            int y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sh / dh));
            int o = y * dw;
            for (int x = 0; x < dw; x++)
            {
                int x0 = (int)((long)x * sw / dw);
                int x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sw / dw));
                long ar = 0, ag = 0, ab = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int row = yy * sw;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        ar += sr[row + xx];
                        ag += sg[row + xx];
                        ab += sb[row + xx];
                    }
                }
                long n = (long)(y1 - y0) * (x1 - x0);
                dst.R[o + x] = (ushort)((ar + n / 2) / n);
                dst.G[o + x] = (ushort)((ag + n / 2) / n);
                dst.B[o + x] = (ushort)((ab + n / 2) / n);
            }
        });
        return dst;
    }

    public BitmapSource ToDisplay(bool dither)
    {
        int stride = W * 4;
        int len = checked(stride * H);
        // Pooled scratch: BitmapSource.Create copies the pixels, so the buffer goes back right after.
        var bgra = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            int w = W;
            var lut = QuantLut.Value;
            var pr = R;
            var pg = G;
            var pb = B;
            Parallel.For(0, H, y =>
            {
                int dy = (y & 3) * 4;
                int p = y * stride;
                int pi = y * w;
                for (int x = 0; x < w; x++, p += 4, pi++)
                {
                    // Row of the LUT = Bayer cell (row 16: no dither).
                    int o = (dither ? dy + (x & 3) : 16) << 16;
                    bgra[p] = lut[o | pb[pi]];
                    bgra[p + 1] = lut[o | pg[pi]];
                    bgra[p + 2] = lut[o | pr[pi]];
                    bgra[p + 3] = 255;
                }
            });
            var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bgra);
        }
    }

    /// <summary>16-bit to 8-bit with the ordered dither offset: 16 Bayer rows plus one plain row, 65536 entries each.</summary>
    private static readonly Lazy<byte[]> QuantLut = new(() =>
    {
        var t = new byte[17 << 16];
        for (int r = 0; r < 17; r++)
        {
            float d = r < 16 ? Bayer[r] : 0f;
            for (int v = 0; v < 65536; v++) t[(r << 16) | v] = Quantize((ushort)v, d);
        }
        return t;
    });

    private static byte Quantize(ushort v, float dither)
    {
        int i = (int)(v * (255.0f / 65535.0f) + dither + 0.5f);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }
}