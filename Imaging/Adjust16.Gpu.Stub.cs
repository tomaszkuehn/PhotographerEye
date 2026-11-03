#if PHOTOGRAPHEREYE_NO_GPU
using System;
using System.Threading;
using System.Windows.Media.Imaging;

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
    public static int GpuDisplayUploads => 0;
    public static int GpuDisplayDenoiseReuse => 0;

    public static BitmapSource? TryRenderDisplayGpu(Raw16Image linear, double ev, double temperature, double tint,
        double shadowLift, double gamma, ManualStretchMap? manual, out HistogramData? histogram, MonoMode mono = MonoMode.None,
        int grainSeed = 0, PhotoAdjustments? extra = null, CancellationToken ct = default, CameraToneCurve? cameraTone = null,
        bool dither = true, int minPixels = 250_000)
    {
        histogram = null;
        return null;
    }

    public static void ReleaseDisplayBuffers() { }
}
#endif
