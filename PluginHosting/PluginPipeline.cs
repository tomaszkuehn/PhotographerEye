using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;

namespace ImgViewer;

public sealed class PipelineReport
{
    public int Ran;
    public List<string> Errors { get; } = new();
}

/// <summary>Everything a render pass needs, captured once so the worker never reads UI state.</summary>
public sealed class PluginRenderJob
{
    public PluginRegistry Registry { get; init; } = PluginRegistry.Empty;
    public PluginEditState State { get; init; } = new();
    public ImageMeta Meta { get; init; } = new();
    public bool IsPreview { get; init; }
    /// <summary>Buffer pixels per full-resolution pixel.</summary>
    public double Scale { get; init; } = 1.0;
    public CancellationToken Cancel { get; init; }
    public PipelineReport Report { get; } = new();
    /// <summary>
    /// Geometry between the source photo (SrcW x SrcH, any unit) and the Output stage image. Set before the Output
    /// stage runs, so masks (given in source fractions) are mapped through crop and rotation.
    /// </summary>
    public (GeoState Geo, double SrcW, double SrcH)? OutputGeo { get; set; }

    public bool Has(FilterStage stage) => State.HasEffect(Registry, stage);
}

/// <summary>Runs plugin (and built-in) filters of one stage on float buffers.</summary>
public static class PluginPipeline
{
    // ------------------------------------------------------------------
    //  Conversions (all rounding, no truncation: 8-bit and 16-bit round-trip exactly)
    // ------------------------------------------------------------------

    public static PluginImage FromRaw16(Raw16Image src)
    {
        int n = src.W * src.H;
        var img = new PluginImage(src.W, src.H);
        const float inv = 1f / 65535f;
        Parallel.For(0, src.H, y =>
        {
            int row = y * src.W;
            for (int i = row; i < row + src.W; i++)
            {
                img.R[i] = src.R[i] * inv;
                img.G[i] = src.G[i] * inv;
                img.B[i] = src.B[i] * inv;
            }
        });
        return img;
    }

    public static Raw16Image ToRaw16(PluginImage img)
    {
        var dst = new Raw16Image(img.Width, img.Height);
        Parallel.For(0, img.Height, y =>
        {
            int row = y * img.Width;
            for (int i = row; i < row + img.Width; i++)
            {
                dst.R[i] = U16(img.R[i]);
                dst.G[i] = U16(img.G[i]);
                dst.B[i] = U16(img.B[i]);
            }
        });
        return dst;
    }

    public static ushort U16(float v) => (ushort)(!(v > 0f) ? 0 : v >= 1f ? 65535 : (int)(v * 65535f + 0.5f));
    public static byte U8(float v) => (byte)(!(v > 0f) ? 0 : v >= 1f ? 255 : (int)(v * 255f + 0.5f));

