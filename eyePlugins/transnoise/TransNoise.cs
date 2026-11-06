using System;
using System.Threading.Tasks;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.TransNoise;

/// <summary>
/// Experimental trans-noise: instead of removing noise it changes its character. Coarse, irregular noise (blotches of
/// 2–16 px, typical of high ISO, pushed shadows and in-camera noise reduction) is taken out of the luma by a soft
/// shrink of the coarse band-pass layers, and the luma energy that was taken out is put back as fine, even, monochrome
/// grain in the same places — so the photo keeps its texture and does not look plastic. Color blotches are smoothed
/// away (pleasant grain has no color). Runs on the display image (after the develop pipeline, before crop).
/// </summary>
public sealed class TransNoisePlugin : IEyePlugin
{
    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "photographereye.transnoise",
        Name = "Experimental trans-noise",
        Version = "1.0",
        Author = "PhotographerEye",
        Description = "Turns coarse, irregular noise into fine, even grain that is pleasant to the eye instead of removing it (experimental)."
    };

    public void Initialize(IPluginHost host)
    {
        // A single instance, hooked up like every other panel tool: it works on the whole photo, or — chosen in
        // its Mask row — inside an existing mask of the photo. The id stays "transnoise", so edits saved with the
        // earlier versions still load (areas 2–6 of the old multi-area layout are gone; their values go dormant).
        host.AddFilter(new FilterDescriptor
        {
            Id = "transnoise",
            Label = "Trans-noise",
            Description = "Turns coarse blotchy noise into fine grain (whole photo, or inside the mask chosen in its Mask row)",
            Group = "Detail",
            Stage = FilterStage.Display,
            Order = 1,
            SupportsMask = true,
            Params = TransNoise.Params(),
            IsIdentity = TransNoise.IsIdentity,
            Render = (img, p, ctx) => TransNoise.Render(img, p, ctx)
        });
    }
}

public static class TransNoise
{
    /// <summary>Gaussian scales (full-resolution px) that bound the coarse bands: [1–2], [2–4], [4–8], [8–16].</summary>
    public static readonly double[] Sigmas = { 1, 2, 4, 8, 16 };
    /// <summary>Shrink threshold at strength 100, in units of the measured band noise.</summary>
    public const double MaxK = 3.0;
    /// <summary>Color blotch threshold at 100, in units of the measured chroma band noise.</summary>
    public const double MaxKc = 5.0;
    /// <summary>Spread of the removed-energy map (full-resolution px): the grain follows where the blotches were.</summary>
    public const double EnergySigma = 6;
    private const uint Seed = 0x9E3779B9u;

    public static ParamDef[] Params() => new[]
    {
        Tip(ParamDef.Slider("amount", "Strength", 0, 100, 0), "How much coarse noise is turned into grain"),
        Tip(ParamDef.Slider("grain", "Grain", 0, 200, 100), "Amount of fine grain put back, % of the coarse noise taken out"),
        Tip(ParamDef.Slider("size", "Grain size", 0, 100, 40), "Size of the grain (0 = one pixel)"),
        Tip(ParamDef.Slider("color", "Color blotches", 0, 100, 60), "Smooths away colored blotches")
    };

    private static ParamDef Tip(ParamDef d, string tip) { d.Tooltip = tip; return d; }

    public static bool IsIdentity(FilterParams p) => p.Get("amount") < 0.5;

    public static void Render(PluginImage img, FilterParams p, RenderContext ctx) =>
        Apply(img, p.Get("amount") / 100.0, p.Get("grain") / 100.0, p.Get("size") / 100.0, p.Get("color") / 100.0, ctx.Scale);

    /// <summary>Grain sigma in full-resolution px for the size slider (0..1).</summary>
    public static double GrainSigma(double size) => 0.35 + 0.65 * Clamp(size, 0, 1);

