using System;
using System.Runtime.CompilerServices;

namespace ImgViewer.Imaging;

public static class Adjust16
{
    private static readonly ConditionalWeakTable<Raw16Image, StretchEntry16> StretchCache = new();

    private sealed class StretchEntry16
    {
        public ushort[]? Value;
    }

    private static readonly float[] MonoDocX = { 0.00f, 0.05f, 0.15f, 0.30f, 0.50f, 0.70f, 0.85f, 0.95f, 1.00f };
    private static readonly float[] MonoDocY = { 0.013f, 0.045f, 0.105f, 0.275f, 0.520f, 0.765f, 0.905f, 0.978f, 1.000f };
    private static readonly float[] MonoTriXx = { 0.00f, 0.03f, 0.12f, 0.30f, 0.50f, 0.70f, 0.85f, 0.94f, 1.00f };
    private static readonly float[] MonoTriXy = { 0.008f, 0.032f, 0.098f, 0.300f, 0.545f, 0.800f, 0.930f, 0.985f, 1.000f };

    public static Raw16Image Apply(Raw16Image source, double shadowLift, double gamma, ManualStretchMap? manual, out HistogramData? histogram, bool autoStretch = true, MonoMode mono = MonoMode.None, int grainSeed = 0, PhotoAdjustments? extra = null)
    {
        int w = source.W;
        int h = source.H;
        if (w <= 0 || h <= 0)
        {
            histogram = null;
            return source;
        }

        var img = source.Clone();

        if (extra is not null && !extra.IsDefault)
        {
            if (extra.Denoise > 0.0005) ApplyDenoise16(img, w, h, extra.Denoise);
            if (extra.ColorNoise > 0.0005) ApplyChromaDenoise16(img, w, h, extra.ColorNoise);
            ApplyColorPoints16(img, extra);
            if (Math.Abs(extra.Highlights) > 0.0005) ApplyHighlights16(img, w, h, extra.Highlights);
            if (Math.Abs(extra.Clarity) > 0.0005) ApplyClarity16(img, w, h, extra.Clarity);
            if (extra.Sharpen > 0.0005) ApplySharpen16(img, w, h, extra.Sharpen);
        }

        bool shadow = Math.Abs(shadowLift) > 0.0001;
        bool hasGamma = Math.Abs(gamma - 1.0) >= 0.0005;

        ushort[]? stretch = null;
        if (shadow && autoStretch)
        {
            var entry = StretchCache.GetOrCreateValue(source);
            stretch = entry.Value;
            if (stretch is null)
            {
                stretch = ComputeStretch16(source);
                entry.Value = stretch;
            }
        }
        if (stretch is not null)
        {
            float k = (float)Math.Min(1.0, Math.Abs(shadowLift) / 0.5);
            ApplyStretch(img, stretch, k);
        }

        if (shadow) ApplyShadow16(img, shadowLift);
        if (hasGamma) ApplyGamma16(img, gamma);
        if (manual is not null && manual.HasEffect) ApplyManual16(img, manual);
        switch (mono)
        {
            case MonoMode.Documentary:
                ApplyMonoCore16(img, 0.325f, 0.605f, 0.070f, MonoDocX, MonoDocY);
                break;
            case MonoMode.TriX:
                ApplyMonoCore16(img, 0.345f, 0.600f, 0.055f, MonoTriXx, MonoTriXy);
                ApplyGrain16(img, grainSeed);
                break;
        }

        histogram = ComputeHistogram16(img);
        return img;
    }

    private static Raw16Image Clone(this Raw16Image src) =>
        new((ushort[])src.R.Clone(), (ushort[])src.G.Clone(), (ushort[])src.B.Clone(), src.W, src.H);

    private static HistogramData ComputeHistogram16(Raw16Image img)
    {
        var lum = new int[256];
        var r = new int[256];
        var g = new int[256];
        var b = new int[256];
        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        int n = pr.Length;
        for (int i = 0; i < n; i++)
        {
            int rv = pr[i];
            int gv = pg[i];
            int bv = pb[i];
            lum[Clamp16((int)((0.2126 * rv + 0.7152 * gv + 0.0722 * bv) + 0.5)) >> 8]++;
            r[rv >> 8]++;
            g[gv >> 8]++;
            b[bv >> 8]++;
        }
        return new HistogramData(lum, r, g, b);
    }

