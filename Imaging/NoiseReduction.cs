using System;
using System.Threading.Tasks;

namespace ImgViewer.Imaging;

// Ky/Kc: threshold in units of the measured noise std (luma/chroma); KeepY: fraction of luma grain kept;
// Median: blend of the 3x3 chroma median (color speckle removal) applied before the wavelet shrink.
// YK/CK: the threshold per wavelet scale (their length is the number of scales): Ky/Kc on the base scales and,
// past 100 on the slider, one extra coarser scale whose threshold ramps in from 0 (coarse blotches).
public sealed record NoisePlan(float Ky, float Kc, float KeepY, float Median, float[] YK, float[] CK);

// Sparse grid on which noise is measured; Bins holds the brightness band of each grid point.
public sealed record NoiseGrid(int Step, int Nx, int Ny, byte[] Bins);

// Noise reduction (luminance and color sliders): the image is split into YCoCg planes in a noise-stabilized
// domain (sqrt of linear for RAW, encoded values for 8-bit), each plane is decomposed with an
// undecimated B3-spline wavelet (a trous) and detail coefficients are shrunk with a Wiener-like
// gain d^2/(d^2+T^2). The noise level is measured on the image itself, separately for every
// wavelet scale and every brightness band: demosaiced noise is spatially correlated (coarse
// blotches, especially chroma) and shadows carry read noise the sqrt does not flatten, so a single
// fine-scale estimate would leave dark areas noisy. The sliders mean "how much of the measured
// noise to remove": edges and texture well above the noise floor pass untouched, flat-area grain
// and color blotches go away instead of everything blurring. Color speckle (single pixels or tiny
// clusters far off the noise distribution, typical of demosaiced shadows) is an outlier no
// threshold catches, so the chroma planes first go through a 3x3 median and then a 3x3 median with
// taps 2 px apart (removes blobs up to ~4x4 px, the size X-Trans demosaicing leaves) while keeping
// chroma edges; luminance is never touched by it.
public static class NoiseReduction
{
    public const int LumaScales = 4;
    public const int ChromaScales = 5;
    public const int Bins = 16;

    // Std of B3 a trous detail coefficients per scale for unit white noise (Starck & Murtagh);
    // only used to scale the noise floor.
    private static readonly float[] ScaleNoise = { 0.8908f, 0.2007f, 0.0856f, 0.0413f, 0.0205f, 0.0103f };

    private const float MinSigma = 0.0015f;

    // Q25 of |N(0, s)| = 0.3186 s. A low quantile instead of the median: texture only adds large
    // coefficients, so the lower part of the distribution stays dominated by noise.
    private const float Q25 = 0.3186f;

    /// <summary>Upper end of the sliders (as a fraction: 2 = 200 on the slider). Up to 1 the result is unchanged from the 0..100 sliders.</summary>
    public const double MaxStrength = 2.0;

    public static NoisePlan Plan(double luma, double color)
    {
        double s = Math.Clamp(luma, 0.0, MaxStrength);
        double c = Math.Clamp(color, 0.0, MaxStrength);
        // Chroma ramps up fast (blotches are ugly and their removal costs no detail), luma gently;
        // a little fine grain is kept so strong settings do not look plastic. The speckle median is
        // fully in from the middle of the color slider. Past 1 (very noisy photos: high ISO, pushed
        // shadows) the luma threshold grows at half the rate (up to 1.5x its value at 1: a larger one
        // mostly softens edges), the chroma one keeps its curve, the kept grain fades out to none at 2
        // and one coarser scale ramps in from 0 (coarse blotches).
        float ky = (float)(s <= 1.0 ? 3.0 * s : 3.0 + 1.5 * (s - 1.0)), kc = (float)(6.0 * Math.Sqrt(c));
        float keep = (float)(0.12 * Math.Min(s, MaxStrength - s));
        return new NoisePlan(ky, kc, keep, (float)Math.Min(1.0, 2.0 * c), Scales(ky, LumaScales, s), Scales(kc, ChromaScales, c));
    }

    private static float[] Scales(float k, int baseScales, double strength)
    {
        bool extra = strength > 1.0;
        var ks = new float[baseScales + (extra ? 1 : 0)];
        Array.Fill(ks, k);
        if (extra) ks[baseScales] = (float)(k * (strength - 1.0) / (MaxStrength - 1.0));
        return ks;
    }

