using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using ImgViewer.Imaging;
using ImgViewer.Plugins;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>One file or package on disk (a loose .dll or an installed .pepkg folder).</summary>
public sealed class PluginFileEntry
{
    /// <summary>File name (loose dll) or folder name (package).</summary>
    public string FileName { get; }
    public string FilePath { get; }
    public string Source { get; }
    public bool IsPackage { get; init; }
    public PackageManifest? Package { get; init; }
    public string Sha256 { get; set; } = "";
    public SignatureInfo Signature { get; set; } = SignatureInfo.Unsigned;
    public bool Enabled => Plugins.Any(p => p.Enabled);
    public List<LoadedPlugin> Plugins { get; } = new();
    public string? Error { get; set; }
    public PluginLoadContext? Context { get; set; }

    public PluginFileEntry(string fileName, string filePath, string source)
    {
        FileName = fileName;
        FilePath = filePath;
        Source = source;
    }
}

/// <summary>A plugin (v2 IEyePlugin or adapted v1 IPlugin) and everything it registered.</summary>
public sealed class LoadedPlugin
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public int Api { get; init; }
    public bool IsV1 => Api == 1;
    public string[] Permissions { get; init; } = Array.Empty<string>();
    public PluginFileEntry Entry { get; init; } = null!;
    public bool Enabled { get; set; }
    public string? Error { get; set; }
    public string? DisabledReason { get; set; }
    /// <summary>Set when the plugin was switched off during this session (pipeline skips it immediately).</summary>
    public volatile bool Faulted;
    public int ConsecutiveFailures;
    internal PluginHostImpl? Host { get; set; }
    internal IEyePlugin? Instance { get; set; }

    public List<Registered<FilterDescriptor>> Filters { get; } = new();
    public List<Registered<MaskProviderDescriptor>> Masks { get; } = new();
    public List<Registered<ImporterDescriptor>> Importers { get; } = new();
    public List<Registered<ExporterDescriptor>> Exporters { get; } = new();
    public List<Registered<MetadataProviderDescriptor>> MetaProviders { get; } = new();
    public List<Registered<CommandDescriptor>> Commands { get; } = new();
    public List<Registered<PresetDescriptor>> Presets { get; } = new();
    public List<Registered<ListPanelHandle>> ListPanels { get; } = new();
    public List<Registered<PluginSettingHandle>> Settings { get; } = new();
}

public sealed class Registered<T>
{
    public string PluginId { get; init; } = "";
    public string PluginName { get; init; } = "";
    /// <summary>"pluginId:localId".</summary>
    public string FullId { get; init; } = "";
    public string LocalId { get; init; } = "";
    public T D { get; init; } = default!;
    /// <summary>Null for built-in (core) registrations.</summary>
    public LoadedPlugin? Owner { get; init; }
    /// <summary>v1 adapter: original feature id (used to migrate old .eye keys).</summary>
    public string? LegacyId { get; init; }
    public int Index { get; init; }
    public bool IsCore => Owner is null;
}

/// <summary>Immutable snapshot of everything registered by enabled plugins (plus built-ins).</summary>
public sealed class PluginRegistry
{
    public static PluginRegistry Empty { get; } = new(Array.Empty<LoadedPlugin>(), includeCore: false);

    public IReadOnlyList<Registered<FilterDescriptor>> Filters { get; }
    public IReadOnlyList<Registered<MaskProviderDescriptor>> Masks { get; }
    public IReadOnlyList<Registered<ImporterDescriptor>> Importers { get; }
    public IReadOnlyList<Registered<ExporterDescriptor>> Exporters { get; }
    public IReadOnlyList<Registered<MetadataProviderDescriptor>> MetaProviders { get; }
    public IReadOnlyList<Registered<CommandDescriptor>> Commands { get; }
    public IReadOnlyList<Registered<PresetDescriptor>> Presets { get; }
    public IReadOnlyList<Registered<ListPanelHandle>> ListPanels { get; }
    public IReadOnlyList<Registered<PluginSettingHandle>> Settings { get; }
    public IReadOnlyList<LoadedPlugin> Plugins { get; }
    private readonly Dictionary<string, Registered<FilterDescriptor>> _filterById;