    private static int Clamp16(int v) => v < 0 ? 0 : v > 65535 ? 65535 : v;

    private static unsafe ushort[]? ComputeStretch16(Raw16Image img)
    {
        var hl = new int[65536];
        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        int n = pr.Length;
        for (int i = 0; i < n; i++)
        {
            int li = (int)(0.2126f * pr[i] + 0.7152f * pg[i] + 0.0722f * pb[i] + 0.5f);
            hl[Clamp16(li)]++;
        }

        long loT = (long)(n * 0.005);
        if (loT < 1) loT = 1;
        long hiT = n - loT;

        long cum = 0;
        int loV = 0;
        for (int v = 0; v < 65536; v++)
        {
            cum += hl[v];
            if (cum >= loT)
            {
                loV = v;
                break;
            }
        }
        cum = 0;
        int hiV = 65535;
        for (int v = 0; v < 65536; v++)
        {
            cum += hl[v];
            if (cum > hiT)
            {
                hiV = v - 1;
                break;
            }
        }

        if (!(hiV > loV && hiV - loV >= 16 * 257 && (loV > 0 || 65535.0f / (hiV - loV) > 1.01f)))
            return null;
        return new ushort[] { (ushort)loV, (ushort)hiV };
    }

    private static void ApplyStretch(Raw16Image img, ushort[] st, float k)
    {
        float lo = st[0];
        float scale = 65535.0f / Math.Max(1, st[1] - st[0]);
        float ik = 1.0f - k;
        var lut = new ushort[65536];
        for (int i = 0; i < 65536; i++)
        {
            float x = i;
            float s = (x - lo) * scale;
            if (s < 0.0f) s = 0.0f;
            else if (s > 65535.0f) s = 65535.0f;
            int o = (int)(x * ik + s * k + 0.5f);
            lut[i] = (ushort)(o < 0 ? 0 : o > 65535 ? 65535 : o);
        }
        ApplyChannelLut16(img.R, lut);
        ApplyChannelLut16(img.G, lut);
        ApplyChannelLut16(img.B, lut);
    }

    private static unsafe void ApplyChannelLut16(ushort[] plane, ushort[] lut)
    {
        fixed (ushort* p = plane)
        {
            ushort* end = p + plane.Length;
            for (ushort* q = p; q < end; q++) *q = lut[*q];
        }
    }

