using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImgViewer.Imaging;

public static class LinearRender
{
    private static readonly int Parts = Math.Max(1, Environment.ProcessorCount);
    private static readonly Lazy<ushort[]> LinToSrgbU16Lut = new(BuildLinToSrgbU16);

    public static ushort[] LinToSrgbLut => LinToSrgbU16Lut.Value;

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

    public static Raw16Image ApplyGain(Raw16Image src, double ev, double temperature, CancellationToken ct = default)
    {
        double evGain = Math.Pow(2.0, ev);
        var (gainR, gainG, gainB) = PhotoOps.TempGains(temperature);

        float gr = (float)(evGain * gainR);
        float gg = (float)(evGain * gainG);
        float gb = (float)(evGain * gainB);

        int length = src.R.Length;
        var outImg = new Raw16Image(new ushort[length], new ushort[length], new ushort[length], src.W, src.H);
        var sR = src.R;
        var sG = src.G;
        var sB = src.B;
        var dR = outImg.R;
        var dG = outImg.G;
        var dB = outImg.B;

        if (length <= 0) return outImg;
        ct.ThrowIfCancellationRequested();

        if (Parts <= 1 || length < 16384)
        {
            GainChunk(0, length, gr, gg, gb);
            return outImg;
        }

        int chunk = (length + Parts - 1) / Parts;
        var opts = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, Parts, opts, p =>
        {
            int s = p * chunk;
            int e = Math.Min(length, s + chunk);
            if (s < e) GainChunk(s, e, gr, gg, gb);
        });
        return outImg;

        void GainChunk(int s, int e, float cr, float cg, float cb)
        {
            for (int i = s; i < e; i++)
            {
                dR[i] = ClampU(sR[i] * cr);
                dG[i] = ClampU(sG[i] * cg);
                dB[i] = ClampU(sB[i] * cb);
            }
        }
    }

    private static ushort ClampU(float v)
    {
        int i = (int)(v + 0.5f);
        return (ushort)(i < 0 ? 0 : i > 65535 ? 65535 : i);
    }
}