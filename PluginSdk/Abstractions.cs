using System;
using System.Collections.Generic;

namespace ImgViewer.Plugins;

// =====================================================================
//  API v1 (legacy) - kept binary compatible. The host wraps every v1
//  IPlugin in an adapter: each PluginFeature becomes a Display-stage
//  v2 filter with id "v1.<file>.<plugin>:<feature>" and params p0..pN.
// =====================================================================

public interface IPlugin
{
    string Name { get; }
    string Description { get; }
    IReadOnlyList<PluginFeature> Features { get; }
}

public enum PluginFeatureKind
{
    ToolbarToggle = 0,
    PanelSlider = 1
}

public sealed class PluginParamDef
{
    public string Name { get; }
    public double Min { get; }
    public double Max { get; }
    public double Default { get; }
    public double Step { get; }
    public string Format { get; }

    public PluginParamDef(string name, double min, double max, double defaultValue, double step = 1.0, string format = "0")
    {
        Name = name;
        Min = min;
        Max = max;
        Default = defaultValue;
        Step = step;
        Format = format;
    }
}

public sealed class PluginFeature
{
    public string Id { get; }
    public string Label { get; }
    public PluginFeatureKind Kind { get; }
    public IReadOnlyList<PluginParamDef> Params { get; }
    public Action<PluginPixelBuffer, double[]> Apply { get; }

    public PluginFeature(string id, string label, PluginFeatureKind kind, IReadOnlyList<PluginParamDef> paramDefs, Action<PluginPixelBuffer, double[]> apply)
    {
        Id = id;
        Label = label;
        Kind = kind;
        Params = paramDefs;
        Apply = apply;
    }
}

public sealed class PluginPixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public ushort[] R { get; }
    public ushort[] G { get; }
    public ushort[] B { get; }

    public PluginPixelBuffer(int width, int height, ushort[] r, ushort[] g, ushort[] b)
    {
        Width = width;
        Height = height;
        R = r;
        G = g;
        B = b;
    }
}

// =====================================================================
//  API v2
// =====================================================================

/// <summary>Current plugin API version implemented by this SDK.</summary>
public static class PluginApi
{
    public const int Version = 2;
}

/// <summary>Optional assembly marker: [assembly: PluginApi(2)]. The host refuses assemblies declaring a newer API.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PluginApiAttribute : Attribute
{
    public int Version { get; }
    public PluginApiAttribute(int version) { Version = version; }
}

/// <summary>Well known permission names a plugin may declare. They are shown to the user before import (declarative, not sandboxed).</summary>
public static class PluginPermissions
{
    public const string FileSystem = "filesystem";
    public const string Network = "network";
    public const string Process = "process";
    public const string Gpu = "gpu";
}

public sealed class PluginManifest
{
    /// <summary>Stable, globally unique id (reverse-DNS style, e.g. "com.example.film"). Used as namespace for all keys in .eye files.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>API version the plugin was built against.</summary>
    public int Api { get; set; } = PluginApi.Version;
    public string[] Permissions { get; set; } = new string[0];
}

/// <summary>Entry point of a v2 plugin. A public parameterless constructor is required.</summary>
public interface IEyePlugin
{
    PluginManifest Manifest { get; }
    /// <summary>Called once after load. Register everything through <paramref name="host"/>.</summary>
    void Initialize(IPluginHost host);
}

public interface IPluginHost
{
    int ApiVersion { get; }
    string HostVersion { get; }
    /// <summary>Writable per-plugin directory (%APPDATA%\PhotographerEye\PluginData\&lt;id&gt;).</summary>
    string DataDirectory { get; }
    /// <summary>True when the host has a usable DirectX 12 GPU (plugins may then use the shared ComputeSharp assembly or <see cref="Imaging"/>).</summary>
    bool GpuAvailable { get; }
    IHostEvents Events { get; }
    IImagingServices Imaging { get; }
    /// <summary>Edit history (undo steps) of the photo shown in the viewer.</summary>
    IEditHistory History { get; }

    void Log(string message);
    void AddFilter(FilterDescriptor filter);
    void AddMaskProvider(MaskProviderDescriptor provider);
    void AddImporter(ImporterDescriptor importer);
    void AddExporter(ExporterDescriptor exporter);
    void AddMetadataProvider(MetadataProviderDescriptor provider);
    void AddCommand(CommandDescriptor command);
    void AddPreset(PresetDescriptor preset);
    /// <summary>Panel in the adjustments column showing a selectable list (mouse or arrow keys). Call inside Initialize.</summary>
    IListPanel AddListPanel(ListPanelDescriptor panel);
    /// <summary>
    /// Plugin-wide setting (not per photo) shown in the viewer's Settings menu; the host stores the choice. Call inside
    /// Initialize; <see cref="IPluginSetting.Value"/> already holds the stored choice when it returns.
    /// </summary>
    IPluginSetting AddSetting(SettingDescriptor setting);
    /// <summary>Drawing over the photo (shapes and an RGBA map), switched on and off in the viewer's menu. Call inside Initialize.</summary>
    IOverlay AddOverlay(OverlayDescriptor overlay);
    /// <summary>
    /// Tool working with the mouse on the photo (brush, stamp, picker, ...), switched on from the Tools menu or its
    /// shortcut. While it is active the left button goes to the tool instead of panning. Call inside Initialize.
    /// </summary>
    ITool AddTool(ToolDescriptor tool);
}

// ---------------------------------------------------------------------
//  Images
// ---------------------------------------------------------------------

public enum PluginColorSpace
{
    /// <summary>Scene-referred linear light, sRGB/Rec.709 primaries, 0..1 (may be &gt; 1 before tone mapping).</summary>
    LinearSrgb = 0,
    /// <summary>Display-referred sRGB encoded, 0..1.</summary>
    Srgb = 1
}

/// <summary>Planar float RGB image, row-major, index = y * Width + x.</summary>
public sealed class PluginImage
{
    public int Width { get; }
    public int Height { get; }
    public float[] R { get; }
    public float[] G { get; }
    public float[] B { get; }