    public static bool Active(double luma, double color) => luma > 0.0005 || color > 0.0005;

    // Median of 9 (Paeth / Devillard network, 19 compare-exchanges).
    public static float Median9(float p0, float p1, float p2, float p3, float p4, float p5, float p6, float p7, float p8)
    {
        static void S(ref float a, ref float b) { if (a > b) (a, b) = (b, a); }
        S(ref p1, ref p2); S(ref p4, ref p5); S(ref p7, ref p8);
        S(ref p0, ref p1); S(ref p3, ref p4); S(ref p6, ref p7);
        S(ref p1, ref p2); S(ref p4, ref p5); S(ref p7, ref p8);
        S(ref p0, ref p3); S(ref p5, ref p8); S(ref p4, ref p7);
        S(ref p3, ref p6); S(ref p1, ref p4); S(ref p2, ref p5);
        S(ref p4, ref p7); S(ref p4, ref p2); S(ref p6, ref p4);
        S(ref p4, ref p2);
        return p4;
    }

    // 3x3 median (taps step px apart) of a chroma plane blended in by t (in place).
    public static void Despeckle(float[] plane, int w, int h, float t, int step, ParallelOptions opts)
    {
        if (t <= 0f) return;
        var src = (float[])plane.Clone();
        Parallel.For(0, h, opts, y =>
        {
            int m = Math.Max(y - step, 0) * w, o = y * w, p = Math.Min(y + step, h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                int l = Math.Max(x - step, 0), r = Math.Min(x + step, w - 1);
                float med = Median9(src[m + l], src[m + x], src[m + r], src[o + l], src[o + x], src[o + r], src[p + l], src[p + x], src[p + r]);
                plane[o + x] = src[o + x] + t * (med - src[o + x]);
            }
        });
    }

    public static void ToPlanes(float r, float g, float b, out float y, out float co, out float cg)
    {
        y = 0.25f * r + 0.5f * g + 0.25f * b;
        co = 0.5f * (r - b);
        cg = -0.25f * r + 0.5f * g - 0.25f * b;
    }

    public static void FromPlanes(float y, float co, float cg, out float r, out float g, out float b)
    {
        float t = y - cg;
        g = y + cg;
        r = t + co;
        b = t - co;
    }

    public static float Shrink(float d, float t, float keep)
    {
        float d2 = d * d;
        float s = d * d2 / (d2 + t * t + 1e-20f);
        return s + keep * (d - s);
    }

    public static int Band(float luma) => Math.Clamp((int)(luma * Bins), 0, Bins - 1);

    // Noise std of a band-indexed table at a given luma, linear between band centers.
    public static float Lookup(float[] lut, float g)
    {
        float f = Math.Clamp(g * Bins - 0.5f, 0f, Bins - 1);
        int i0 = (int)f;
        int i1 = Math.Min(i0 + 1, Bins - 1);
        return lut[i0] + (lut[i1] - lut[i0]) * (f - i0);
    }

    // ~200k grid points (cell centers), band taken from the guide luma.
    public static NoiseGrid MakeGrid(int w, int h, Func<int, float> guideAt)
    {
        int step = Math.Max(1, (int)Math.Sqrt((double)w * h / 200_000.0));
        int nx = w / step, ny = h / step;
        var bins = new byte[nx * ny];
        Parallel.For(0, ny, j =>
        {
            int y = j * step + step / 2;
            for (int k = 0; k < nx; k++) bins[j * nx + k] = (byte)Band(guideAt(y * w + k * step + step / 2));
        });
        return new NoiseGrid(step, nx, ny, bins);
    }

    public static int GridIndex(NoiseGrid g, int w, int j, int k) => (j * g.Step + g.Step / 2) * w + k * g.Step + g.Step / 2;