    public static PluginImage FromBitmap(BitmapSource src, out byte[] bgra, out int stride)
    {
        bgra = AdjustmentsProcessor.ToBgraBuffer(src, out stride);
        int w = src.PixelWidth, h = src.PixelHeight, st = stride;
        var img = new PluginImage(w, h);
        var buf = bgra;
        const float inv = 1f / 255f;
        Parallel.For(0, h, y =>
        {
            int row = y * st, o = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = row + x * 4;
                img.B[o + x] = buf[p] * inv;
                img.G[o + x] = buf[p + 1] * inv;
                img.R[o + x] = buf[p + 2] * inv;
            }
        });
        return img;
    }

    /// <summary>Writes RGB back into the original BGRA buffer (alpha kept) and returns a frozen bitmap.</summary>
    public static BitmapSource ToBitmap(PluginImage img, byte[] bgra, int stride)
    {
        int w = img.Width, h = img.Height;
        var outBuf = (byte[])bgra.Clone();
        Parallel.For(0, h, y =>
        {
            int row = y * stride, o = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = row + x * 4;
                outBuf[p] = U8(img.B[o + x]);
                outBuf[p + 1] = U8(img.G[o + x]);
                outBuf[p + 2] = U8(img.R[o + x]);
            }
        });
        return AdjustmentsProcessor.FromBgraBuffer(outBuf, w, h, stride);
    }

    public static void ToLinear(PluginImage img) => MapAll(img, PluginImaging.SrgbToLinearF);
    public static void ToSrgb(PluginImage img) => MapAll(img, v => PluginImaging.LinearToSrgbF(Math.Clamp(v, 0f, 1f)));

    private static void MapAll(PluginImage img, Func<float, float> f)
    {
        var lut = new float[65536];
        for (int i = 0; i < lut.Length; i++) lut[i] = f(i / 65535f);
        Parallel.For(0, img.Height, y =>
        {
            int row = y * img.Width;
            for (int i = row; i < row + img.Width; i++)
            {
                img.R[i] = Map(img.R[i], lut, f);
                img.G[i] = Map(img.G[i], lut, f);
                img.B[i] = Map(img.B[i], lut, f);
            }
        });
    }

    private static float Map(float v, float[] lut, Func<float, float> f)
    {
        if (!(v > 0f)) return lut[0];
        if (v >= 1f) return v == 1f ? lut[65535] : f(v);
        float p = v * 65535f;
        int i = (int)p;
        float t = p - i;
        return i >= 65535 ? lut[65535] : lut[i] + t * (lut[i + 1] - lut[i]);
    }

    // ------------------------------------------------------------------
    //  Stage helpers used by the viewer and save paths
    // ------------------------------------------------------------------

    /// <summary>Stages whose filters see linear light.</summary>
    public static bool IsLinear(FilterStage stage) => stage is FilterStage.SceneLinear or FilterStage.Base;

    /// <summary>Base then SceneLinear on an 8-bit source (photos whose exposure is applied later, in 8 bits). Null when neither ran.</summary>
    public static BitmapSource? ApplyBitmapLinearStages(BitmapSource src, PluginRenderJob job, int fullW, int fullH)
    {
        var b = ApplyBitmap(src, FilterStage.Base, job, fullW, fullH);
        var s = ApplyBitmap(b ?? src, FilterStage.SceneLinear, job, fullW, fullH);
        return s ?? b;
    }

    /// <summary>Runs a stage on a 16-bit image. Returns null when no filter of the stage is active.</summary>
    public static Raw16Image? ApplyRaw16(Raw16Image src, FilterStage stage, PluginRenderJob job, int fullW, int fullH)
    {
        if (!job.Has(stage)) return null;
        var img = FromRaw16(src);
        var space = IsLinear(stage) ? PluginColorSpace.LinearSrgb : PluginColorSpace.Srgb;
        return Run(img, stage, space, job, fullW, fullH) > 0 ? ToRaw16(img) : null;
    }

    /// <summary>Runs a stage on an 8-bit bitmap (sRGB). SceneLinear filters see linearised values.</summary>
    public static BitmapSource? ApplyBitmap(BitmapSource src, FilterStage stage, PluginRenderJob job, int fullW, int fullH)
    {
        if (!job.Has(stage)) return null;
        var img = FromBitmap(src, out var bgra, out var stride);
        bool linear = IsLinear(stage);
        if (linear) ToLinear(img);
        int ran = Run(img, stage, linear ? PluginColorSpace.LinearSrgb : PluginColorSpace.Srgb, job, fullW, fullH);
        if (ran == 0) return null;
        if (linear) ToSrgb(img);
        return ToBitmap(img, bgra, stride);
    }

    // ------------------------------------------------------------------
    //  Core runner
    // ------------------------------------------------------------------

    /// <summary>Runs all active filters of <paramref name="stage"/> in order, in place. Returns how many ran.</summary>
    public static int Run(PluginImage img, FilterStage stage, PluginColorSpace space, PluginRenderJob job, int fullW, int fullH)
    {
        int ran = 0;
        var filters = job.State.Ordered(job.Registry, stage);
        foreach (var f in filters)
        {
            job.Cancel.ThrowIfCancellationRequested();
            if (!job.State.IsFilterActive(f)) continue;
            var ctx = new RenderContext
            {
                Stage = stage,
                Space = space,
                IsPreview = job.IsPreview,
                Scale = job.Scale,
                FullWidth = fullW > 0 ? fullW : img.Width,
                FullHeight = fullH > 0 ? fullH : img.Height,
                Meta = job.Meta
            };
            if (RunOne(f, img, ctx, job)) ran++;
        }
        job.Report.Ran += ran;
        return ran;
    }

    private static bool RunOne(Registered<FilterDescriptor> f, PluginImage img, RenderContext ctx, PluginRenderJob job)
    {
        var p = job.State.ParamsFor(f);
        var maskSpec = job.State.MaskFor(f);
        float[]? mask = null;
        if (maskSpec is not null)
        {
            try
            {
                mask = ctx.Stage == FilterStage.Output && job.OutputGeo is { } og && MaskRender.IsGeometric(maskSpec.Kind)
                    ? MaskRender.RenderMapped(maskSpec, img.Width, img.Height, og.Geo, og.SrcW, og.SrcH)
                    : MaskRender.Render(maskSpec, img, ctx, job.Registry);
            }
            catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var owner = maskSpec.Kind.StartsWith(MaskRender.PluginPrefix) ? job.Registry.FindMask(maskSpec.Kind.Substring(MaskRender.PluginPrefix.Length))?.Owner : null;
                PluginManager.Instance.ReportFailure(owner, $"mask {maskSpec.Kind} for {f.FullId}", ex);
                job.Report.Errors.Add($"{f.D.Label}: mask failed ({ex.Message})");
                return false;
            }
        }

        var work = img.Clone();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cancel);
        ctx.Cancel = cts.Token;
        try
        {
            if (job.IsPreview && !f.IsCore)
            {
                var task = Task.Run(() => f.D.Render(work, p, ctx), CancellationToken.None);
                // Wait on handles, not task.Wait: Wait may inline a not-yet-started task on this thread,
                // which would run the plugin synchronously and defeat the timeout.
                int signaled = WaitHandle.WaitAny(new[] { ((IAsyncResult)task).AsyncWaitHandle, job.Cancel.WaitHandle }, PluginManager.PreviewTimeoutMs);
                if (signaled == 1 && !task.IsCompleted)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(job.Cancel);
                }
                bool done = task.IsCompleted;
                if (done) task.GetAwaiter().GetResult();
                if (!done)
                {
                    cts.Cancel();
                    PluginManager.Instance.ReportFailure(f.Owner, $"{f.FullId} preview timed out after {PluginManager.PreviewTimeoutMs / 1000} s", null, fatal: true);
                    job.Report.Errors.Add($"{f.D.Label}: timed out, plugin disabled");
                    return false;
                }
            }
            else
            {
                f.D.Render(work, p, ctx);
            }
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException ae && ae.InnerExceptions.Count == 1 ? ae.InnerException! : ex;
            if (inner is OperationCanceledException && job.Cancel.IsCancellationRequested) throw new OperationCanceledException(job.Cancel);
            PluginManager.Instance.ReportFailure(f.Owner, $"{f.FullId} render", inner);
            job.Report.Errors.Add($"{f.D.Label}: {inner.Message}");
            return false;
        }
        if (work.Width != img.Width || work.Height != img.Height)
        {
            PluginManager.Instance.ReportFailure(f.Owner, $"{f.FullId} changed the image size", null);
            job.Report.Errors.Add($"{f.D.Label}: changed the image size");
            return false;
        }
        PluginManager.ReportSuccess(f.Owner);
        if (mask is not null) MaskRender.Blend(img, work, mask);
        img.CopyFrom(work);
        return true;
    }
}