    public PluginImage(int width, int height)
        : this(width, height, new float[width * height], new float[width * height], new float[width * height]) { }

    public PluginImage(int width, int height, float[] r, float[] g, float[] b)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int n = width * height;
        if (r == null || g == null || b == null || r.Length < n || g.Length < n || b.Length < n)
            throw new ArgumentException("plane size mismatch");
        Width = width;
        Height = height;
        R = r;
        G = g;
        B = b;
    }

    public int Length => Width * Height;

    public PluginImage Clone()
    {
        return new PluginImage(Width, Height, (float[])R.Clone(), (float[])G.Clone(), (float[])B.Clone());
    }

    public void CopyFrom(PluginImage other)
    {
        if (other.Width != Width || other.Height != Height) throw new ArgumentException("size mismatch");
        Array.Copy(other.R, R, Length);
        Array.Copy(other.G, G, Length);
        Array.Copy(other.B, B, Length);
    }
}

public sealed class ImageMeta
{
    public string SourcePath { get; set; } = "";
    public bool IsRaw { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Camera { get; set; }
    public string Date { get; set; }
    public double? Iso { get; set; }
    public double? Aperture { get; set; }
    public double? ShutterSeconds { get; set; }
    public double? FocalLength { get; set; }
}

public enum FilterStage
{
    /// <summary>Before the built-in develop pipeline, linear light (white balance and exposure already applied).</summary>
    SceneLinear = 0,
    /// <summary>After the built-in develop pipeline (tone, color, sharpening), sRGB encoded, before crop/rotate.</summary>
    Display = 1,
    /// <summary>After crop/rotate ("post-crop"), sRGB encoded. Runs in preview and on save/export.</summary>
    Output = 2,
    /// <summary>
    /// First of all, linear light, before the manual exposure and white balance (RAW: after the default brightness,
    /// before the camera tone curve; other photos: the decoded image). Its result does not depend on the manual
    /// corrections, which are applied on top of it. Runs before <see cref="SceneLinear"/>.
    /// </summary>
    Base = 3
}

public sealed class RenderContext
{
    /// <summary>Stage the filter runs in.</summary>
    public FilterStage Stage { get; set; }
    public PluginColorSpace Space { get; set; }
    /// <summary>True for interactive previews (downscaled). False for full-resolution save/export.</summary>
    public bool IsPreview { get; set; }
    /// <summary>Buffer pixels per full-resolution pixel (&lt;= 1). Scale spatial radii by it so previews match the export.</summary>
    public double Scale { get; set; } = 1.0;
    /// <summary>Full-resolution size of the image at this stage.</summary>
    public int FullWidth { get; set; }
    public int FullHeight { get; set; }
    public ImageMeta Meta { get; set; } = new ImageMeta();
    public System.Threading.CancellationToken Cancel { get; set; }
    public Action<double> Progress { get; set; } = _ => { };
}

// ---------------------------------------------------------------------
//  Parameters
// ---------------------------------------------------------------------

public enum ParamKind
{
    Slider = 0,
    Toggle = 1,
    Choice = 2,
    /// <summary>Color, stored as "#RRGGBB" text.</summary>
    Color = 3,
    /// <summary>Tone curve, stored as "x,y;x,y;..." (0..1) text.</summary>
    Curve = 4,
    /// <summary>Point on the image, stored as "x,y" fractions (picked by clicking the photo).</summary>
    Point = 5,
    /// <summary>File path, stored as text.</summary>
    File = 6
}

public sealed class ParamChoice
{
    public string Id { get; }
    public string Label { get; }
    public ParamChoice(string id, string label) { Id = id; Label = label; }
}

public sealed class ParamDef
{
    /// <summary>Stable id, unique inside the filter. Never reuse an id with a different meaning.</summary>
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public ParamKind Kind { get; set; }
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    /// <summary>Default for Slider (value) and Toggle (0/1).</summary>
    public double Default { get; set; }
    public double Step { get; set; } = 1;
    public string Format { get; set; } = "0";
    /// <summary>Default for text kinds: choice id, "#RRGGBB", curve points, "x,y" or path.</summary>
    public string DefaultText { get; set; } = "";
    public IReadOnlyList<ParamChoice> Choices { get; set; } = new ParamChoice[0];
    /// <summary>Open-file dialog filter for <see cref="ParamKind.File"/>.</summary>
    public string FileFilter { get; set; } = "All files|*.*";
    public string Tooltip { get; set; } = "";
    /// <summary>
    /// <see cref="ParamKind.Point"/> only: id of a Slider parameter of the same filter giving a radius in % of the
    /// photo's long side. The host then draws the point as a circle of that size on the photo (while the plugin's
    /// panel section is open); without it a set point is drawn as a small cross.
    /// </summary>
    public string MarkerRadius { get; set; } = "";
    /// <summary>
    /// <see cref="ParamKind.Point"/> only: id of another Point parameter of the same filter. This point is drawn dashed
    /// with an arrow to that one (e.g. a clone source pointing at its target).
    /// </summary>
    public string MarkerLink { get; set; } = "";

    public bool IsText => Kind == ParamKind.Choice || Kind == ParamKind.Color || Kind == ParamKind.Curve || Kind == ParamKind.Point || Kind == ParamKind.File;

    public static ParamDef Slider(string id, string label, double min, double max, double def, double step = 1, string format = "0")
        => new ParamDef { Id = id, Label = label, Kind = ParamKind.Slider, Min = min, Max = max, Default = def, Step = step, Format = format };

    public static ParamDef Toggle(string id, string label, bool def = false)
        => new ParamDef { Id = id, Label = label, Kind = ParamKind.Toggle, Min = 0, Max = 1, Default = def ? 1 : 0 };

    public static ParamDef Choice(string id, string label, string defaultId, params ParamChoice[] choices)
        => new ParamDef { Id = id, Label = label, Kind = ParamKind.Choice, DefaultText = defaultId, Choices = choices };

    public static ParamDef Color(string id, string label, string defaultHex = "#808080")
        => new ParamDef { Id = id, Label = label, Kind = ParamKind.Color, DefaultText = defaultHex };