    public PluginRegistry(IEnumerable<LoadedPlugin> plugins, bool includeCore = true)
    {
        var active = plugins.Where(p => p.Enabled && p.Error is null).ToList();
        Plugins = active;
        var filters = new List<Registered<FilterDescriptor>>();
        if (includeCore) filters.AddRange(BuiltInFilters.Create());
        filters.AddRange(active.SelectMany(p => p.Filters));
        Filters = filters;
        Masks = active.SelectMany(p => p.Masks).ToList();
        Importers = active.SelectMany(p => p.Importers).ToList();
        Exporters = active.SelectMany(p => p.Exporters).ToList();
        MetaProviders = active.SelectMany(p => p.MetaProviders).ToList();
        Commands = active.SelectMany(p => p.Commands).ToList();
        Presets = active.SelectMany(p => p.Presets).ToList();
        ListPanels = active.SelectMany(p => p.ListPanels).ToList();
        Settings = active.SelectMany(p => p.Settings).ToList();
        _filterById = new Dictionary<string, Registered<FilterDescriptor>>(StringComparer.Ordinal);
        foreach (var f in filters) _filterById.TryAdd(f.FullId, f);
    }

    public Registered<FilterDescriptor>? FindFilter(string fullId) => _filterById.TryGetValue(fullId, out var f) ? f : null;
    public Registered<MaskProviderDescriptor>? FindMask(string fullId) => Masks.FirstOrDefault(m => m.FullId == fullId);
    public Registered<ImporterDescriptor>? FindImporter(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0) return null;
        return Importers.FirstOrDefault(i => i.D.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)));
    }

    public IEnumerable<string> ImporterExtensions => Importers.SelectMany(i => i.D.Extensions).Select(e => e.ToLowerInvariant()).Distinct();
    public bool HasPlugin(string pluginId) => pluginId == BuiltInFilters.CoreId || Plugins.Any(p => p.Id == pluginId);
}

public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _dir;

    public PluginLoadContext(string name, string dir) : base(name, isCollectible: true)
    {
        _dir = dir;
    }

    protected override Assembly? Load(AssemblyName name)
    {
        // The SDK contract and everything the host already ships (ComputeSharp, System.*, ...) stay unified
        // in the default context. This also works in single-file publish, where app assemblies have no .dll on disk.
        if (name.Name is "ImgViewer.Plugin.Abstractions") return null;
        foreach (var a in Default.Assemblies)
            if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var shared = Default.LoadFromAssemblyName(name);
            if (shared is not null) return null;
        }
        catch (Exception)
        {
        }
        string path = Path.Combine(_dir, name.Name + ".dll");
        return File.Exists(path) ? LoadNoLock(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string path = Path.Combine(_dir, unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : unmanagedDllName + ".dll");
        return File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    /// <summary>Loads from an in-memory copy so the file is never locked (remove/re-import while running).</summary>
    public Assembly LoadNoLock(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string pdb = Path.ChangeExtension(path, ".pdb");
        if (File.Exists(pdb))
        {
            try
            {
                return LoadFromStream(new MemoryStream(bytes), new MemoryStream(File.ReadAllBytes(pdb)));
            }
            catch (BadImageFormatException)
            {
            }
        }
        return LoadFromStream(new MemoryStream(bytes));
    }
}

public sealed class PluginManager
{
    public const int MaxConsecutiveFailures = 3;
    public const int PreviewTimeoutMs = 8000;

    private static PluginManager? _instance;
    public static PluginManager Instance => _instance ??= new PluginManager();

    public static string AppDataDir => SettingsStore.DirectoryPath;

    public static string ImportDir => Path.Combine(AppDataDir, "Plugins");
    public static string PluginDataRoot => Path.Combine(AppDataDir, "PluginData");
    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Directories scanned by Reload (tests may replace them).</summary>
    public List<(string Dir, string Source)> ScanDirs { get; } = new()
    {
        (BundledDir, "bundled"),
        (ImportDir, "imported")
    };

