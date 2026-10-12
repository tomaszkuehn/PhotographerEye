using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Imaging;

public sealed class Raw16Image
{
    private static readonly float[] Bayer =
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

    public BitmapSource ToDisplay(bool dither)
    {
        int stride = W * 4;
        var bgra = new byte[(long)stride * H];
        int p = 0;
        int pi = 0;
        for (int y = 0; y < H; y++)
        {
            int dy = (y & 3) * 4;
            for (int x = 0; x < W; x++, p += 4, pi++)
            {
                float d = dither ? Bayer[dy + (x & 3)] : 0f;
                bgra[p] = Quantize(B[pi], d);
                bgra[p + 1] = Quantize(G[pi], d);
                bgra[p + 2] = Quantize(R[pi], d);
                bgra[p + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
        bmp.Freeze();
        return bmp;
    }

    private static byte Quantize(ushort v, float dither)
    {
        int i = (int)(v * (255.0f / 65535.0f) + dither + 0.5f);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }
}