    public static ParamDef Curve(string id, string label, string defaultPoints = "0,0;1,1")
        => new ParamDef { Id = id, Label = label, Kind = ParamKind.Curve, DefaultText = defaultPoints };

    public static ParamDef Point(string id, string label, double x = 0.5, double y = 0.5)
        => new ParamDef
        {
            Id = id, Label = label, Kind = ParamKind.Point,
            DefaultText = x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "," + y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
        };

    public static ParamDef File(string id, string label, string filter = "All files|*.*")
        => new ParamDef { Id = id, Label = label, Kind = ParamKind.File, FileFilter = filter };
}

/// <summary>Parameter values passed to filters, masks and exporters. Missing values fall back to the defaults.</summary>
public sealed class FilterParams
{
    private readonly IReadOnlyList<ParamDef> _defs;
    private readonly IDictionary<string, double> _values;
    private readonly IDictionary<string, string> _text;

    public FilterParams(IReadOnlyList<ParamDef> defs, IDictionary<string, double> values, IDictionary<string, string> text)
    {
        _defs = defs ?? new ParamDef[0];
        _values = values ?? new Dictionary<string, double>();
        _text = text ?? new Dictionary<string, string>();
    }

    public IReadOnlyList<ParamDef> Definitions => _defs;

    private ParamDef Def(string id)
    {
        foreach (var d in _defs) if (d.Id == id) return d;
        return null;
    }

    public double Get(string id)
    {
        if (_values.TryGetValue(id, out var v)) return v;
        var d = Def(id);
        return d != null ? d.Default : 0.0;
    }

    public bool GetBool(string id) => Get(id) >= 0.5;

    public string GetText(string id)
    {
        if (_text.TryGetValue(id, out var s) && s != null) return s;
        var d = Def(id);
        return d != null ? d.DefaultText : "";
    }

    /// <summary>Color as linear-agnostic 0..1 sRGB components.</summary>
    public void GetColor(string id, out float r, out float g, out float b)
    {
        ParseColor(GetText(id), out r, out g, out b);
    }

    public bool GetPoint(string id, out double x, out double y)
    {
        return ParsePoint(GetText(id), out x, out y);
    }

    public ToneCurve GetCurve(string id) => ToneCurve.Parse(GetText(id));

    public static void ParseColor(string hex, out float r, out float g, out float b)
    {
        r = g = b = 0.5f;
        if (string.IsNullOrEmpty(hex)) return;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return;
        try
        {
            r = Convert.ToInt32(s.Substring(0, 2), 16) / 255f;
            g = Convert.ToInt32(s.Substring(2, 2), 16) / 255f;
            b = Convert.ToInt32(s.Substring(4, 2), 16) / 255f;
        }
        catch (FormatException) { }
    }

    public static string FormatColor(float r, float g, float b)
    {
        int Q(float v) => Math.Max(0, Math.Min(255, (int)Math.Round(v * 255f)));
        return "#" + Q(r).ToString("X2") + Q(g).ToString("X2") + Q(b).ToString("X2");
    }

    public static bool ParsePoint(string text, out double x, out double y)
    {
        x = y = 0.5;
        if (string.IsNullOrEmpty(text)) return false;
        var p = text.Split(',');
        if (p.Length != 2) return false;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return double.TryParse(p[0], System.Globalization.NumberStyles.Float, ci, out x) &
               double.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out y);
    }
}

/// <summary>Monotone cubic (Fritsch-Carlson) curve through sorted 0..1 points.</summary>
public sealed class ToneCurve
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _m;

    public IReadOnlyList<double> X => _x;
    public IReadOnlyList<double> Y => _y;

    public ToneCurve(IList<double> xs, IList<double> ys)
    {
        var pts = new List<KeyValuePair<double, double>>();
        for (int i = 0; i < Math.Min(xs.Count, ys.Count); i++)
            pts.Add(new KeyValuePair<double, double>(Clamp01(xs[i]), Clamp01(ys[i])));
        pts.Sort((a, b) => a.Key.CompareTo(b.Key));
        var cx = new List<double>();
        var cy = new List<double>();
        foreach (var p in pts)
        {
            if (cx.Count > 0 && p.Key - cx[cx.Count - 1] < 1e-4) { cy[cy.Count - 1] = p.Value; continue; }
            cx.Add(p.Key);
            cy.Add(p.Value);
        }
        if (cx.Count == 0) { cx.Add(0); cy.Add(0); }
        if (cx.Count == 1) { cx.Add(1); cy.Add(1); if (cx[0] >= 1) { cx[0] = 0; cy[0] = 0; } }
        _x = cx.ToArray();
        _y = cy.ToArray();
        int n = _x.Length;
        var d = new double[n - 1];
        for (int i = 0; i < n - 1; i++) d[i] = (_y[i + 1] - _y[i]) / (_x[i + 1] - _x[i]);
        _m = new double[n];
        _m[0] = d[0];
        _m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++) _m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (Math.Abs(d[i]) < 1e-12) { _m[i] = 0; _m[i + 1] = 0; continue; }
            double a = _m[i] / d[i], b = _m[i + 1] / d[i];
            double s = a * a + b * b;
            if (s > 9)
            {
                double t = 3 / Math.Sqrt(s);
                _m[i] = t * a * d[i];
                _m[i + 1] = t * b * d[i];
            }
        }
    }

    public bool IsIdentity
    {
        get
        {
            for (int i = 0; i < _x.Length; i++) if (Math.Abs(_x[i] - _y[i]) > 1e-4) return false;
            return true;
        }
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    public double Evaluate(double x)
    {
        if (x <= _x[0]) return _y[0];
        int n = _x.Length;
        if (x >= _x[n - 1]) return _y[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_x[mid] > x) hi = mid; else lo = mid;
        }
        double h = _x[hi] - _x[lo];
        double t = (x - _x[lo]) / h;
        double t2 = t * t, t3 = t2 * t;
        return Clamp01((2 * t3 - 3 * t2 + 1) * _y[lo] + (t3 - 2 * t2 + t) * h * _m[lo] + (-2 * t3 + 3 * t2) * _y[hi] + (t3 - t2) * h * _m[hi]);
    }

    /// <summary>Lookup table with <paramref name="size"/> entries over 0..1.</summary>
    public float[] ToLut(int size = 4096)
    {
        var lut = new float[size];
        for (int i = 0; i < size; i++) lut[i] = (float)Evaluate(i / (double)(size - 1));
        return lut;
    }

    public static ToneCurve Parse(string text)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (!string.IsNullOrEmpty(text))
        {
            foreach (var part in text.Split(';'))
            {
                var p = part.Split(',');
                if (p.Length != 2) continue;
                if (double.TryParse(p[0], System.Globalization.NumberStyles.Float, ci, out var x) &&
                    double.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out var y))
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }
        }
        if (xs.Count < 2) { xs.Clear(); ys.Clear(); xs.Add(0); ys.Add(0); xs.Add(1); ys.Add(1); }
        return new ToneCurve(xs, ys);
    }

    public string Format()
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var parts = new string[_x.Length];
        for (int i = 0; i < _x.Length; i++) parts[i] = _x[i].ToString("0.####", ci) + "," + _y[i].ToString("0.####", ci);
        return string.Join(";", parts);
    }
}