    /// <param name="strength">0..1, coarse shrink threshold up to <see cref="MaxK"/>.</param>
    /// <param name="grain">0..2, grain energy relative to the removed coarse energy.</param>
    /// <param name="size">0..1, grain size.</param>
    /// <param name="color">0..1, color blotch smoothing.</param>
    /// <param name="scale">Buffer px per full-resolution px (previews are downscaled).</param>
    public static void Apply(PluginImage img, double strength, double grain, double size, double color, double scale)
    {
        int w = img.Width, h = img.Height, n = w * h;
        strength = Clamp(strength, 0, 1);
        if (strength <= 0) return;
        scale = scale > 0 && scale <= 1 ? scale : 1;
        float[] R = img.R, G = img.G, B = img.B;

        var a = new float[n];
        var b = new float[n];
        var tmp = new float[n];
        var rem = new float[n];
        var dY = new float[n];

        // ---- Luma: coarse bands shrunk, removed energy measured.
        var y0 = new float[n];
        Parallel.For(0, h, y => { int o = y * w; for (int x = 0; x < w; x++, o++) y0[o] = Luma(R[o], G[o], B[o]); });
        ShrinkBands(y0, w, h, scale, strength * MaxK, firstBand: 1, a, b, tmp, rem, energy: dY);

        // Energy map (local RMS of what was removed) -> grain amplitude; dY holds the energy so far.
        Gaussian(dY, a, tmp, w, h, EnergySigma * scale);
        // Grain: deterministic white noise on full-resolution coordinates, blurred to the grain size, unit std.
        double gs = GrainSigma(size);
        double gsb = gs * scale;
        Parallel.For(0, h, y =>
        {
            int fy = (int)Math.Floor((y + 0.5) / scale), o = y * w;
            for (int x = 0; x < w; x++, o++) b[o] = Noise((int)Math.Floor((x + 0.5) / scale), fy);
        });
        if (gsb >= 0.5) Gaussian(b, b, tmp, w, h, Math.Sqrt(gsb * gsb - 0.25));
        double sd = Std(b);
        // Downscaled previews average the full-resolution grain: weaker, like the photo viewed at that size.
        double att = Math.Min(1.0, 2.0 * gsb);
        float gain = (float)(Clamp(grain, 0, 2) * att / Math.Max(1e-9, sd));
        Parallel.For(0, h, y =>
        {
            int o = y * w;
            for (int x = 0; x < w; x++, o++)
            {
                float l = y0[o];
                // Film-like: most grain in the midtones, none in pure black and white.
                float wl = l <= 0f || l >= 1f ? 0f : (float)Math.Sqrt(4f * l * (1f - l));
                dY[o] = -rem[o] + gain * (float)Math.Sqrt(Math.Max(0f, a[o])) * b[o] * wl;
            }
        });

        // ---- Chroma: color blotches (all bands, fine included) shrunk.
        float[] dCr = null, dCb = null;
        if (color > 0.005)
        {
            double kc = Clamp(color, 0, 1) * MaxKc;
            dCr = new float[n];
            dCb = new float[n];
            for (int ch = 0; ch < 2; ch++)
            {
                float[] src = ch == 0 ? R : B;
                Parallel.For(0, n, i => y0[i] = src[i] - Luma(R[i], G[i], B[i]));
                ShrinkBands(y0, w, h, scale, kc, firstBand: 0, a, b, tmp, ch == 0 ? dCr : dCb, energy: null);
            }
        }

        // ---- Back to RGB: Y' = Y + dY, R' - Y' = Cr - removed (same for B), G from Y'.
        Parallel.For(0, n, i =>
        {
            float cr = dCr == null ? 0f : -dCr[i], cb = dCb == null ? 0f : -dCb[i];
            float d = dY[i];
            float r = R[i] + d + cr, bb = B[i] + d + cb;
            float g = G[i] + d - (0.2126f * cr + 0.0722f * cb) / 0.7152f;
            R[i] = r > 0f ? r : 0f;
            G[i] = g > 0f ? g : 0f;
            B[i] = bb > 0f ? bb : 0f;
        });
    }

    /// <summary>
    /// Splits <paramref name="plane"/> into band-pass layers between the <see cref="Sigmas"/> (band 0 = plane − G(1px),
    /// band j = G(σj) − G(σj+1)) and soft-shrinks bands from <paramref name="firstBand"/> on with a Wiener-like gain
    /// d²/(d² + (k·σn)²), σn the robust (MAD) noise of the band. <paramref name="removed"/> gets the sum of what was taken
    /// out, <paramref name="energy"/> (optional) its squared sum per pixel. <paramref name="plane"/> is not changed.
    /// </summary>
    private static void ShrinkBands(float[] plane, int w, int h, double scale, double k, int firstBand,
                                    float[] a, float[] b, float[] tmp, float[] removed, float[] energy)
    {
        int n = w * h;
        Array.Clear(removed, 0, n);
        if (energy != null) Array.Clear(energy, 0, n);
        // a = current (finer) level, b = next (coarser) level.
        Array.Copy(plane, a, n);
        double prev = 0;
        for (int j = 0; j < Sigmas.Length; j++)
        {
            double s = Sigmas[j] * scale;
            double inc = Math.Sqrt(Math.Max(0, s * s - prev * prev));
            prev = s;
            Gaussian(a, b, tmp, w, h, inc);
            if (j >= firstBand && k > 0)
            {
                float sn = (float)NoiseSigma(a, b);
                float t2 = (float)(k * k) * sn * sn;
                if (t2 > 0f)
                {
                    Parallel.For(0, n, i =>
                    {
                        float d = a[i] - b[i];
                        float r = d * t2 / (d * d + t2);
                        removed[i] += r;
                        if (energy != null) energy[i] += r * r;
                    });
                }
            }
            var t = a; a = b; b = t;
        }
    }