    private static readonly object SettingsGate = new();
    private readonly object _gate = new();
    private List<PluginFileEntry> _files = new();
    private volatile PluginRegistry _registry = new(Array.Empty<LoadedPlugin>());

    public IReadOnlyList<PluginFileEntry> Files { get { lock (_gate) return _files; } }
    public PluginRegistry Registry => _registry;

    /// <summary>Raised after Reload (may be raised on a worker thread).</summary>
    public event Action? Changed;
    /// <summary>Raised (on any thread) when a plugin was switched off automatically.</summary>
    public event Action<LoadedPlugin, string>? PluginFaulted;

    public static string ImportDialogFilter => "PhotographerEye plugin (*.pepkg;*.dll)|*.pepkg;*.dll|Plugin package (*.pepkg)|*.pepkg|Plugin assembly (*.dll)|*.dll";

    public void Reload()
    {
        var files = new List<PluginFileEntry>();
        try
        {
            var enabled = LoadEnabledMap();
            var reasons = SettingsStore.Load().PluginDisabledReason;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (dir, source) in ScanDirs)
                ScanDir(files, dir, source, enabled, reasons, seen);
        }
        catch (Exception ex)
        {
            Debug.Log($"PLUGINS: scan failed [{ex.GetType().Name}] {ex.Message}");
        }