// ---------------------------------------------------------------------
//  Extension points
// ---------------------------------------------------------------------

public enum FilterPlacement
{
    /// <summary>Section in the Adjustments panel. Active while any parameter differs from its default.</summary>
    Panel = 0,
    /// <summary>On/off button in the top toolbar (parameters, if any, still appear in the panel).</summary>
    Toolbar = 1
}

public sealed class FilterDescriptor
{
    /// <summary>Stable id, unique inside the plugin.</summary>
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Panel group header (e.g. "Effects", "Color", "Film").</summary>
    public string Group { get; set; } = "Plugins";
    /// <summary>Where the filter runs. May be changed later from a setting's <see cref="SettingDescriptor.Changed"/> handler (the host re-renders).</summary>
    public FilterStage Stage { get; set; } = FilterStage.Display;
    public FilterPlacement Placement { get; set; } = FilterPlacement.Panel;
    /// <summary>Default position inside the stage (lower runs first). The user can reorder; the order is stored per photo.</summary>
    public int Order { get; set; }
    /// <summary>Whether the user may attach a local mask (gradient, radial, brush, range, plugin mask). Ignored for the Output stage.</summary>
    public bool SupportsMask { get; set; } = true;
    /// <summary>
    /// Optional: filters of one plugin with the same non-empty RepeatGroup are instances of one tool (e.g. heal spots).
    /// The host shows them as a single panel section with a list of the used instances (select / edit / remove) and an
    /// Add button, up to the number of registered instances. The value is the item name shown in the UI ("Spot").
    /// An instance with Point parameters counts as used once one of its points is set; Add picks the first Point
    /// parameter on the photo, places unset points linked to it (<see cref="ParamDef.MarkerLink"/>) next to it and
    /// copies the other parameters from the selected instance.
    /// </summary>
    public string RepeatGroup { get; set; } = "";
    public IReadOnlyList<ParamDef> Params { get; set; } = new ParamDef[0];
    /// <summary>Optional: return true when the parameters produce no change (the host then skips the filter).</summary>
    public Func<FilterParams, bool> IsIdentity { get; set; }
    /// <summary>Process the image in place.</summary>
    public Action<PluginImage, FilterParams, RenderContext> Render { get; set; }
}

public sealed class MaskProviderDescriptor
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public IReadOnlyList<ParamDef> Params { get; set; } = new ParamDef[0];
    /// <summary>Return a mask (Width*Height, 0..1) for the given image. The image must not be modified.</summary>
    public Func<PluginImage, FilterParams, RenderContext, float[]> CreateMask { get; set; }
}

public sealed class ImportedImage
{
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Planes 0..1.</summary>
    public float[] R { get; set; }
    public float[] G { get; set; }
    public float[] B { get; set; }
    /// <summary>True when the planes are linear light; false for sRGB encoded.</summary>
    public bool Linear { get; set; }
}

public sealed class ImporterDescriptor
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Lower-case extensions including the dot, e.g. ".ppm".</summary>
    public IReadOnlyList<string> Extensions { get; set; } = new string[0];
    /// <summary>Decode the file. <paramref name="maxSize"/> is a hint (long side) for previews, 0 = full size.</summary>
    public Func<string, int, ImportedImage> Load { get; set; }
    /// <summary>Optional fast probe returning (width, height) without decoding.</summary>
    public Func<string, int[]> Probe { get; set; }
}

public sealed class ExportRequest
{
    /// <summary>Fully developed image (sRGB encoded, after crop/rotate and Output filters), full resolution.</summary>
    public PluginImage Image { get; set; }
    public string SourcePath { get; set; } = "";
    /// <summary>Destination chosen by the user (null when the exporter has no FileFilter).</summary>
    public string DestinationPath { get; set; }
    public FilterParams Options { get; set; }
    public ImageMeta Meta { get; set; } = new ImageMeta();
    public System.Threading.CancellationToken Cancel { get; set; }
    public Action<double> Progress { get; set; } = _ => { };
    /// <summary>Optional status text shown after the export.</summary>
    public string ResultMessage { get; set; }
}

public sealed class ExporterDescriptor
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Save dialog filter (e.g. "JPEG|*.jpg"). Null/empty = no dialog (publish services).</summary>
    public string FileFilter { get; set; }
    public string DefaultSuffix { get; set; } = "_export";
    /// <summary>Options asked in a dialog before exporting.</summary>
    public IReadOnlyList<ParamDef> Options { get; set; } = new ParamDef[0];
    public Action<ExportRequest> Export { get; set; }
}

public sealed class MetadataProviderDescriptor
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Return label/value pairs shown in the info tooltip of the status bar.</summary>
    public Func<ImageMeta, IReadOnlyList<KeyValuePair<string, string>>> Provide { get; set; }
}

