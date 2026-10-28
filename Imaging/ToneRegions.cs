using System;

namespace ImgViewer.Imaging;

public sealed record ToneRegionGrid(float[] A, float[] B, int Lw, int Lh, int S);

// Shadows/Highlights masks are driven by an edge-aware base layer (fast guided filter on
// log2 luminance, solved on a ~480 px grid and upsampled with the full-res guide), so the
// EV gain follows regions instead of single pixels and local contrast inside them survives.
public static class ToneRegions
{
    private const int GridLong = 480;
    private const float Eps = 0.6f;
    private const float FloorEv = -12f;

    public static int Factor(int w, int h) => Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / (double)GridLong));

    public static float LogLum(float y) => MathF.Max(MathF.Log2(MathF.Max(y, 1e-9f) / 0.18f), FloorEv);

    public static ToneRegionGrid Solve(float[] low, int lw, int lh, int s, int fullW, int fullH)
    {
        int r = Math.Max(2, (int)Math.Round(Math.Min(fullW, fullH) * 0.03 / s));
        int n = lw * lh;
        var ii = new float[n];
        for (int i = 0; i < n; i++) ii[i] = low[i] * low[i];
        var mI = BoxMean(low, lw, lh, r);
        var mII = BoxMean(ii, lw, lh, r);
        var a = new float[n];
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float v = MathF.Max(mII[i] - mI[i] * mI[i], 0f);
            a[i] = v / (v + Eps);
            b[i] = mI[i] - a[i] * mI[i];
        }
        return new ToneRegionGrid(BoxMean(a, lw, lh, r), BoxMean(b, lw, lh, r), lw, lh, s);
    }

    public static float Base(ToneRegionGrid g, int x, int y, float l)
    {
        float gx = Math.Clamp((x + 0.5f) / g.S - 0.5f, 0f, g.Lw - 1);
        float gy = Math.Clamp((y + 0.5f) / g.S - 0.5f, 0f, g.Lh - 1);
        int x0 = (int)gx;
        int y0 = (int)gy;
        int x1 = Math.Min(x0 + 1, g.Lw - 1);
        int y1 = Math.Min(y0 + 1, g.Lh - 1);
        float fx = gx - x0;
        float fy = gy - y0;
        int i00 = y0 * g.Lw + x0, i01 = y0 * g.Lw + x1, i10 = y1 * g.Lw + x0, i11 = y1 * g.Lw + x1;
        float a = Lerp(Lerp(g.A[i00], g.A[i01], fx), Lerp(g.A[i10], g.A[i11], fx), fy);
        float b = Lerp(Lerp(g.B[i00], g.B[i01], fx), Lerp(g.B[i10], g.B[i11], fx), fy);
        return a * l + b;
    }

    public static float DeltaEv(float baseEv, float highlights, float shadows)
    {
        float d = highlights * 1.7f * SmoothEV(-1.0f, 2.8f, baseEv);
        float m = 1.0f - SmoothEV(-4.8f, 0.3f, baseEv);
        return d + shadows * 1.7f * m * MathF.Sqrt(m);
    }

    // Target linear luminance; brightening rolls off toward 1.0 instead of clipping.
    public static float TargetLum(float y, float deltaEv)
    {
        float g = MathF.Pow(2.0f, deltaEv);
        if (g <= 1.0f) return y * g;
        return y * g / (1.0f + (g - 1.0f) * MathF.Min(y, 1.0f));
    }

    private static float SmoothEV(float e0, float e1, float x)
    {
        float t = (x - e0) / (e1 - e0);
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float[] BoxMean(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        var pre = new double[Math.Max(w, h) + 1];
        for (int y = 0; y < h; y++)
        {
            int o = y * w;
            for (int x = 0; x < w; x++) pre[x + 1] = pre[x] + src[o + x];
            for (int x = 0; x < w; x++)
            {
                int a = Math.Max(0, x - r), b = Math.Min(w - 1, x + r);
                tmp[o + x] = (float)((pre[b + 1] - pre[a]) / (b - a + 1));
            }
        }
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) pre[y + 1] = pre[y] + tmp[y * w + x];
            for (int y = 0; y < h; y++)
            {
                int a = Math.Max(0, y - r), b = Math.Min(h - 1, y + r);
                dst[y * w + x] = (float)((pre[b + 1] - pre[a]) / (b - a + 1));
            }
        }
        return dst;
    }
}