    /// <summary>Robust noise std of the band a − b: 1.4826 · median |a − b| over a subsample.</summary>
    private static double NoiseSigma(float[] a, float[] b)
    {
        int n = a.Length;
        int step = Math.Max(1, n / 200000);
        var v = new float[(n + step - 1) / step];
        int m = 0;
        for (int i = 0; i < n; i += step) v[m++] = Math.Abs(a[i] - b[i]);
        Array.Sort(v, 0, m);
        return 1.4826 * v[m / 2];
    }

    private static double Std(float[] p)
    {
        double s = 0, s2 = 0;
        for (int i = 0; i < p.Length; i++) { s += p[i]; s2 += p[i] * (double)p[i]; }
        double mean = s / p.Length;
        return Math.Sqrt(Math.Max(0, s2 / p.Length - mean * mean));
    }

    /// <summary>Uniform-ish noise in −1..1 (sum of two uniforms: triangular) from a hash of the pixel position.</summary>
    public static float Noise(int x, int y)
    {
        uint hh = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ Seed;
        hh ^= hh >> 16; hh *= 0x7FEB352Du; hh ^= hh >> 15; hh *= 0x846CA68Bu; hh ^= hh >> 16;
        float u1 = (hh & 0xFFFF) / 65535f, u2 = (hh >> 16) / 65535f;
        return u1 + u2 - 1f;
    }

    // ---------------------------------------------------------------- blur

    /// <summary>Gaussian blur approximated by three box blurs (separable, running sums). src may equal dst.</summary>
    public static void Gaussian(float[] src, float[] dst, float[] tmp, int w, int h, double sigma)
    {
        if (sigma < 0.3)
        {
            if (!ReferenceEquals(src, dst)) Array.Copy(src, dst, w * h);
            return;
        }
        // Box sizes for 3 passes (W. Jarosz / P. Kovesi).
        const int passes = 3;
        double wIdeal = Math.Sqrt(12 * sigma * sigma / passes + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        int mm = (int)Math.Round((12 * sigma * sigma - passes * wl * wl - 4 * passes * wl - 3 * passes) / (-4.0 * wl - 4));
        var cur = src;
        for (int i = 0; i < passes; i++)
        {
            int r = ((i < mm ? wl : wu) - 1) / 2;
            BoxH(cur, tmp, w, h, r);
            BoxV(tmp, dst, w, h, r);
            cur = dst;
        }
    }

    private static void BoxH(float[] s, float[] d, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            int o = y * w;
            double sum = 0;
            for (int k = -r; k <= r; k++) sum += s[o + Clampi(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                d[o + x] = (float)(sum * inv);
                sum += s[o + Clampi(x + r + 1, 0, w - 1)] - s[o + Clampi(x - r, 0, w - 1)];
            }
        });
    }

    private static void BoxV(float[] s, float[] d, int w, int h, int r)
    {
        const int Block = 64;
        float inv = 1f / (2 * r + 1);
        int blocks = (w + Block - 1) / Block;
        Parallel.For(0, blocks, bi =>
        {
            int x0 = bi * Block, x1 = Math.Min(w, x0 + Block), bw = x1 - x0;
            var sum = new double[bw];
            for (int k = -r; k <= r; k++)
            {
                int row = Clampi(k, 0, h - 1) * w;
                for (int x = 0; x < bw; x++) sum[x] += s[row + x0 + x];
            }
            for (int y = 0; y < h; y++)
            {
                int o = y * w + x0;
                int add = Clampi(y + r + 1, 0, h - 1) * w + x0, sub = Clampi(y - r, 0, h - 1) * w + x0;
                for (int x = 0; x < bw; x++)
                {
                    d[o + x] = (float)(sum[x] * inv);
                    sum[x] += s[add + x] - s[sub + x];
                }
            }
        });
    }

    private static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
    private static int Clampi(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