[Flags]
public enum CommandPlacement
{
    Menu = 1,
    Toolbar = 2,
    /// <summary>A button at the bottom of the panel section of <see cref="CommandDescriptor.SectionFilter"/>.</summary>
    Section = 4
}

public interface ICommandContext
{
    /// <summary>Currently opened file (null when nothing is open).</summary>
    string CurrentPath { get; }
    ImageMeta Meta { get; }
    void SetStatus(string text);
    void ShowMessage(string text);
    /// <summary>Read/write a parameter of a filter of this plugin (or "pluginId:filterId" for other plugins).</summary>
    double GetValue(string filterId, string paramId);
    void SetValue(string filterId, string paramId, double value);
    void SetText(string filterId, string paramId, string value);
    /// <summary>Current value of a text parameter (choice id, colour, curve, point, file).</summary>
    string GetText(string filterId, string paramId);
    bool IsActive(string filterId);
    void SetActive(string filterId, bool active);
    /// <summary>Apply a preset of this plugin by id.</summary>
    void ApplyPreset(string presetId);

    // ---- Core adjustments, geometry and curves -------------------------------------------------------------
    // Changes made by one command (or one RunInBackground completion) become a single undo step, labelled with
    // StepLabel (default: the command label). Reads return the value including changes made earlier in the
    // same command. The preview re-renders once after the command returns.

    /// <summary>Label of the undo step this command creates (default: the command label).</summary>
    string StepLabel { get; set; }
    /// <summary>Keys accepted by <see cref="GetCore"/> / <see cref="SetCore"/>: "Exposure", "Contrast", ... (see <see cref="CoreKeys"/>).</summary>
    IReadOnlyList<string> CoreKeys { get; }
    /// <summary>Core adjustment, key with or without the "core." prefix (e.g. "Exposure" or "core.Exposure").</summary>
    double GetCore(string key);
    /// <summary>Sets a core adjustment (clamped to the slider range; "Mono": 0 colour, 1 documentary B&W, 2 Tri-X B&W).</summary>
    void SetCore(string key, double value);
    /// <summary>Slider range of a core adjustment.</summary>
    CoreRange GetCoreRange(string key);
    /// <summary>Crop, straighten and quarter rotation of the photo.</summary>
    PhotoGeometry GetGeometry();
    void SetGeometry(PhotoGeometry geometry);
    /// <summary>Removes crop, straightening and rotation.</summary>
    void ResetGeometry();
    /// <summary>
    /// Curve points as [input, output] pairs. Red/Green/Blue curves and levels and the histogram stretch use display
    /// fractions 0..1; <see cref="CurveKind.StretchEv"/> uses EV; <see cref="CurveKind.ToneCurve"/> is the camera tone
    /// curve (linear 0..1). An empty array means "no curve".
    /// </summary>
    double[][] GetCurve(CurveKind kind);
    /// <summary>Replaces a curve (null or empty resets it).</summary>
    void SetCurve(CurveKind kind, double[][] points);
    /// <summary>
    /// White balance from a neutral point, like the white point eyedropper. Coordinates are fractions of the
    /// un-rotated, un-cropped photo. Returns false (and changes nothing) when the point is outside or clipped.
    /// </summary>
    bool SetWhitePoint(double x, double y);
    /// <summary>
    /// White balance that makes a colour neutral: <paramref name="r"/>, <paramref name="g"/>, <paramref name="b"/> are
    /// linear-light values measured on <see cref="PhotoImageKind.Source"/> (before white balance), e.g. the average
    /// of areas that should be grey. Sets Temperature and Tint; returns false when the colour cannot be neutralised
    /// within their range (the clamped result is still applied) or is not positive.
    /// </summary>
    bool SetWhiteBalanceFromNeutral(double r, double g, double b);

    // ---- Pixels --------------------------------------------------------------------------------------------

    /// <summary>
    /// Pixels of the photo, downscaled so the long side is at most <paramref name="maxLongSide"/> (0 = as held in
    /// memory). <see cref="PhotoImageKind.Source"/>: unprocessed photo (RAW: linear light at the default brightness;
    /// other formats: sRGB), un-rotated and un-cropped. <see cref="PhotoImageKind.Developed"/>: the photo as it looks
    /// with every setting at its default (RAW: default brightness and camera tone curve; no edits, no plugins), sRGB,
    /// un-rotated and un-cropped. <see cref="PhotoImageKind.Rendered"/>: the current preview (sRGB, after crop and
    /// rotation). Null when no photo is shown.
    /// </summary>
    PhotoPixels GetImage(PhotoImageKind kind, int maxLongSide);
    /// <summary>Histogram of the current preview (256 bins per channel), or null when none was computed yet.</summary>
    PhotoHistogram GetHistogram();

    // ---- Interaction ---------------------------------------------------------------------------------------

    /// <summary>
    /// Shows a dialog with the given parameters (Slider, Toggle, Choice, Text) and returns the values, or null when the
    /// user cancelled. The last values are remembered per <paramref name="title"/> for this session.
    /// </summary>
    FilterParams Ask(string title, IReadOnlyList<ParamDef> parameters);
    /// <summary>File open dialog; returns the path or null. <paramref name="filter"/> like "Images|*.jpg;*.png".</summary>
    string PickOpenFile(string title, string filter);
    /// <summary>File save dialog; returns the path or null.</summary>
    string PickSaveFile(string title, string filter, string defaultName);
    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread with progress (0..1) in the status bar; Esc, switching to
    /// another photo or another RunInBackground call of the same plugin cancels it. <paramref name="done"/> runs on the UI thread with a fresh
    /// context when the work finished (not when it was cancelled or failed); its edits become one undo step.
    /// </summary>
    void RunInBackground(string label, Func<IProgress<double>, System.Threading.CancellationToken, object> work, Action<ICommandContext, object> done);

    // ---- Viewer --------------------------------------------------------------------------------------------

    /// <summary>Zoom, fit, 100% and the before/after view.</summary>
    IViewerControl View { get; }

    // ---- Masks ---------------------------------------------------------------------------------------------

