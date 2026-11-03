using System;
using System.Threading;
using ImgViewer;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;

internal static partial class Program
{
    private static void TestStageCache()
    {
        const int W = 640, H = 480;
        var src = new Raw16Image(W, H);
        var rnd = new Random(3);
        for (int i = 0; i < src.R.Length; i++)
        {
            int v = 4000 + (i % W) * 40;
            src.R[i] = (ushort)Math.Clamp(v + rnd.Next(-900, 900), 0, 65535);
            src.G[i] = (ushort)Math.Clamp(v + rnd.Next(-900, 900), 0, 65535);
            src.B[i] = (ushort)Math.Clamp(v / 2 + rnd.Next(-900, 900), 0, 65535);
        }
        static bool Same(Raw16Image a, Raw16Image b) =>
            a.W == b.W && a.H == b.H && a.R.AsSpan().SequenceEqual(b.R) && a.G.AsSpan().SequenceEqual(b.G) && a.B.AsSpan().SequenceEqual(b.B);

        // Gain: same source and settings -> same buffer; any change -> new one, equal to a fresh gain.
        var cache = new StageCache();
        var g1 = cache.Gain(src, 0.3, 0.1, 0, CancellationToken.None);
        var g2 = cache.Gain(src, 0.3, 0.1, 0, CancellationToken.None);
        var g3 = cache.Gain(src, 0.3, 0.2, 0, CancellationToken.None);
        Check(ReferenceEquals(g1, g2) && cache.GainHits == 1 && !ReferenceEquals(g2, g3), "stage cache: gain reused only while ev/WB are unchanged");
        Check(Same(g3, LinearRender.ApplyGain(src, 0.3, 0.2, 0)), "stage cache: gain result equals a fresh gain");

        // SceneLinear plugins: cached by input buffer and the stage's parameters.
        int calls = 0;
        var pDef = new ParamDef { Id = "amt", Kind = ParamKind.Slider, Min = 0, Max = 100, Default = 0 };
        var lp = TestPlugin("test.scene", new FilterDescriptor
        {
            Id = "lift",
            Stage = FilterStage.SceneLinear,
            Params = new[] { pDef },
            Render = (img, p, c) =>
            {
                calls++;
                float a = (float)(p.Get("amt") / 1000.0);
                for (int i = 0; i < img.R.Length; i++) { img.R[i] += a; img.G[i] += a; img.B[i] += a; }
            }
        });
        var reg = new PluginRegistry(new[] { lp });
        var f = reg.FindFilter("test.scene:lift")!;
        var st = new PluginEditState();
        st.SetValue(f, pDef, 20);
        PluginRenderJob Job() => new() { Registry = reg, State = st, IsPreview = true, Scale = 0.5 };
        var s1 = cache.SceneLinear(g3, Job(), W * 2, H * 2);
        var s2 = cache.SceneLinear(g3, Job(), W * 2, H * 2);
        Check(calls == 1 && ReferenceEquals(s1, s2) && cache.SceneHits == 1 && !ReferenceEquals(s1, g3), "stage cache: SceneLinear plugins run once for unchanged input and parameters");
        st.SetValue(f, pDef, 40);
        var s3 = cache.SceneLinear(g3, Job(), W * 2, H * 2);
        Check(calls == 2 && !ReferenceEquals(s3, s1), "stage cache: a plugin parameter change runs the stage again");
        cache.SceneLinear(g1, Job(), W * 2, H * 2);
        Check(calls == 3, "stage cache: a new gain buffer runs the stage again");
        st.SetValue(f, pDef, 0);
        Check(ReferenceEquals(cache.SceneLinear(g1, Job(), W * 2, H * 2), g1), "stage cache: no active SceneLinear filter returns the input");
        Check(st.StageKey(reg, FilterStage.SceneLinear) == "" && st.StageKey(reg, FilterStage.Display) == "", "stage key: empty for inactive stages");

        // CPU denoise: reused while only later settings change, identical to a fresh run.
        Adjust16.ClearStageCaches();
        var dn = new PhotoAdjustments { Denoise = 0.6, ColorNoise = 0.5, Contrast = 0.1 };
        int r0 = Adjust16.CpuDenoiseReuse;
        var a1 = Adjust16.Apply(g3, 0, 1, null, out _, extra: dn);
        var a2 = Adjust16.Apply(g3, 0, 1, null, out _, extra: new PhotoAdjustments { Denoise = 0.6, ColorNoise = 0.5, Contrast = 0.4 });
        var a3 = Adjust16.Apply(g3, 0, 1, null, out _, extra: dn);
        Check(Adjust16.CpuDenoiseReuse - r0 == 2 && Same(a1, a3) && !Same(a1, a2), $"stage cache: CPU denoise reused for tone changes ({Adjust16.CpuDenoiseReuse - r0})");
        Adjust16.Apply(g3, 0, 1, null, out _, extra: new PhotoAdjustments { Denoise = 0.3, ColorNoise = 0.5, Contrast = 0.1 });
        Adjust16.ClearStageCaches();
        var a4 = Adjust16.Apply(g3, 0, 1, null, out _, extra: dn);
        Check(Adjust16.CpuDenoiseReuse - r0 == 2 && Same(a1, a4), "stage cache: strength change and clear redo the denoise, same result");
    }
}
