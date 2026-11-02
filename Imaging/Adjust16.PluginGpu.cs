using System;
#if !PHOTOGRAPHEREYE_NO_GPU
using ComputeSharp;
#endif

namespace ImgViewer.Imaging;

/// <summary>GPU services exposed to plugins (IImagingServices). Falls back to CPU in the Win7 build.</summary>
public static partial class Adjust16
{
#if !PHOTOGRAPHEREYE_NO_GPU
    /// <summary>Repeated box blurs (one H+V pass per radius) of a float plane on the GPU. Returns false when no GPU is usable.</summary>
    public static bool TryBoxBlurGpu(float[] plane, int w, int h, int[] radii, out float[]? result)
    {
        result = null;
        if (!GpuEnabled || w <= 0 || h <= 0 || (long)w * h > int.MaxValue / 2) return false;
        if ((long)w * h < 250_000) return false;
        if (!EnsureGpu() || _gpu is null) return false;
        lock (GpuLock)
        {
            try
            {
                int n = w * h;
                using var a = _gpu.AllocateReadWriteBuffer<float>(n);
                using var b = _gpu.AllocateReadWriteBuffer<float>(n);
                a.CopyFrom(plane.AsSpan(0, n));
                foreach (var r in radii)
                {
                    if (r <= 0) continue;
                    float wf = 2 * r + 1;
                    _gpu.For(w, h, new BlurHFloatKernel(a, b, w, h, r, wf));
                    _gpu.For(w, h, new BlurVFloatKernel(b, a, w, h, r, wf));
                }
                var dst = new float[n];
                a.CopyTo(dst.AsSpan());
                result = dst;
                return true;
            }
            catch (Exception ex)
            {
                Debug.Log($"GPU plugin blur failed: {ex.GetType().Name} {ex.Message}; fallback CPU");
                return false;
            }
        }
    }
#else
    public static bool TryBoxBlurGpu(float[] plane, int w, int h, int[] radii, out float[]? result)
    {
        result = null;
        return false;
    }
#endif
}