    // Per-band noise std of one scale from |detail| sampled on the grid. Bands with too few samples
    // are interpolated from measured neighbours (flat past the ends), then lightly smoothed.
    // Noise gets weaker toward coarser scales (white noise: x0.23 from scale 0 to 1, then ~x0.45;
    // demosaiced, correlated noise decays slower but still decays), while texture grows: capping
    // each scale at a fraction of the previous one keeps fine texture from being read as noise.
    public static float[] BandSigma(float[] absDetail, NoiseGrid grid, int scale, float[]? prev)
    {
        var bins = grid.Bins;
        var counts = new int[Bins];
        foreach (var b in bins) counts[b]++;
        var groups = new float[Bins][];
        for (int b = 0; b < Bins; b++) groups[b] = new float[counts[b]];
        var fill = new int[Bins];
        for (int i = 0; i < bins.Length; i++) groups[bins[i]][fill[bins[i]]++] = absDetail[i];
        var sig = new float[Bins];
        var ok = new bool[Bins];
        int minCount = Math.Max(64, bins.Length / 2000);
        for (int b = 0; b < Bins; b++)
        {
            if (counts[b] < minCount) continue;
            sig[b] = Quantile(groups[b], 0.25f) / Q25;
            ok[b] = true;
        }
        // The pooled quantile is only robust while texture is a minority of the band. Block RMS
        // (4x4 grid points, spaced at least the wavelet step apart so samples are near-independent)
        // stays robust far beyond that: flat blocks cluster tightly around the noise level and a low
        // quantile picks them even if most blocks are textured. Q20 of RMS over 16 Gaussian samples is
        // 0.835 sigma; the 1.2 slack keeps the pooled estimate where both agree (pure noise).
        int m = Math.Max(1, ((1 << scale) + grid.Step - 1) / grid.Step);
        int span = 4 * m;
        var blocks = new System.Collections.Generic.List<float>[Bins];
        for (int b = 0; b < Bins; b++) blocks[b] = new System.Collections.Generic.List<float>();
        for (int by = 0; by + span <= grid.Ny; by += span)
            for (int bx = 0; bx + span <= grid.Nx; bx += span)
            {
                float ss = 0f;
                for (int u = 0; u < 4; u++)
                    for (int v = 0; v < 4; v++)
                    {
                        float d = absDetail[(by + u * m) * grid.Nx + bx + v * m];
                        ss += d * d;
                    }
                blocks[bins[(by + 2 * m) * grid.Nx + bx + 2 * m]].Add(MathF.Sqrt(ss / 16f));
            }
        for (int b = 0; b < Bins; b++)
        {
            if (!ok[b] || blocks[b].Count < 16) continue;
            sig[b] = MathF.Min(sig[b], 1.2f * Quantile(blocks[b].ToArray(), 0.2f) / 0.835f);
        }
        float floor = MinSigma * ScaleNoise[Math.Min(scale, ScaleNoise.Length - 1)] / ScaleNoise[0];
        // A band made only of edges or texture (e.g. transition pixels between two flat areas, or a
        // stripe pattern whose local mean has its own brightness) has no flat block and shows up as a
        // bump in the profile. Real noise only rises toward one end (read noise in the shadows, or a
        // tone curve), so cap every band at 1.2x the larger of the lowest level on its darker side and
        // the lowest on its brighter side. Band 0 and the top two bands (black and white clipping) are
        // not used as limits.
        float pre = float.MaxValue;
        var preMin = new float[Bins];
        for (int b = 0; b < Bins; b++)
        {
            if (ok[b] && b >= 1 && b < Bins - 2) pre = MathF.Min(pre, MathF.Max(sig[b], floor));
            preMin[b] = pre;
        }
        float suf = float.MaxValue;
        for (int b = Bins - 1; b >= 1; b--)
        {
            if (ok[b] && b < Bins - 2) suf = MathF.Min(suf, MathF.Max(sig[b], floor));
            if (ok[b]) sig[b] = MathF.Min(sig[b], 1.2f * MathF.Max(preMin[b], suf));
        }
        // The noise level also changes smoothly with brightness, so a band far above any measured band
        // is edges or texture: limit every band to 2x per band of distance from the others. Band 0
        // (black clipping, read noise) neither limits nor is limited.
        var raw = (float[])sig.Clone();
        for (int b = 1; b < Bins; b++)
        {
            if (!ok[b]) continue;
            for (int a = 1; a < Bins; a++)
                if (ok[a] && a != b) sig[b] = MathF.Min(sig[b], MathF.Max(raw[a], floor) * MathF.Pow(2f, Math.Abs(a - b)));
        }
        float all = Quantile(absDetail, 0.25f) / Q25;
        for (int b = 0; b < Bins; b++)
        {
            if (ok[b]) continue;
            int lo = b - 1, hi = b + 1;
            while (lo >= 0 && !ok[lo]) lo--;
            while (hi < Bins && !ok[hi]) hi++;
            if (lo < 0 && hi >= Bins) sig[b] = all;
            else if (lo < 0) sig[b] = sig[hi];
            else if (hi >= Bins) sig[b] = sig[lo];
            else sig[b] = sig[lo] + (sig[hi] - sig[lo]) * (b - lo) / (hi - lo);
        }
        float cap = scale == 1 ? 0.5f : 0.9f;
        var r = new float[Bins];
        for (int i = 0; i < Bins; i++)
        {
            float v = 0.25f * sig[Math.Max(i - 1, 0)] + 0.5f * sig[i] + 0.25f * sig[Math.Min(i + 1, Bins - 1)];
            if (prev is not null) v = MathF.Min(v, prev[i] * cap);
            r[i] = MathF.Max(v, floor);
        }
        return r;
    }

