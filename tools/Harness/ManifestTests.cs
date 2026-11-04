using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ImgViewer;
using ImgViewer.Plugins;

internal static partial class Program
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ImgViewer.csproj")) && Directory.Exists(Path.Combine(dir.FullName, "eyePlugins")))
                return dir.FullName;
        return null;
    }

    /// <summary>
    /// Every bundled plugin's manifest.json: strict JSON, read by the host like at startup (id, entry, api), and its
    /// entry assembly loads. The plugin tests load the DLLs directly, so a broken manifest would otherwise only show
    /// as "load error" in the app. Bundled plugins are discovered like ImgViewer.csproj does it: every
    /// eyePlugins\&lt;folder&gt;\&lt;Assembly&gt;.csproj ships as plugins\&lt;folder&gt;.
    /// </summary>
    private static void TestManifests()
    {
        var root = FindRepoRoot();
        if (root is null) { Check(false, "manifest: repository root found"); return; }
        var csproj = File.ReadAllText(Path.Combine(root, "ImgViewer.csproj"));
        Check(csproj.Contains(@"<BundledPluginProject Include=""eyePlugins\*\*.csproj"""), @"manifest: ImgViewer.csproj bundles eyePlugins\*\*.csproj");
        var folders = Directory.GetDirectories(Path.Combine(root, "eyePlugins")).Where(d => !Path.GetFileName(d).StartsWith(".")).OrderBy(d => d, StringComparer.Ordinal).ToList();
        var bundled = new System.Collections.Generic.List<(string Folder, string Asm)>();
        foreach (var d in folders)
        {
            var projects = Directory.GetFiles(d, "*.csproj");
            Check(projects.Length == 1, $"manifest {Path.GetFileName(d)}: exactly one project in the folder ({projects.Length})");
            if (projects.Length == 1) bundled.Add((Path.GetFileName(d), Path.GetFileNameWithoutExtension(projects[0])));
        }
        Check(bundled.Count > 0, $"manifest: bundled plugin projects found ({bundled.Count})");
        var ids = new System.Collections.Generic.HashSet<string>();
        foreach (var (folder, asm) in bundled)
        {
            var path = Path.Combine(root, "eyePlugins", folder, PluginPackage.ManifestFileName);
            if (!File.Exists(path)) { Check(false, $"manifest {folder}: file present"); continue; }
            var json = File.ReadAllText(path);
            string? strictErr = null;
            try { using var _ = JsonDocument.Parse(json); }
            catch (JsonException ex) { strictErr = ex.Message; }
            Check(strictErr is null, $"manifest {folder}: valid JSON{(strictErr is null ? "" : " (" + strictErr + ")")}");
            PackageManifest? m = null;
            string? readErr = null;
            try { m = PluginPackage.ReadManifest(json); }
            catch (Exception ex) { readErr = ex.Message; }
            Check(m is not null, $"manifest {folder}: read by the host{(readErr is null ? "" : " (" + readErr + ")")}");
            if (m is null) continue;
            Check(m.Entry == asm + ".dll", $"manifest {folder}: entry {m.Entry} matches the project {asm}");
            Check(m.Api == PluginApi.Version, $"manifest {folder}: api {m.Api} = host {PluginApi.Version}");
            Check(ids.Add(m.Id), $"manifest {folder}: id {m.Id} unique");
            Check(!string.IsNullOrWhiteSpace(m.Name) && !string.IsNullOrWhiteSpace(m.Description), $"manifest {folder}: name and description");
            var dll = FindPluginDll(folder, m.Entry);
            if (dll is null) { Check(false, $"manifest {folder}: entry assembly built"); continue; }
            var e = PluginManager.LoadFromFile(dll);
            Check(e.Error is null && e.Plugins.Count > 0 && e.Plugins.All(p => p.Error is null), $"manifest {folder}: entry assembly loads ({e.Error})");
            // The app build copies it to plugins\<folder>\ next to PhotographerEye.exe.
            var shipped = new[] { "Debug", "Release" }
                .Select(cfg => Path.Combine(root, "bin", cfg, "net8.0-windows", "plugins", folder))
                .Where(Directory.Exists).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
            Check(File.Exists(Path.Combine(shipped, m.Entry)) && File.Exists(Path.Combine(shipped, PluginPackage.ManifestFileName)),
                  $"manifest {folder}: copied to plugins\\{folder} by the app build");
        }
    }
}