    /// <summary>Mask of a filter (this plugin's "filterId" or "pluginId:filterId"), or null when it has none.</summary>
    MaskInfo GetMask(string filterId);
    /// <summary>Sets (or with null removes) the mask of a filter that supports masks.</summary>
    void SetMask(string filterId, MaskInfo mask);
}

public sealed class CommandDescriptor
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Optional shortcut, e.g. "Ctrl+Shift+N".</summary>
    public string Shortcut { get; set; }
    public CommandPlacement Placement { get; set; } = CommandPlacement.Menu;
    /// <summary>Short toolbar caption or a Segoe MDL2 Assets glyph.</summary>
    public string ToolbarText { get; set; }
    /// <summary>
    /// With <see cref="CommandPlacement.Section"/>: id of a filter of this plugin; the command is shown as a button
    /// (caption <see cref="ToolbarText"/>, else <see cref="Label"/>) at the bottom of that filter's panel section, and
    /// the panel is rebuilt after it ran (e.g. to show changed <see cref="ParamDef.Choices"/>).
    /// </summary>
    public string SectionFilter { get; set; }
    public Action<ICommandContext> Execute { get; set; }
}

/// <summary>
/// Named set of values. Keys: "core.Exposure", "core.Contrast", "core.Temperature", "core.Tint", "core.Saturation",
/// "core.Highlights", "core.Shadows", "core.Whites", "core.Blacks", "core.Clarity", "core.Dehaze",
/// "core.Sharpen", "core.Denoise", "core.ColorNoise", "core.Gamma", "core.Mono" ("core.ShadowLift" = "core.Shadows") and
/// "filterId/paramId" (this plugin) or "pluginId:filterId/paramId" (any plugin).
/// </summary>
public sealed class PresetDescriptor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "Plugin presets";
    public IDictionary<string, double> Values { get; set; } = new Dictionary<string, double>();
    public IDictionary<string, string> Text { get; set; } = new Dictionary<string, string>();
    /// <summary>Toolbar filters to switch on ("filterId" or "pluginId:filterId").</summary>
    public IList<string> Active { get; set; } = new List<string>();
}

// ---------------------------------------------------------------------
//  Command helpers: geometry, curves, pixels, viewer, masks
// ---------------------------------------------------------------------

public struct CoreRange
{
    public double Min { get; set; }
    public double Max { get; set; }
    public double Default { get; set; }
}

public sealed class PhotoGeometry
{
    /// <summary>Quarter turns clockwise, 0..3.</summary>
    public int Rot90 { get; set; }
    /// <summary>Straightening angle in degrees, -45..45.</summary>
    public double Angle { get; set; }
    /// <summary>Crop rectangle as fractions of the rotated and straightened photo.</summary>
    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropW { get; set; } = 1.0;
    public double CropH { get; set; } = 1.0;
    /// <summary>"original", "3:2", "4:3", "16:9" or "16:10" (as in the crop tool).</summary>
    public string Aspect { get; set; } = "original";
}

public enum CurveKind
{
    Red = 0,
    Green = 1,
    Blue = 2,
    LevelsRed = 3,
    LevelsGreen = 4,
    LevelsBlue = 5,
    /// <summary>Histogram stretch in display fractions 0..1.</summary>
    Stretch = 6,
    /// <summary>Histogram stretch in EV.</summary>
    StretchEv = 7,
    /// <summary>Camera tone curve (RAW).</summary>
    ToneCurve = 8
}

public enum PhotoImageKind
{
    Source = 0,
    Rendered = 1,
    /// <summary>Default development without edits (sRGB, un-cropped).</summary>
    Developed = 2
}

public sealed class PhotoPixels
{
    public PluginImage Image { get; set; }
    public PluginColorSpace Space { get; set; }
    /// <summary>Size of the full-resolution image these pixels stand for (Source: the photo; Rendered: the cropped output).</summary>
    public int FullWidth { get; set; }
    public int FullHeight { get; set; }
}

public sealed class PhotoHistogram
{
    public int[] Luma { get; set; }
    public int[] R { get; set; }
    public int[] G { get; set; }
    public int[] B { get; set; }
}

public interface IViewerControl
{
    /// <summary>1.0 = 100 % (one image pixel per screen pixel).</summary>
    double Zoom { get; }
    /// <summary>Sets the zoom (0.05..4), keeping <paramref name="centerX"/>/<paramref name="centerY"/> (fractions of the shown image) in the middle when given.</summary>
    void SetZoom(double zoom, double centerX = double.NaN, double centerY = double.NaN);
    void FitToWindow();
    void ActualSize();
    /// <summary>Shows the unedited photo (like holding Space) until set back to false.</summary>
    bool ShowOriginal { get; set; }
}

/// <summary>
/// Mask of a filter. Coordinates are fractions of the un-rotated, un-cropped photo (also for Output-stage filters;
/// the host maps them through the crop).
/// Kind "linear": P = x1, y1, x2, y2 (full effect at the first point, none past the second).
/// "radial": P = cx, cy, rx, ry, feather (0..1) and optionally the rotation in degrees (clockwise, default 0). "luma": P = low, high, feather (0..1).
/// "brush": Strokes = [mode (1 paint, 0 erase), radius (fraction of width), x1, y1, x2, y2, ...].
/// "plugin:pluginId:maskId": a mask provider with <see cref="Values"/> / <see cref="Text"/>.
/// </summary>
public sealed class MaskInfo
{
    public string Kind { get; set; } = "";
    public double[] P { get; set; }
    public bool Invert { get; set; }
    public IList<double[]> Strokes { get; set; }
    public IDictionary<string, double> Values { get; set; }
    public IDictionary<string, string> Text { get; set; }
}

// ---------------------------------------------------------------------
//  Overlays drawn over the photo
// ---------------------------------------------------------------------

public enum OverlaySpace
{
    /// <summary>Fractions of the shown image (after crop and rotation).</summary>
    Shown = 0,
    /// <summary>Fractions of the un-rotated, un-cropped photo (follows crop and rotation).</summary>
    Source = 1
}