        List<PluginFileEntry> oldFiles;
        lock (_gate)
        {
            oldFiles = _files;
            _files = files;
            _registry = new PluginRegistry(files.SelectMany(f => f.Plugins));
        }
        foreach (var e in oldFiles)
            Unload(e);
        Debug.Log($"PLUGINS: reload files={files.Count} plugins={files.Sum(f => f.Plugins.Count)} filters={_registry.Filters.Count}");
        Changed?.Invoke();
    }

    private static void Unload(PluginFileEntry e)
    {
        foreach (var p in e.Plugins)
        {
            try { (p.Instance as IDisposable)?.Dispose(); } catch (Exception) { }
        }
        try { e.Context?.Unload(); } catch (Exception) { }
    }

    private static void ScanDir(List<PluginFileEntry> files, string dir, string source, Dictionary<string, bool> enabled,
        Dictionary<string, string>? reasons, HashSet<string> seen)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(sub, PluginPackage.ManifestFileName);
            if (!File.Exists(manifestPath)) continue;
            PluginFileEntry entry;
            try
            {
                var pm = PluginPackage.ReadManifest(File.ReadAllText(manifestPath));
                entry = LoadFile(Path.Combine(sub, pm.Entry), source, enabled, reasons, seen, pm, Path.GetFileName(sub));
            }
            catch (Exception ex)
            {
                entry = new PluginFileEntry(Path.GetFileName(sub), sub, source) { IsPackage = true, Error = ex.Message };
            }
            files.Add(entry);
        }
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (name.Equals("ImgViewer.Plugin.Abstractions.dll", StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(LoadFile(path, source, enabled, reasons, seen, null, null));
        }
    }

    /// <summary>Load one plugin assembly with every plugin enabled (tests / tools).</summary>
    public static PluginFileEntry LoadFromFile(string path) =>
        LoadFile(path, "loaded", new Dictionary<string, bool>(), null, new HashSet<string>(), null, null);

    private static PluginFileEntry LoadFile(string path, string source, Dictionary<string, bool> enabled,
        Dictionary<string, string>? reasons, HashSet<string> seen, PackageManifest? pm, string? packageDir)
    {
        var fileName = packageDir ?? Path.GetFileName(path);
        var entry = new PluginFileEntry(fileName, path, source) { IsPackage = pm is not null, Package = pm };
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("entry assembly not found: " + Path.GetFileName(path));
            entry.Sha256 = PluginTrust.Sha256File(path);
            entry.Signature = PluginTrust.CheckSignature(path);
            if (pm is not null && pm.Api > PluginApi.Version)
                throw new InvalidOperationException($"requires plugin API {pm.Api}, this PhotographerEye supports {PluginApi.Version}");

            var ctx = new PluginLoadContext("plugin:" + fileName, Path.GetDirectoryName(path)!);
            entry.Context = ctx;
            var asm = ctx.LoadNoLock(path);
            var apiAttr = asm.GetCustomAttributes().FirstOrDefault(a => a.GetType().FullName == typeof(PluginApiAttribute).FullName);
            if (apiAttr is PluginApiAttribute pa && pa.Version > PluginApi.Version)
                throw new InvalidOperationException($"assembly requires plugin API {pa.Version}, host supports {PluginApi.Version}");

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t is not null).ToArray()!; }

            var v2Types = types.Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && typeof(IEyePlugin).IsAssignableFrom(t)).ToList();
            var v1Types = types.Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && typeof(IPlugin).IsAssignableFrom(t)).ToList();
            if (v2Types.Count == 0 && v1Types.Count == 0)
                throw new InvalidOperationException("no IEyePlugin (v2) or IPlugin (v1) implementation found");

            foreach (var t in v2Types)
            {
                LoadedPlugin lp;
                try
                {
                    var inst = (IEyePlugin)Activator.CreateInstance(t)!;
                    var m = inst.Manifest ?? throw new InvalidOperationException("Manifest is null");
                    var id = string.IsNullOrWhiteSpace(m.Id) ? t.FullName ?? t.Name : m.Id.Trim();
                    lp = new LoadedPlugin
                    {
                        Id = id,
                        Name = string.IsNullOrWhiteSpace(m.Name) ? t.Name : m.Name,
                        Description = m.Description ?? "",
                        Version = m.Version ?? "",
                        Author = m.Author ?? "",
                        Api = m.Api <= 0 ? 2 : m.Api,
                        Permissions = (m.Permissions ?? Array.Empty<string>()).Union(pm?.Permissions ?? Array.Empty<string>()).Distinct().ToArray(),
                        Entry = entry,
                        Instance = inst
                    };
                    if (lp.Api > PluginApi.Version) lp.Error = $"requires plugin API {lp.Api}";
                }
                catch (Exception ex)
                {
                    lp = new LoadedPlugin { Id = t.FullName ?? t.Name, Name = t.Name, Api = 2, Entry = entry, Error = Unwrap(ex).Message };
                }
                AddPlugin(entry, lp, enabled, reasons, seen);
            }
            foreach (var t in v1Types)
            {
                LoadedPlugin lp;
                IPlugin? legacy = null;
                try
                {
                    legacy = (IPlugin)Activator.CreateInstance(t)!;
                    lp = new LoadedPlugin
                    {
                        Id = V1PluginId(fileName, legacy.Name),
                        Name = legacy.Name,
                        Description = legacy.Description ?? "",
                        Version = asm.GetName().Version?.ToString() ?? "",
                        Api = 1,
                        Entry = entry
                    };
                }
                catch (Exception ex)
                {
                    lp = new LoadedPlugin { Id = V1PluginId(fileName, t.Name), Name = t.Name, Api = 1, Entry = entry, Error = Unwrap(ex).Message };
                }
                if (legacy is not null && lp.Error is null)
                    V1Adapter.Register(lp, legacy);
                AddPlugin(entry, lp, enabled, reasons, seen, legacyName: legacy?.Name);
            }
        }
        catch (Exception ex)
        {
            entry.Error = Unwrap(ex).Message;
            if (entry.Context is not null)
            {
                try { entry.Context.Unload(); } catch (Exception) { }
                entry.Context = null;
            }
        }
        return entry;
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: not null } t ? t.InnerException! : ex;

    private static void AddPlugin(PluginFileEntry entry, LoadedPlugin lp, Dictionary<string, bool> enabled,
        Dictionary<string, string>? reasons, HashSet<string> seen, string? legacyName = null)
    {
        if (lp.Error is null && !seen.Add(lp.Id))
            lp.Error = $"duplicate plugin id '{lp.Id}' (already loaded from another file)";
        bool en = enabled.TryGetValue(EnabledKey(lp.Id), out var pe) ? pe
                : legacyName is not null && enabled.TryGetValue(entry.FileName + "::" + legacyName, out var le) ? le
                : !enabled.TryGetValue(entry.FileName, out var fe) || fe;
        lp.Enabled = en && lp.Error is null;
        if (!en && reasons is not null && reasons.TryGetValue(lp.Id, out var why)) lp.DisabledReason = why;
        if (lp.Enabled && lp.Instance is not null)
        {
            var host = new PluginHostImpl(lp);
            lp.Host = host;
            try
            {
                lp.Instance.Initialize(host);
                host.Sealed = true;
            }
            catch (Exception ex)
            {
                lp.Error = "Initialize failed: " + Unwrap(ex).Message;
                lp.Enabled = false;
                lp.Filters.Clear();
                lp.Masks.Clear();
                lp.Importers.Clear();
                lp.Exporters.Clear();
                lp.MetaProviders.Clear();
                lp.Commands.Clear();
                lp.Presets.Clear();
                lp.ListPanels.Clear();
                lp.Settings.Clear();
            }
        }
        entry.Plugins.Add(lp);
    }

    public static string V1PluginId(string fileName, string pluginName) =>
        "v1." + Sanitize(Path.GetFileNameWithoutExtension(fileName)) + "." + Sanitize(pluginName);

    private static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        return new string(chars).Trim('-');
    }

    public static string EnabledKey(string pluginId) => "plugin:" + pluginId;

    // ------------------------------------------------------------------
    //  Install / remove / enable
    // ------------------------------------------------------------------

    /// <summary>Install an inspected package or dll into the import folder.</summary>
    public void Install(PluginInspection inspection)
    {
        Directory.CreateDirectory(ImportDir);
        if (inspection.IsPackage)
        {
            PluginPackage.Extract(inspection.SourcePath, Path.Combine(ImportDir, inspection.InstallFolder));
        }
        else
        {
            string dest = Path.Combine(ImportDir, Path.GetFileName(inspection.SourcePath));
            File.Copy(inspection.SourcePath, dest, overwrite: true);
            // Copy private dependencies sitting next to the dll.
            var srcDir = Path.GetDirectoryName(inspection.SourcePath)!;
            foreach (var dep in inspection.Dependencies)
            {
                var from = Path.Combine(srcDir, dep);
                if (File.Exists(from)) File.Copy(from, Path.Combine(ImportDir, dep), overwrite: true);
            }
        }
        var map = LoadEnabledMap();
        // Package ids come from the manifest; ids of plugins already loaded from the same file/folder are known too.
        string fileKey = inspection.IsPackage ? inspection.InstallFolder : Path.GetFileName(inspection.SourcePath);
        var ids = new HashSet<string>(inspection.PluginIds, StringComparer.Ordinal);
        foreach (var f in Files.Where(f => string.Equals(f.FileName, fileKey, StringComparison.OrdinalIgnoreCase)))
            foreach (var p in f.Plugins) ids.Add(p.Id);
        foreach (var k in map.Keys.Where(k => k.StartsWith("plugin:", StringComparison.Ordinal) && ids.Contains(k.Substring(7))).ToList())
            map.Remove(k);
        map.Remove(fileKey);
        SaveEnabledMap(map, clearReasonsFor: ids);
        Reload();
    }

    /// <summary>Back-compat: import without the trust dialog.</summary>
    public void Import(string path) => Install(PluginPackage.Inspect(path));

    public void Remove(string fileName)
    {
        var entry = Files.FirstOrDefault(f => string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        var map = LoadEnabledMap();
        if (entry.Source == "imported")
        {
            // Assemblies are loaded from memory, so the files are not locked.
            Unload(entry);
            try
            {
                if (entry.IsPackage) Directory.Delete(Path.GetDirectoryName(entry.FilePath)!, recursive: true);
                else if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
            }
            catch (Exception ex)
            {
                Debug.Log($"PLUGINS: remove {fileName} failed [{ex.GetType().Name}] {ex.Message}");
                throw new IOException($"Could not delete {fileName}: {ex.Message}", ex);
            }
            foreach (var p in entry.Plugins) map.Remove(EnabledKey(p.Id));
            map.Remove(fileName);
        }
        else
        {
            // Bundled files stay in the app folder: disable everything they contain.
            foreach (var p in entry.Plugins) map[EnabledKey(p.Id)] = false;
        }
        SaveEnabledMap(map);
        Reload();
    }

    public void SetPluginEnabled(string pluginId, bool enabled)
    {
        var map = LoadEnabledMap();
        map[EnabledKey(pluginId)] = enabled;
        SaveEnabledMap(map, clearReasonsFor: enabled ? new[] { pluginId } : null);
        Reload();
    }

    /// <summary>Called by the pipeline when a plugin threw or timed out.</summary>
    public void ReportFailure(LoadedPlugin? plugin, string what, Exception? ex, bool fatal = false)
    {
        string msg = ex is null ? what : $"{what}: [{ex.GetType().Name}] {ex.Message}";
        Debug.Log($"PLUGIN FAIL: {plugin?.Id ?? "core"} {msg}{(ex is not null ? "\n" + ex.StackTrace : "")}");
        if (plugin is null || plugin.Faulted) return;
        int n = Interlocked.Increment(ref plugin.ConsecutiveFailures);
        if (!fatal && n < MaxConsecutiveFailures) return;
        plugin.Faulted = true;
        string reason = fatal ? msg : $"{n} consecutive failures, last: {msg}";
        plugin.DisabledReason = reason;
        try
        {
            lock (SettingsGate)
            {
                var map = LoadEnabledMap();
                map[EnabledKey(plugin.Id)] = false;
                var st = SettingsStore.Load();
                var reasons = st.PluginDisabledReason is null ? new Dictionary<string, string>() : new Dictionary<string, string>(st.PluginDisabledReason);
                reasons[plugin.Id] = reason;
                SettingsStore.Save(st with
                {
                    PluginFiles = new Dictionary<string, bool>(map, StringComparer.OrdinalIgnoreCase),
                    PluginDisabledReason = reasons
                });
            }
        }
        catch (Exception)
        {
        }
        PluginFaulted?.Invoke(plugin, reason);
    }

    public static void ReportSuccess(LoadedPlugin? plugin)
    {
        if (plugin is not null && plugin.ConsecutiveFailures != 0) Interlocked.Exchange(ref plugin.ConsecutiveFailures, 0);
    }

    public static Dictionary<string, bool> LoadEnabledMap()
    {
        var st = SettingsStore.Load();
        if (st.PluginFiles is null) return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, bool>(st.PluginFiles, StringComparer.OrdinalIgnoreCase);
    }

    public static void SaveEnabledMap(Dictionary<string, bool> map, IEnumerable<string>? clearReasonsFor = null)
    {
        lock (SettingsGate)
        {
            var st = SettingsStore.Load();
            var reasons = st.PluginDisabledReason;
            if (clearReasonsFor is not null && reasons is not null)
            {
                reasons = new Dictionary<string, string>(reasons);
                foreach (var id in clearReasonsFor) reasons.Remove(id);
                if (reasons.Count == 0) reasons = null;
            }
            SettingsStore.Save(st with
            {
                PluginFiles = map.Count > 0 ? new Dictionary<string, bool>(map, StringComparer.OrdinalIgnoreCase) : null,
                PluginDisabledReason = reasons
            });
        }
    }

    // ------------------------------------------------------------------
    //  Host events
    // ------------------------------------------------------------------

    public void RaiseImageOpened(string path, ImageMeta meta)
    {
        foreach (var p in Registry.Plugins)
            p.Host?.EventsImpl.RaiseImageOpened(p, new ImageEventArgs { Path = path, Meta = meta });
    }

    /// <summary>Returns a cancel reason when a plugin cancelled the save.</summary>
    public string? RaiseBeforeSave(string source, string dest, string format)
    {
        foreach (var p in Registry.Plugins)
        {
            if (p.Host is null) continue;
            var e = new SaveEventArgs { SourcePath = source, DestinationPath = dest, Format = format };
            p.Host.EventsImpl.RaiseBeforeSave(p, e);
            if (e.Cancel) return $"{p.Name}: {e.CancelReason ?? "save cancelled"}";
        }
        return null;
    }

    public void RaiseAfterSave(string source, string dest, string format, bool ok, bool export)
    {
        foreach (var p in Registry.Plugins)
        {
            var e = new SaveEventArgs { SourcePath = source, DestinationPath = dest, Format = format, Success = ok };
            if (export) p.Host?.EventsImpl.RaiseAfterExport(p, e);
            else p.Host?.EventsImpl.RaiseAfterSave(p, e);
        }
    }

    public static ImageMeta MetaFor(string path, int width = 0, int height = 0)
    {
        var m = new ImageMeta { SourcePath = path, IsRaw = RawDecoder.IsRaw(path), Width = width, Height = height };
        var pm = PhotoMetaReader.Get(path);
        if (pm is not null)
        {
            m.Camera = pm.Camera;
            m.Date = pm.DateText;
            m.Iso = pm.Iso;
            m.Aperture = pm.Aperture;
            m.ShutterSeconds = pm.ShutterSeconds;
            m.FocalLength = pm.Focal;
        }
        return m;
    }
}