    private static float Quantile(float[] v, float q)
    {
        if (v.Length == 0) return 0f;
        var c = (float[])v.Clone();
        Array.Sort(c);
        return c[Math.Min((int)(q * c.Length), c.Length - 1)];
    }

    // Local luma that picks the noise band: the Y plane smoothed by two a trous levels (~9x9),
    // so the band choice is not itself driven by the noise.
    public static float[] Guide(float[] py, int w, int h, ParallelOptions opts)
    {
        var a = new float[py.Length];
        var tmp = new float[py.Length];
        var g = new float[py.Length];
        Atrous(py, tmp, a, w, h, 1, opts);
        Atrous(a, tmp, g, w, h, 2, opts);
        return g;
    }

    // Denoises one plane in place; returns the per-scale band sigmas it measured (diagnostics).
    public static float[][] DenoisePlane(float[] plane, int w, int h, float[] ks, float keep, float[] guide, NoiseGrid grid, ParallelOptions opts)
    {
        int scales = ks.Length;
        var measured = new float[scales][];
        if (scales == 0 || ks[0] <= 0f) return measured;
        int n = plane.Length;
        var cur = (float[])plane.Clone();
        var next = new float[n];
        var tmp = new float[n];
        var samples = new float[grid.Bins.Length];
        for (int j = 0; j < scales; j++)
        {
            int step = 1 << j;
            if (step * 2 >= Math.Min(w, h)) break;
            Atrous(cur, tmp, next, w, h, step, opts);
            var c = cur;
            var nx = next;
            Parallel.For(0, grid.Ny, opts, gy =>
            {
                for (int gx = 0; gx < grid.Nx; gx++)
                {
                    int i = GridIndex(grid, w, gy, gx);
                    samples[gy * grid.Nx + gx] = MathF.Abs(c[i] - nx[i]);
                }
            });
            var sig = BandSigma(samples, grid, j, j > 0 ? measured[j - 1] : null);
            measured[j] = sig;
            bool first = j == 0;
            float k = ks[j];
            Parallel.For(0, h, opts, y =>
            {
                int o = y * w;
                for (int i = o; i < o + w; i++)
                {
                    float d = Shrink(c[i] - nx[i], k * Lookup(sig, guide[i]), keep);
                    plane[i] = first ? d : plane[i] + d;
                }
            });
            (cur, next) = (next, cur);
        }
        Parallel.For(0, h, opts, y =>
        {
            int o = y * w;
            for (int i = o; i < o + w; i++) plane[i] += cur[i];
        });
        return measured;
    }

    private static void Atrous(float[] src, float[] tmp, float[] dst, int w, int h, int step, ParallelOptions opts)
    {
        Parallel.For(0, h, opts, y =>
        {
            int o = y * w;
            for (int x = 0; x < w; x++)
            {
                int xm2 = Math.Max(x - 2 * step, 0), xm1 = Math.Max(x - step, 0);
                int xp1 = Math.Min(x + step, w - 1), xp2 = Math.Min(x + 2 * step, w - 1);
                tmp[o + x] = (src[o + xm2] + src[o + xp2] + 4f * (src[o + xm1] + src[o + xp1]) + 6f * src[o + x]) * 0.0625f;
            }
        });
        Parallel.For(0, h, opts, y =>
        {
            int m2 = Math.Max(y - 2 * step, 0) * w, m1 = Math.Max(y - step, 0) * w;
            int p1 = Math.Min(y + step, h - 1) * w, p2 = Math.Min(y + 2 * step, h - 1) * w;
            int o = y * w;
            for (int x = 0; x < w; x++)
                dst[o + x] = (tmp[m2 + x] + tmp[p2 + x] + 4f * (tmp[m1 + x] + tmp[p1 + x]) + 6f * tmp[o + x]) * 0.0625f;
        });
    }