public enum OverlayShapeKind
{
    Line = 0,
    Rectangle = 1,
    Ellipse = 2,
    Polyline = 3,
    Polygon = 4,
    Text = 5
}

public sealed class OverlayShape
{
    public OverlayShapeKind Kind { get; set; }
    /// <summary>Line: x1, y1, x2, y2. Rectangle / Ellipse: x, y, w, h (ellipse: bounding box). Polyline / Polygon: x1, y1, x2, y2, ... Text: x, y.</summary>
    public double[] Points { get; set; }
    /// <summary>ARGB, e.g. 0xFFFF0000 (opaque red).</summary>
    public uint Stroke { get; set; } = 0xFFFFFFFF;
    /// <summary>ARGB fill (0 = none).</summary>
    public uint Fill { get; set; }
    /// <summary>Line width in screen pixels.</summary>
    public double Thickness { get; set; } = 1.5;
    public bool Dashed { get; set; }
    public string Text { get; set; }
    /// <summary>Text size in screen pixels.</summary>
    public double FontSize { get; set; } = 12;
}

/// <summary>RGBA raster stretched over the shown image (e.g. a clipping map). Pixels: 4 bytes per pixel, R G B A, row-major.</summary>
public sealed class OverlayBitmap
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Rgba { get; set; }
}

public sealed class OverlayContent
{
    public OverlaySpace Space { get; set; }
    public IList<OverlayShape> Shapes { get; set; } = new List<OverlayShape>();
    /// <summary>Covers the shown image (always in <see cref="OverlaySpace.Shown"/>).</summary>
    public OverlayBitmap Bitmap { get; set; }
}

public sealed class OverlayDescriptor
{
    public string Id { get; set; } = "";
    /// <summary>Name in the settings menu > "Plugin overlays".</summary>
    public string Label { get; set; } = "";
    /// <summary>Shown when the plugin loads (the user can switch it off in the menu).</summary>
    public bool DefaultVisible { get; set; }
    /// <summary>
    /// Called on the UI thread when the overlay is visible and the photo, the render or <see cref="IOverlay.Refresh"/>
    /// asks for new content. <see cref="OverlayRequest.Rendered"/> is the current preview (sRGB), when available.
    /// Return null to draw nothing.
    /// </summary>
    public Func<OverlayRequest, OverlayContent> Draw { get; set; }
}

public sealed class OverlayRequest
{
    public string Path { get; set; }
    public ImageMeta Meta { get; set; }
    /// <summary>Lazily fetched current preview (sRGB, after crop), long side at most 1024; null when nothing is shown.</summary>
    public Func<PhotoPixels> Rendered { get; set; }
}

public interface IOverlay
{
    bool Visible { get; set; }
    /// <summary>Ask for <see cref="OverlayDescriptor.Draw"/> again (any thread).</summary>
    void Refresh();
}

// ---------------------------------------------------------------------
//  Pointer tools (mouse on the photo)
// ---------------------------------------------------------------------

public enum PointerPhase
{
    /// <summary>A button was pressed over the photo; the tool owns the mouse until Up or Cancel.</summary>
    Down = 0,
    /// <summary>The mouse moved with the button held.</summary>
    Move = 1,
    /// <summary>The button was released: the press ends, its changes are one undo step.</summary>
    Up = 2,
    /// <summary>The press was cancelled (Esc, tool switched off, photo changed); the changes made so far are kept as one step.</summary>
    Cancel = 3,
    /// <summary>The mouse moved without a button (only when <see cref="ToolDescriptor.Hover"/> is set).</summary>
    Hover = 4
}

public enum PointerButton
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3
}

