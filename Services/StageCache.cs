using System;
using System.Threading;
using ImgViewer.Imaging;

namespace ImgViewer.Services;

/// <summary>
/// Results of the early live-preview stages kept between renders: the exposure / white balance gain and the
/// SceneLinear plugin pass. Each is reused while its input buffer and settings are unchanged, so moving a later
/// slider (tone, colour, curves) skips them; the outputs keep their identity, which lets the denoise caches of
/// <see cref="Adjust16"/> hit as well. Buffers handed out are never written by the pipeline.
/// </summary>
public sealed class StageCache
{
    private readonly object _lock = new();
    private (Raw16Image Src, double Ev, double Temp, double Tint, Raw16Image Out)? _gain;
    private (Raw16Image Src, object Registry, string Key, double Scale, int FullW, int FullH, Raw16Image? Out)? _scene;

    public int GainHits { get; private set; }
    public int SceneHits { get; private set; }

    public Raw16Image Gain(Raw16Image src, double ev, double temp, double tint, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_gain is { } g && ReferenceEquals(g.Src, src) && g.Ev == ev && g.Temp == temp && g.Tint == tint)
            {
                GainHits++;
                return g.Out;
            }
        }
        var o = LinearRender.ApplyGain(src, ev, temp, tint, ct);
        lock (_lock) _gain = (src, ev, temp, tint, o);
        return o;
    }

    /// <summary>SceneLinear plugins on <paramref name="src"/>; returns <paramref name="src"/> when none is active.</summary>
    public Raw16Image SceneLinear(Raw16Image src, PluginRenderJob job, int fullW, int fullH)
    {
        if (!job.Has(Plugins.FilterStage.SceneLinear)) return src;
        string key = job.State.StageKey(job.Registry, Plugins.FilterStage.SceneLinear);
        lock (_lock)
        {
            if (_scene is { } s && ReferenceEquals(s.Src, src) && ReferenceEquals(s.Registry, job.Registry) && s.Key == key
                && s.Scale == job.Scale && s.FullW == fullW && s.FullH == fullH)
            {
                SceneHits++;
                return s.Out ?? src;
            }
        }
        var o = PluginPipeline.ApplyRaw16(src, Plugins.FilterStage.SceneLinear, job, fullW, fullH);
        if (job.Report.Errors.Count == 0)
            lock (_lock) _scene = (src, job.Registry, key, job.Scale, fullW, fullH, o);
        return o ?? src;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _gain = null;
            _scene = null;
        }
    }
}