/// <summary>Wraps a v1 IPlugin: each feature becomes a Display-stage filter with params p0..pN.</summary>
public static class V1Adapter
{
    public static void Register(LoadedPlugin lp, IPlugin legacy)
    {
        int index = 0;
        foreach (var f in legacy.Features ?? Array.Empty<PluginFeature>())
        {
            if (f is null || string.IsNullOrWhiteSpace(f.Id)) continue;
            var feature = f;
            var defs = (feature.Params ?? Array.Empty<PluginParamDef>())
                .Select((d, i) => ParamDef.Slider("p" + i, d.Name, d.Min, d.Max, d.Default, d.Step, d.Format))
                .ToArray();
            var desc = new FilterDescriptor
            {
                Id = feature.Id,
                Label = feature.Label,
                Description = legacy.Description ?? "",
                Group = "Plugins",
                Stage = FilterStage.Display,
                Placement = feature.Kind == PluginFeatureKind.ToolbarToggle ? FilterPlacement.Toolbar : FilterPlacement.Panel,
                Order = 1000 + index,
                Params = defs,
                Render = (img, p, ctx) => RunLegacy(feature, defs, img, p)
            };
            lp.Filters.Add(new Registered<FilterDescriptor>
            {
                PluginId = lp.Id,
                PluginName = lp.Name,
                FullId = lp.Id + ":" + feature.Id,
                LocalId = feature.Id,
                D = desc,
                Owner = lp,
                LegacyId = feature.Id,
                Index = index++
            });
        }
    }

    private static void RunLegacy(PluginFeature feature, ParamDef[] defs, PluginImage img, FilterParams p)
    {
        int n = img.Length;
        var r = new ushort[n];
        var g = new ushort[n];
        var b = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            r[i] = ToU16(img.R[i]);
            g[i] = ToU16(img.G[i]);
            b[i] = ToU16(img.B[i]);
        }
        var values = defs.Select(d => p.Get(d.Id)).ToArray();
        feature.Apply(new PluginPixelBuffer(img.Width, img.Height, r, g, b), values);
        const float inv = 1f / 65535f;
        for (int i = 0; i < n; i++)
        {
            img.R[i] = r[i] * inv;
            img.G[i] = g[i] * inv;
            img.B[i] = b[i] * inv;
        }
    }

    private static ushort ToU16(float v) => (ushort)(v <= 0f ? 0 : v >= 1f ? 65535 : (int)(v * 65535f + 0.5f));
}