public sealed class PointerEvent
{
    public PointerPhase Phase { get; set; }
    /// <summary>The button of the press (None for Hover).</summary>
    public PointerButton Button { get; set; }
    /// <summary>Position in fractions of the image in <see cref="ToolDescriptor.Space"/> (may be outside 0..1 while dragging).</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>The position is over the photo.</summary>
    public bool Inside { get; set; }
    public bool Shift { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    /// <summary>2 for a double click (Down only).</summary>
    public int ClickCount { get; set; } = 1;
    /// <summary>One screen pixel as a fraction of the image width (for hit tolerances and brush sizes in screen units).</summary>
    public double ScreenPixel { get; set; }
}

public sealed class ToolDescriptor
{
    public string Id { get; set; } = "";
    /// <summary>Name in the Tools menu, the status bar and the undo step of a press.</summary>
    public string Label { get; set; } = "";
    /// <summary>Optional key gesture that switches the tool on and off, e.g. "Shift+H".</summary>
    public string Shortcut { get; set; }
    /// <summary>Cursor over the photo while active: "Cross" (default), "Hand", "Pen", "Arrow", "SizeAll", "None".</summary>
    public string Cursor { get; set; } = "Cross";
    /// <summary>Coordinate space of <see cref="PointerEvent"/> positions (default: the un-cropped, un-rotated photo).</summary>
    public OverlaySpace Space { get; set; } = OverlaySpace.Source;
    /// <summary>Also deliver <see cref="PointerPhase.Hover"/> moves.</summary>
    public bool Hover { get; set; }
    /// <summary>
    /// Mouse input on the photo. Changes made through the context during one press (Down .. Up) are previewed as they
    /// happen and recorded as one undo step labelled <see cref="Label"/> (or <see cref="ICommandContext.StepLabel"/>).
    /// </summary>
    public Action<PointerEvent, ICommandContext> Pointer { get; set; }
    /// <summary>Optional: called when the tool is switched on.</summary>
    public Action<ICommandContext> Activated { get; set; }
    /// <summary>Optional: called when the tool is switched off (another tool, Esc, photo closed, plugin reload).</summary>
    public Action<ICommandContext> Deactivated { get; set; }
    /// <summary>Optional drawing while the tool is active (brush outline, guides), like an overlay; ask again with <see cref="ITool.Refresh"/>.</summary>
    public Func<OverlayRequest, OverlayContent> Draw { get; set; }
}

public interface ITool
{
    /// <summary>Switch the tool on or off (any thread; at most one tool is active).</summary>
    bool Active { get; set; }
    /// <summary>Ask for <see cref="ToolDescriptor.Draw"/> again (any thread).</summary>
    void Refresh();
}

// ---------------------------------------------------------------------
//  Edit events
// ---------------------------------------------------------------------

public sealed class EditChangedEventArgs : EventArgs
{
    public string Path { get; set; } = "";
    /// <summary>
    /// What changed: "core.Exposure", "core.Crop", "core.CurveR", ... and for filters "pluginId:filterId/paramId",
    /// "pluginId:filterId#active", "pluginId:filterId#mask", "#order".
    /// </summary>
    public IReadOnlyList<string> Keys { get; set; } = new string[0];
    /// <summary>True when the change came from undo, redo or a jump in the history.</summary>
    public bool FromHistory { get; set; }
}

public sealed class FilterToggledEventArgs : EventArgs
{
    public string Path { get; set; } = "";
    /// <summary>"pluginId:filterId".</summary>
    public string FilterId { get; set; } = "";
    public bool Active { get; set; }
}

// ---------------------------------------------------------------------
//  Host events
// ---------------------------------------------------------------------

public sealed class ImageEventArgs : EventArgs
{
    public string Path { get; set; } = "";
    public ImageMeta Meta { get; set; } = new ImageMeta();
}

public sealed class SaveEventArgs : EventArgs
{
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    /// <summary>"jpg", "tiff16" or the exporter id.</summary>
    public string Format { get; set; } = "";
    public bool Success { get; set; }
    /// <summary>BeforeSave only: set to cancel the save.</summary>
    public bool Cancel { get; set; }
    public string CancelReason { get; set; }
}

public interface IHostEvents
{
    event EventHandler<ImageEventArgs> ImageOpened;
    event EventHandler<SaveEventArgs> BeforeSave;
    event EventHandler<SaveEventArgs> AfterSave;
    event EventHandler<SaveEventArgs> AfterExport;
    /// <summary>The shown photo is about to be replaced by another one or the viewer closes (UI thread).</summary>
    event EventHandler<ImageEventArgs> ImageClosed;
    /// <summary>An edit of the shown photo became an undo step, or undo / redo changed it (UI thread).</summary>
    event EventHandler<EditChangedEventArgs> EditChanged;
    /// <summary>A filter was switched on or off (UI thread; raised with <see cref="EditChanged"/>).</summary>
    event EventHandler<FilterToggledEventArgs> FilterToggled;
}

// ---------------------------------------------------------------------
//  Host imaging services (GPU accelerated when available)
// ---------------------------------------------------------------------

public interface IImagingServices
{
    /// <summary>Gaussian blur of a single plane (GPU when available). Returns a new plane.</summary>
    float[] GaussianBlur(float[] plane, int width, int height, float sigma);
    /// <summary>Resize so the long side is at most <paramref name="maxLongSide"/> (area/bilinear). Returns the same image when already smaller.</summary>
    PluginImage Resize(PluginImage image, int maxLongSide);
    /// <summary>Encode an sRGB image as JPEG.</summary>
    byte[] EncodeJpeg(PluginImage image, int quality);
    /// <summary>Encode an sRGB image as PNG (8-bit).</summary>
    byte[] EncodePng(PluginImage image);
    float SrgbToLinear(float v);
    float LinearToSrgb(float v);
}

// ---------------------------------------------------------------------
//  Edit history
// ---------------------------------------------------------------------

public sealed class HistoryStep
{
    public int Index { get; set; }
    /// <summary>What the step changed ("Opened" for step 0, e.g. "Exposure", "Crop", "Heal · Spot 1: Size").</summary>
    public string Label { get; set; } = "";
}

/// <summary>Undo steps of the current photo. Members must be used on the UI thread.</summary>
public interface IEditHistory
{
    /// <summary>All saved states, oldest first; step 0 is the state the photo was opened with. Empty when nothing is open.</summary>
    IReadOnlyList<HistoryStep> Steps { get; }
    /// <summary>Index of the state shown now.</summary>
    int Position { get; }
    /// <summary>Show the state <paramref name="index"/> (like undo/redo to it). The next edit drops all later steps.</summary>
    bool GoTo(int index);
    /// <summary>Raised on the UI thread when steps or the position change or another photo is opened.</summary>
    event EventHandler Changed;
}

// ---------------------------------------------------------------------
//  List panels
// ---------------------------------------------------------------------

public sealed class ListPanelItem
{
    public string Text { get; set; } = "";
    /// <summary>Optional dimmed text on the right.</summary>
    public string Detail { get; set; }
}

public sealed class ListPanelDescriptor
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; }
    /// <summary>Current items (called on the UI thread whenever the panel is refreshed).</summary>
    public Func<IReadOnlyList<ListPanelItem>> Items { get; set; }
    /// <summary>Index of the selected item, -1 for none.</summary>
    public Func<int> Selected { get; set; }
    /// <summary>The user selected an item (click or arrow keys).</summary>
    public Action<int> Select { get; set; }
    /// <summary>Rows visible before the list scrolls.</summary>
    public int VisibleRows { get; set; } = 10;
}

public interface IListPanel
{
    /// <summary>Re-read items and selection (any thread).</summary>
    void Refresh();
}

// ---------------------------------------------------------------------
//  Settings
// ---------------------------------------------------------------------

/// <summary>One choice out of a list, kept by the host across sessions (Settings menu > Plugin settings).</summary>
public sealed class SettingDescriptor
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; }
    public IReadOnlyList<ParamChoice> Choices { get; set; } = new ParamChoice[0];
    /// <summary>Choice id used until the user picks another one (first choice when empty or unknown).</summary>
    public string DefaultId { get; set; }
    /// <summary>
    /// The user picked another choice (UI thread, not called at startup). The plugin may change its filters'
    /// <see cref="FilterDescriptor.Stage"/> here; the host then rebuilds its panels and re-renders the photo.
    /// </summary>
    public Action<string> Changed { get; set; }
}

public interface IPluginSetting
{
    /// <summary>Id of the current choice.</summary>
    string Value { get; }
}
