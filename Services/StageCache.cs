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
    private (Raw16Image Src, object Registry, string Key, double Scale, int FullW, int FullH, Raw16Image? Out)? _base;
    private (Raw16Image Src, double Ev, double Temp, double Tint, Raw16Image Out)? _manual;

    public int GainHits { get; private set; }
    public int SceneHits { get; private set; }
    public int BaseHits { get; private set; }

    /// <summary>
    /// Linear input of the develop pipeline: gain, Base plugins between the default brightness and the manual
    /// exposure / white balance when any is active, then SceneLinear plugins.
    /// </summary>
    public Raw16Image Linear(Raw16Image raw16, double baseEv, double ev, double temp, double tint, PluginRenderJob job, int fullW, int fullH, CancellationToken ct)
    {
        if (!job.Has(Plugins.FilterStage.Base))
            return SceneLinear(Gain(raw16, baseEv + ev, temp, tint, ct), job, fullW, fullH);
        var b = Base(Gain(raw16, baseEv, 0.0, 0.0, ct), job, fullW, fullH);
        return SceneLinear(ManualGain(b, ev, temp, tint, ct), job, fullW, fullH);
    }

    /// <summary>Uncached counterpart of <see cref="Linear"/> (save / export).</summary>
    public static Raw16Image LinearUncached(Raw16Image raw16, double baseEv, double ev, double temp, double tint, PluginRenderJob job, int fullW, int fullH, CancellationToken ct)
    {
        Raw16Image r;
        if (job.Has(Plugins.FilterStage.Base))
        {
            r = LinearRender.ApplyGain(raw16, baseEv, 0.0, 0.0, ct);
            r = PluginPipeline.ApplyRaw16(r, Plugins.FilterStage.Base, job, fullW, fullH) ?? r;
            if (ev != 0 || temp != 0 || tint != 0) r = LinearRender.ApplyGain(r, ev, temp, tint, ct);
        }
        else r = LinearRender.ApplyGain(raw16, baseEv + ev, temp, tint, ct);
        return PluginPipeline.ApplyRaw16(r, Plugins.FilterStage.SceneLinear, job, fullW, fullH) ?? r;
    }

    private Raw16Image ManualGain(Raw16Image src, double ev, double temp, double tint, CancellationToken ct)
    {
        if (ev == 0 && temp == 0 && tint == 0) return src;
        lock (_lock)
        {
            if (_manual is { } g && ReferenceEquals(g.Src, src) && g.Ev == ev && g.Temp == temp && g.Tint == tint) return g.Out;
        }
        var o = LinearRender.ApplyGain(src, ev, temp, tint, ct);
        lock (_lock) _manual = (src, ev, temp, tint, o);
        return o;
    }

    /// <summary>Base plugins on <paramref name="src"/>; returns <paramref name="src"/> when none is active.</summary>
    public Raw16Image Base(Raw16Image src, PluginRenderJob job, int fullW, int fullH)
    {
        if (!job.Has(Plugins.FilterStage.Base)) return src;
        string key = job.State.StageKey(job.Registry, Plugins.FilterStage.Base);
        lock (_lock)
        {
            if (_base is { } s && ReferenceEquals(s.Src, src) && ReferenceEquals(s.Registry, job.Registry) && s.Key == key
                && s.Scale == job.Scale && s.FullW == fullW && s.FullH == fullH)
            {
                BaseHits++;
                return s.Out ?? src;
            }
        }
        var o = PluginPipeline.ApplyRaw16(src, Plugins.FilterStage.Base, job, fullW, fullH);
        if (job.Report.Errors.Count == 0)
            lock (_lock) _base = (src, job.Registry, key, job.Scale, fullW, fullH, o);
        return o ?? src;
    }

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
            _base = null;
            _manual = null;
        }
    }
}