    // Shared by the 16-bit and 8-bit paths: planes in, planes out (in place). Returns the measured
    // luma sigmas per scale and band (diagnostics).
    private static float[][] DenoisePlanes(float[] py, float[] po, float[] pc, int w, int h, double luma, double color, ParallelOptions opts)
    {
        var plan = Plan(luma, color);
        var guide = Guide(py, w, h, opts);
        var grid = MakeGrid(w, h, i => guide[i]);
        var my = DenoisePlane(py, w, h, plan.YK, plan.KeepY, guide, grid, opts);
        foreach (var pl in new[] { po, pc })
        {
            Despeckle(pl, w, h, plan.Median, 1, opts);
            Despeckle(pl, w, h, plan.Median, 2, opts);
        }
        DenoisePlane(po, w, h, plan.CK, 0f, guide, grid, opts);
        DenoisePlane(pc, w, h, plan.CK, 0f, guide, grid, opts);
        return my;
    }

    // 16-bit linear RGB planes (in place). Works in sqrt(linear) where photon noise is ~constant.
    public static float[][]? Apply16(ushort[] pr, ushort[] pg, ushort[] pb, int w, int h, double luma, double color, ParallelOptions opts)
    {
        if (!Active(luma, color)) return null;
        var sq = SqrtLut.Value;
        int n = pr.Length;
        var py = new float[n];
        var po = new float[n];
        var pc = new float[n];
        Parallel.For(0, h, opts, y =>
        {
            for (int i = y * w; i < (y + 1) * w; i++)
                ToPlanes(sq[pr[i]], sq[pg[i]], sq[pb[i]], out py[i], out po[i], out pc[i]);
        });
        var measured = DenoisePlanes(py, po, pc, w, h, luma, color, opts);
        Parallel.For(0, h, opts, y =>
        {
            for (int i = y * w; i < (y + 1) * w; i++)
            {
                FromPlanes(py[i], po[i], pc[i], out float r, out float g, out float b);
                pr[i] = FromSqrt(r);
                pg[i] = FromSqrt(g);
                pb[i] = FromSqrt(b);
            }
        });
        return measured;
    }

    // 8-bit BGRA (in place), on encoded values.
    public static float[][]? Apply8(byte[] buffer, int w, int h, double luma, double color)
    {
        if (!Active(luma, color)) return null;
        const float k = 1f / 255f;
        int n = w * h;
        var py = new float[n];
        var po = new float[n];
        var pc = new float[n];
        var opts = new ParallelOptions();
        Parallel.For(0, h, y =>
        {
            for (int i = y * w; i < (y + 1) * w; i++)
            {
                int o = i * 4;
                ToPlanes(buffer[o + 2] * k, buffer[o + 1] * k, buffer[o] * k, out py[i], out po[i], out pc[i]);
            }
        });
        var measured = DenoisePlanes(py, po, pc, w, h, luma, color, opts);
        Parallel.For(0, h, y =>
        {
            for (int i = y * w; i < (y + 1) * w; i++)
            {
                FromPlanes(py[i], po[i], pc[i], out float r, out float g, out float b);
                int o = i * 4;
                buffer[o + 2] = ToByte(r);
                buffer[o + 1] = ToByte(g);
                buffer[o] = ToByte(b);
            }
        });
        return measured;
    }

    public static readonly Lazy<float[]> SqrtLut = new(() =>
    {
        var lut = new float[65536];
        for (int i = 0; i < lut.Length; i++) lut[i] = MathF.Sqrt(i / 65535f);
        return lut;
    });

    private static ushort FromSqrt(float t)
    {
        if (t <= 0f) return 0;
        float v = t * t * 65535f + 0.5f;
        return v >= 65535f ? (ushort)65535 : (ushort)v;
    }

    private static byte ToByte(float v)
    {
        float f = v * 255f + 0.5f;
        return f <= 0f ? (byte)0 : f >= 255f ? (byte)255 : (byte)f;
    }
}
