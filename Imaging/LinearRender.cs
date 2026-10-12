using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace ImgViewer.Imaging;

public static class LinearRender
{
    private const double WhiteTarget = 1.0;
    private const double WhitePercentile = 0.999;
    private const double MinEv = -1.5;
    private const double MaxEv = 2.5;

    private const float ToeGamma = 0.75f;
    private const float ShoulderStart = 0.60f;
    private const float ShoulderEnd = 0.92f;
    private const float HighlightDesatStart = 0.80f;
    private const float HighlightDesatFull = 0.98f;

    private static readonly int Parts = Math.Max(1, Environment.ProcessorCount);
    private static readonly Lazy<float[]> SrgbToLinLut = new(BuildSrgbToLin);
    private static readonly Lazy<ushort[]> LinToSrgbU16Lut = new(BuildLinToSrgbU16);
    private static readonly Lazy<int> DesatThresholdEnc = new(() => Encode(HighlightDesatStart));
    private static readonly ConcurrentDictionary<long, ushort[]> CurveLutCache = new();

    private static float[] BuildSrgbToLin()
    {
        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
        {
            double c = i / 65535.0;
            lut[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return lut;
    }

    private static ushort[] BuildLinToSrgbU16()
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

    public static double ComputeBaseEv(Raw16Image srgb)
    {
        int n = srgb.R.Length;
        if (n == 0) return 0.0;
        var pr = srgb.R;
        var pg = srgb.G;
        var pb = srgb.B;
        var toLin = SrgbToLinLut.Value;

        int parts = Math.Min(Parts, Math.Max(1, n));
        var hists = new int[parts][];
        int chunk = (n + parts - 1) / parts;
        Parallel.For(0, parts, p =>
        {
            int s = p * chunk;
            int e = Math.Min(n, s + chunk);
            var hl = new int[65536];
            for (int i = s; i < e; i++)
            {
                float lum = 0.2126f * toLin[pr[i]] + 0.7152f * toLin[pg[i]] + 0.0722f * toLin[pb[i]];
                int b = (int)(lum * 65535.0f + 0.5f);
                if (b < 0) b = 0;
                else if (b > 65535) b = 65535;
                hl[b]++;
            }
            hists[p] = hl;
        });

        var hist = hists[0];
        for (int p = 1; p < parts; p++)
        {
            var hp = hists[p];
            if (hp is null) continue;
            for (int v = 0; v < 65536; v++) hist[v] += hp[v];
        }

        long target = (long)(n * WhitePercentile);
        if (target < 1) target = 1;
        long cum = 0;
        int white = 65535;
        for (int v = 65535; v >= 0; v--)
        {
            cum += hist[v];
            if (cum >= n - target)
            {
                white = v;
                break;
            }
        }
        double w = white / 65535.0;
        if (w <= 1e-4) return MaxEv;
        double ev = Math.Log2(WhiteTarget / w);
        return Math.Clamp(ev, MinEv, MaxEv);
    }

    public static Raw16Image ApplyGain(Raw16Image src, double ev, double temperature, CancellationToken ct = default)
    {
        double evGain = Math.Pow(2.0, ev);
        var (gainR, gainG, gainB) = PhotoOps.TempGains(temperature);

        float gr = (float)(evGain * gainR);
        float gg = (float)(evGain * gainG);
        float gb = (float)(evGain * gainB);

        var toLin = SrgbToLinLut.Value;
        var lutR = GetCurveLut(gr);
        var lutG = GetCurveLut(gg);
        var lutB = GetCurveLut(gb);
        int desatEnc = DesatThresholdEnc.Value;

        var outImg = new Raw16Image(
            new ushort[src.R.Length],
            new ushort[src.G.Length],
            new ushort[src.B.Length],
            src.W, src.H);

        var sR = src.R;
        var sG = src.G;
        var sB = src.B;
        var dR = outImg.R;
        var dG = outImg.G;
        var dB = outImg.B;

        int length = sR.Length;
        if (length <= 0) return outImg;

        if (Parts <= 1 || length < 16384)
        {
            ct.ThrowIfCancellationRequested();
            RenderChunk(0, length, sR, sG, sB, dR, dG, dB, toLin, lutR, lutG, lutB, gr, gg, gb, desatEnc);
            return outImg;
        }

        int chunk = (length + Parts - 1) / Parts;
        var opts = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, Parts, opts, p =>
        {
            int s = p * chunk;
            int e = Math.Min(length, s + chunk);
            if (s < e) RenderChunk(s, e, sR, sG, sB, dR, dG, dB, toLin, lutR, lutG, lutB, gr, gg, gb, desatEnc);
        });
        return outImg;
    }

    private static void RenderChunk(int s, int e, ushort[] sR, ushort[] sG, ushort[] sB, ushort[] dR, ushort[] dG, ushort[] dB,
        float[] toLin, ushort[] lutR, ushort[] lutG, ushort[] lutB, float gr, float gg, float gb, int desatEnc)
    {
        for (int i = s; i < e; i++)
        {
            ushort vr = sR[i];
            ushort vg = sG[i];
            ushort vb = sB[i];

            ushort er = lutR[vr];
            ushort eg = lutG[vg];
            ushort eb = lutB[vb];

            bool clipped = vr >= 65535 || vg >= 65535 || vb >= 65535;
            int emx = er > eg ? er : eg;
            if (eb > emx) emx = eb;
            if (!clipped && emx < desatEnc)
            {
                dR[i] = er;
                dG[i] = eg;
                dB[i] = eb;
                continue;
            }

            float r = toLin[vr] * gr;
            float g = toLin[vg] * gg;
            float b = toLin[vb] * gb;

            if (clipped)
            {
                bool cR = vr >= 65535;
                bool cG = vg >= 65535;
                bool cB = vb >= 65535;
                float m = 0f;
                bool any = false;
                if (!cR) { if (r > m) m = r; any = true; }
                if (!cG) { if (g > m) m = g; any = true; }
                if (!cB) { if (b > m) m = b; any = true; }
                if (!any) m = 1f;
                if (cR) r = m;
                if (cG) g = m;
                if (cB) b = m;
            }

            float mx = r > g ? r : g;
            if (b > mx) mx = b;
            if (mx > HighlightDesatStart)
            {
                float t = (mx - HighlightDesatStart) / (HighlightDesatFull - HighlightDesatStart);
                if (t > 1f) t = 1f;
                t = t * t * (3f - 2f * t);
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                r += (lum - r) * t;
                g += (lum - g) * t;
                b += (lum - b) * t;
            }

            dR[i] = Encode(r);
            dG[i] = Encode(g);
            dB[i] = Encode(b);
        }
    }

    private static ushort Encode(float lin)
    {
        lin = BaseCurve(lin);
        int idx = (int)(lin * 65535.0f + 0.5f);
        if (idx < 0) idx = 0;
        else if (idx > 65535) idx = 65535;
        return LinToSrgbU16Lut.Value[idx];
    }

    private static float BaseCurve(float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        float f = (float)Math.Pow(x, ToeGamma);
        float s = (x - ShoulderStart) / (ShoulderEnd - ShoulderStart);
        if (s <= 0f) return f;
        if (s >= 1f) return x;
        s = s * s * (3f - 2f * s);
        return f * (1f - s) + x * s;
    }

    private static ushort[] GetCurveLut(float gain)
    {
        long key = (long)Math.Round(gain * 1000000.0);
        return CurveLutCache.GetOrAdd(key, _ => BuildCurveLut(gain));
    }

    private static ushort[] BuildCurveLut(float gain)
    {
        var toLin = SrgbToLinLut.Value;
        var lut = new ushort[65536];
        for (int i = 0; i < 65536; i++)
        {
            lut[i] = Encode(toLin[i] * gain);
        }
        return lut;
    }
}