    private static float[] BuildSrgbLinF16()
    {
        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
        {
            double c = i / 65535.0;
            lut[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return lut;
    }

    private static ushort[] BuildLinSrgbU16()
    {
        var lut = new ushort[65536];
        for (int i = 0; i < 65536; i++)
        {
            double c = i / 65535.0;
            double s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            lut[i] = (ushort)Math.Clamp(Math.Round(s * 65535.0), 0, 65535);
        }
        return lut;
    }

    private static int LiftIndex(float lin)
    {
        int i = (int)(lin * 1023.0f + 0.5f);
        return i < 0 ? 0 : (i > 1023 ? 1023 : i);
    }

    private static unsafe void ApplyShadow16(Raw16Image img, double strength)
    {
        var lift = new float[1024];
        float exp = 1.0f + (float)(2.0 * Math.Abs(strength));
        lift[0] = 0.0f;
        if (strength >= 0)
        {
            for (int i = 1; i < 1024; i++)
            {
                float x = i / 1023.0f;
                lift[i] = 1.0f - MathF.Pow(1.0f - x, exp);
            }
        }
        else
        {
            for (int i = 1; i < 1024; i++)
            {
                float x = i / 1023.0f;
                lift[i] = MathF.Pow(x, exp);
            }
        }

        const float threshold = 0.25f;
        const float softness = 0.15f;
        float lower = threshold - softness;
        float upper = threshold + softness;
        float denom = upper - lower;

        var srgbLin = BuildSrgbLinF16();
        var linSrgb = BuildLinSrgbU16();

        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        for (int i = 0; i < pr.Length; i++)
        {
            float lr = srgbLin[pr[i]];
            float lg = srgbLin[pg[i]];
            float lb = srgbLin[pb[i]];

            float lum = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
            float ramp = (upper - lum) / denom;
            if (ramp < 0.0f) ramp = 0.0f;
            else if (ramp > 1.0f) ramp = 1.0f;
            float mask = ramp * ramp;
            if (mask <= 0.0f) continue;

            float inv = 1.0f - mask;
            pr[i] = linSrgb[Clamp16((int)((lr * inv + lift[LiftIndex(lr)] * mask) * 65535.0f + 0.5f))];
            pg[i] = linSrgb[Clamp16((int)((lg * inv + lift[LiftIndex(lg)] * mask) * 65535.0f + 0.5f))];
            pb[i] = linSrgb[Clamp16((int)((lb * inv + lift[LiftIndex(lb)] * mask) * 65535.0f + 0.5f))];
        }
    }

    private static unsafe void ApplyGamma16(Raw16Image img, double gamma)
    {
        var lut = new ushort[65536];
        for (int i = 0; i < 65536; i++)
        {
            double v = Math.Pow(i / 65535.0, 1.0 / Math.Max(gamma, 0.05));
            lut[i] = (ushort)Math.Clamp(v * 65535.0 + 0.5, 0, 65535);
        }
        ApplyChannelLut16(img.R, lut);
        ApplyChannelLut16(img.G, lut);
        ApplyChannelLut16(img.B, lut);
    }

    private static unsafe void ApplyManual16(Raw16Image img, ManualStretchMap manual)
    {
        var lut = new ushort[65536];
        for (int i = 0; i < 65536; i++)
        {
            double v = manual.Map(i / 65535.0);
            lut[i] = (ushort)Math.Clamp(Math.Round(v * 65535.0), 0, 65535);
        }
        ApplyChannelLut16(img.R, lut);
        ApplyChannelLut16(img.G, lut);
        ApplyChannelLut16(img.B, lut);
    }

    private static void ApplyMonoCore16(Raw16Image img, float wr, float wg, float wb, float[] ax, float[] ay)
    {
        var lut = new ushort[65536];
        int seg = 0;
        for (int i = 0; i < 65536; i++)
        {
            float x = i / 65535.0f;
            while (seg + 2 < ax.Length && x > ax[seg + 1]) seg++;
            float t = (x - ax[seg]) / (ax[seg + 1] - ax[seg]);
            if (t < 0.0f) t = 0.0f;
            else if (t > 1.0f) t = 1.0f;
            float v = ay[seg] + t * (ay[seg + 1] - ay[seg]);
            lut[i] = (ushort)Math.Clamp(Math.Round(v * 65535.0f), 0, 65535);
        }

        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        for (int i = 0; i < pr.Length; i++)
        {
            float gray = wr * pr[i] + wg * pg[i] + wb * pb[i];
            int gi = (int)(gray + 0.5f);
            if (gi < 0) gi = 0;
            else if (gi > 65535) gi = 65535;
            ushort o = lut[gi];
            pr[i] = o;
            pg[i] = o;
            pb[i] = o;
        }
    }

    private static unsafe void ApplyGrain16(Raw16Image img, int seed)
    {
        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        int pixels = pr.Length;
        for (int i = 0; i < pixels; i++)
        {
            uint n = (uint)i * 1597334677u ^ (uint)seed;
            n = (n ^ (n >> 13)) * 1274126177u;
            n ^= n >> 16;
            uint n2 = (n ^ (n >> 11)) * 2654434677u;
            n2 ^= n2 >> 15;
            float t = ((n & 0xFFFF) / 65535.0f) - ((n2 & 0xFFFF) / 65535.0f);
            int gray = (pr[i] + pg[i] + pb[i]) / 3;
            float fg = gray / 65535.0f;
            float weight = 4.0f * fg * (1.0f - fg);
            int v = gray + (int)MathF.Round(t * 17.5f * 257.0f * weight);
            if (v < 0) v = 0;
            else if (v > 65535) v = 65535;
            ushort o = (ushort)v;
            pr[i] = o;
            pg[i] = o;
            pb[i] = o;
        }
    }

    private static void ApplyDenoise16(Raw16Image img, int w, int h, double denoise)
    {
        double n = Math.Clamp(denoise, 0.0, 1.0);
        if (n <= 0.0005) return;
        int r = n < 0.34 ? 1 : (n < 0.67 ? 2 : 3);
        float blend = (float)(0.15 + 0.55 * n);
        BlurMix(img, w, h, r, blend);
    }

    private static void ApplyChromaDenoise16(Raw16Image img, int w, int h, double colorNoise)
    {
        double n = Math.Clamp(colorNoise, 0.0, 1.0);
        if (n <= 0.0005) return;
        int r = n < 0.34 ? 1 : (n < 0.67 ? 2 : 3);
        float blend = (float)(0.15 + 0.65 * n);

        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        int count = pr.Length;
        var blr = new ushort[count];
        var blg = new ushort[count];
        var blb = new ushort[count];
        var tmpC = new ushort[count];
        BoxBlurPlane(pr, tmpC, w, h, r, vertical: false);
        BoxBlurPlane(tmpC, blr, w, h, r, vertical: true);
        BoxBlurPlane(pg, tmpC, w, h, r, vertical: false);
        BoxBlurPlane(tmpC, blg, w, h, r, vertical: true);
        BoxBlurPlane(pb, tmpC, w, h, r, vertical: false);
        BoxBlurPlane(tmpC, blb, w, h, r, vertical: true);

        var lum0 = new float[count];
        var lumB = new float[count];
        ComputeLuma16(pr, pg, pb, lum0);
        ComputeLuma16(blr, blg, blb, lumB);

        for (int i = 0; i < count; i++)
        {
            pr[i] = MixUshort(pr[i], (int)Math.Round(lum0[i] + (blr[i] - lumB[i])), blend);
            pg[i] = MixUshort(pg[i], (int)Math.Round(lum0[i] + (blg[i] - lumB[i])), blend);
            pb[i] = MixUshort(pb[i], (int)Math.Round(lum0[i] + (blb[i] - lumB[i])), blend);
        }
    }

    private static void BlurMix(Raw16Image img, int w, int h, int r, float blend)
    {
        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        int count = pr.Length;
        var tmp = new ushort[count];
        var blr = new ushort[count];
        var blg = new ushort[count];
        var blb = new ushort[count];
        BoxBlurPlane(pr, tmp, w, h, r, vertical: false);
        BoxBlurPlane(tmp, blr, w, h, r, vertical: true);
        BoxBlurPlane(pg, tmp, w, h, r, vertical: false);
        BoxBlurPlane(tmp, blg, w, h, r, vertical: true);
        BoxBlurPlane(pb, tmp, w, h, r, vertical: false);
        BoxBlurPlane(tmp, blb, w, h, r, vertical: true);
        for (int i = 0; i < count; i++)
        {
            pr[i] = MixUshort(pr[i], blr[i], blend);
            pg[i] = MixUshort(pg[i], blg[i], blend);
            pb[i] = MixUshort(pb[i], blb[i], blend);
        }
    }

    private static unsafe void BoxBlurPlane(ushort[] src, ushort[] dst, int w, int h, int r, bool vertical)
    {
        int window = 2 * r + 1;
        if (!vertical)
        {
            for (int y = 0; y < h; y++)
            {
                int rowBase = y * w;
                long s = 0;
                for (int x = -r; x <= r; x++) s += src[rowBase + ClampI(x, 0, w - 1)];
                for (int x = 0; x < w; x++)
                {
                    dst[rowBase + x] = (ushort)(s / window);
                    s += src[rowBase + ClampI(x + r + 1, 0, w - 1)] - src[rowBase + ClampI(x - r, 0, w - 1)];
                }
            }
        }
        else
        {
            for (int x = 0; x < w; x++)
            {
                long s = 0;
                for (int y = -r; y <= r; y++) s += src[ClampI(y, 0, h - 1) * w + x];
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = (ushort)(s / window);
                    s += src[ClampI(y + r + 1, 0, h - 1) * w + x] - src[ClampI(y - r, 0, h - 1) * w + x];
                }
            }
        }
    }

    private static void ComputeLuma16(ushort[] r, ushort[] g, ushort[] b, float[] lum)
    {
        for (int i = 0; i < lum.Length; i++)
            lum[i] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
    }

    private static ushort MixUshort(int a, int b, float t)
    {
        int v = (int)(a + (b - a) * t + 0.5f);
        return (ushort)(v < 0 ? 0 : v > 65535 ? 65535 : v);
    }

    private static ushort ClampU(float v)
    {
        int i = (int)(v + 0.5f);
        return (ushort)(i < 0 ? 0 : i > 65535 ? 65535 : i);
    }

    private static void BuildChannelLuts16(PhotoAdjustments a, out ushort[] lutR, out ushort[] lutG, out ushort[] lutB)
    {
        double gainR = 1.0 + 0.30 * a.Temperature;
        double gainB = 1.0 - 0.30 * a.Temperature;
        double gainG = 1.0;
        if (a.Temperature < 0)
        {
            double k = -a.Temperature;
            gainR = 1.0 - 0.10 * k;
            gainB = 1.0 + 0.30 * k;
            gainG = 1.0 + 0.02 * k;
        }
        double evGain = Math.Pow(2.0, a.Exposure);
        double hazeA = 0.10 * Math.Clamp(a.Dehaze, 0.0, 1.0);

        lutR = BuildChannelLut16(gainR * evGain, hazeA, a.Contrast);
        lutG = BuildChannelLut16(gainG * evGain, hazeA, a.Contrast);
        lutB = BuildChannelLut16(gainB * evGain, hazeA, a.Contrast);
    }

    private static ushort[] BuildChannelLut16(double gain, double hazeA, double contrast)
    {
        var lut = new ushort[65536];
        bool posC = contrast > 0.0001;
        bool negC = contrast < -0.0001;
        double k = 1.0 + Math.Max(contrast, 0.0) * 7.0;
        double kNorm = Math.Tanh(0.5 * k);
        double flatten = 1.0 + Math.Min(contrast, 0.0) * 0.55;

        for (int i = 0; i < 65536; i++)
        {
            double v = i / 65535.0 * gain;
            if (hazeA > 0.0) v = (v - hazeA) / (1.0 - hazeA);
            if (posC)
            {
                v = 0.5 + 0.5 * (Math.Tanh((v - 0.5) * k) / kNorm);
            }
            else if (negC)
            {
                v = 0.5 + (v - 0.5) * flatten;
            }
            lut[i] = (ushort)Math.Clamp(Math.Round(v * 65535.0), 0, 65535);
        }
        return lut;
    }

    private static void ApplyColorPoints16(Raw16Image img, PhotoAdjustments a)
    {
        BuildChannelLuts16(a, out ushort[] lutR, out ushort[] lutG, out ushort[] lutB);

        float dehazeBoost = (float)(Math.Clamp(a.Dehaze, 0.0, 1.0) * 0.30);
        float sat = (float)Math.Clamp(a.Saturation * (1.0 + dehazeBoost), 0.0, 3.0);

        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        for (int i = 0; i < pr.Length; i++)
        {
            float r = lutR[pr[i]];
            float g = lutG[pg[i]];
            float b = lutB[pb[i]];
            if (sat != 1.0f)
            {
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                b = lum + (b - lum) * sat;
                g = lum + (g - lum) * sat;
                r = lum + (r - lum) * sat;
            }
            pr[i] = ClampU(r);
            pg[i] = ClampU(g);
            pb[i] = ClampU(b);
        }
    }

    private static unsafe void ApplyHighlights16(Raw16Image img, int w, int h, double highlights)
    {
        double v = Math.Clamp(highlights, -1.0, 1.0);
        if (Math.Abs(v) <= 0.0005) return;
        float exp = 1.0f + (float)(2.0 * Math.Abs(v));

        const float threshold = 0.72f;
        const float softness = 0.16f;
        float lower = threshold - softness;
        float upper = threshold + softness;
        float denom = upper - lower;

        var srgbLin = BuildSrgbLinF16();
        var linSrgb = BuildLinSrgbU16();

        var lift = new float[1024];
        if (v > 0)
        {
            for (int i = 0; i < 1024; i++)
                lift[i] = 1.0f - MathF.Pow(1.0f - i / 1023.0f, exp);
        }
        else
        {
            for (int i = 1; i < 1024; i++)
                lift[i] = MathF.Pow(i / 1023.0f, exp);
            lift[0] = 0.0f;
        }

        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        for (int i = 0; i < pr.Length; i++)
        {
            float lr = srgbLin[pr[i]];
            float lg = srgbLin[pg[i]];
            float lb = srgbLin[pb[i]];

            float lum = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
            float ramp = (lum - lower) / denom;
            if (ramp < 0.0f) ramp = 0.0f;
            else if (ramp > 1.0f) ramp = 1.0f;
            float mask = ramp * ramp;
            if (mask <= 0.0f) continue;

            float inv = 1.0f - mask;
            pr[i] = linSrgb[Clamp16((int)((lr * inv + lift[LiftIndex(lr)] * mask) * 65535.0f + 0.5f))];
            pg[i] = linSrgb[Clamp16((int)((lg * inv + lift[LiftIndex(lg)] * mask) * 65535.0f + 0.5f))];
            pb[i] = linSrgb[Clamp16((int)((lb * inv + lift[LiftIndex(lb)] * mask) * 65535.0f + 0.5f))];
        }
    }

    private static unsafe void ApplySharpen16(Raw16Image img, int w, int h, double sharpen)
    {
        double amt = Math.Clamp(sharpen, 0.0, 1.0);
        if (amt <= 0.0005) return;
        int r = Math.Clamp((int)Math.Round(Math.Min(w, h) * 0.0005), 1, 3);

        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        var lum = new float[pr.Length];
        ComputeLuma16(pr, pg, pb, lum);
        var low = new float[pr.Length];
        var low2 = new float[pr.Length];
        BoxBlurFloatPass(lum, low, w, h, r, vertical: false);
        BoxBlurFloatPass(low, low2, w, h, r, vertical: true);

        float a = (float)(amt * 1.15);
        for (int i = 0; i < pr.Length; i++)
        {
            float delta = (lum[i] - low2[i]) * a;
            pr[i] = ClampU(pr[i] + delta);
            pg[i] = ClampU(pg[i] + delta);
            pb[i] = ClampU(pb[i] + delta);
        }
    }

    private static unsafe void ApplyClarity16(Raw16Image img, int w, int h, double clarity)
    {
        double amt = Math.Clamp(clarity, -1.0, 1.0);
        if (Math.Abs(amt) <= 0.0005) return;
        int r = Math.Clamp((int)Math.Round(Math.Min(w, h) * 0.006), 2, 40);

        var pr = img.R;
        var pg = img.G;
        var pb = img.B;
        var lum = new float[pr.Length];
        ComputeLuma16(pr, pg, pb, lum);
        var low = new float[pr.Length];
        var low2 = new float[pr.Length];
        BoxBlurFloatPass(lum, low, w, h, r, vertical: false);
        BoxBlurFloatPass(low, low2, w, h, r, vertical: true);

        float a = (float)amt;
        for (int i = 0; i < pr.Length; i++)
        {
            float delta = (lum[i] - low2[i]) * a;
            pr[i] = ClampU(pr[i] + delta);
            pg[i] = ClampU(pg[i] + delta);
            pb[i] = ClampU(pb[i] + delta);
        }
    }

    private static unsafe void BoxBlurFloatPass(float[] src, float[] dst, int w, int h, int r, bool vertical)
    {
        fixed (float* srcP = src, dstP = dst)
        {
            float wf = 2 * r + 1;
            if (!vertical)
            {
                for (int y = 0; y < h; y++)
                {
                    float* row = srcP + y * w;
                    float* outRow = dstP + y * w;
                    float s = 0;
                    for (int x = -r; x <= r; x++) s += row[ClampI(x, 0, w - 1)];
                    for (int x = 0; x < w; x++)
                    {
                        outRow[x] = s / wf;
                        s += row[ClampI(x + r + 1, 0, w - 1)] - row[ClampI(x - r, 0, w - 1)];
                    }
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int y = -r; y <= r; y++) s += srcP[ClampI(y, 0, h - 1) * w + x];
                    for (int y = 0; y < h; y++)
                    {
                        dstP[y * w + x] = s / wf;
                        s += srcP[ClampI(y + r + 1, 0, h - 1) * w + x] - srcP[ClampI(y - r, 0, h - 1) * w + x];
                    }
                }
            }
        }
    }

    private static int ClampI(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}