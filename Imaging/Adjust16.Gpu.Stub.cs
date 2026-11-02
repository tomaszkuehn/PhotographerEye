#if PHOTOGRAPHEREYE_NO_GPU
using System;

namespace ImgViewer.Imaging;

public static partial class Adjust16
{
    public static bool GpuEnabled { get; set; }

    public static bool GpuAvailable => false;

    public static string? GpuDeviceName => null;

    public static string GpuStatus => "CPU (GPU not available in this build)";

    public static Raw16Image? TryApplyGpu(
        Raw16Image source, double shadowLift, double gamma, ManualStretchMap? manual,
        out HistogramData? histogram, MonoMode mono = MonoMode.None, int grainSeed = 0,
        PhotoAdjustments? extra = null, CancellationToken ct = default,
        CameraToneCurve? cameraTone = null)
    {
        histogram = null;
        return null;
    }
}
#endif