using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImgViewer.Plugins;

namespace ImgViewer;

/// <summary>manifest.json inside a .pepkg package.</summary>
public sealed class PackageManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public int Api { get; set; } = PluginApi.Version;
    /// <summary>Entry assembly file name inside the package.</summary>
    public string Entry { get; set; } = "";
    public string[] Permissions { get; set; } = Array.Empty<string>();
}

public sealed record SignatureInfo(bool Signed, bool Valid, string? Subject, string Status)
{
    public static SignatureInfo Unsigned { get; } = new(false, false, null, "Not signed");
    public string Display => Signed ? (Valid ? $"Signed by {Subject}" : $"Invalid signature ({Status}){(Subject is null ? "" : " — " + Subject)}") : "Not signed";
}

/// <summary>What the trust dialog shows before anything is installed or executed.</summary>
public sealed class PluginInspection
{
    public string SourcePath { get; init; } = "";
    public bool IsPackage { get; init; }
    public PackageManifest Manifest { get; init; } = new();
    public string PackageSha256 { get; init; } = "";
    public string EntrySha256 { get; init; } = "";
    public SignatureInfo Signature { get; init; } = SignatureInfo.Unsigned;
    /// <summary>Plugin types found in the assembly metadata (no code executed).</summary>
    public IReadOnlyList<string> PluginTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PluginIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    public string InstallFolder { get; init; } = "";
    public bool AlreadyInstalled { get; init; }
    public List<string> Warnings { get; } = new();
}

public static class PluginPackage
{
    public const string ManifestFileName = "manifest.json";
    public const string Extension = ".pepkg";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static PackageManifest ReadManifest(string json)
    {
        var m = JsonSerializer.Deserialize<PackageManifest>(json, JsonOpts) ?? throw new InvalidDataException("manifest.json is empty");
        if (string.IsNullOrWhiteSpace(m.Id)) throw new InvalidDataException("manifest.json: id is missing");
        if (string.IsNullOrWhiteSpace(m.Entry)) throw new InvalidDataException("manifest.json: entry is missing");
        if (m.Entry.Contains('/') || m.Entry.Contains('\\') || m.Entry.Contains("..")) throw new InvalidDataException("manifest.json: entry must be a file name");
        if (m.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || m.Id.Contains(':')) throw new InvalidDataException("manifest.json: id contains invalid characters");
        m.Permissions ??= Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(m.Name)) m.Name = m.Id;
        return m;
    }

    public static string WriteManifest(PackageManifest m) => JsonSerializer.Serialize(m, JsonOpts);

    public static PluginInspection Inspect(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("plugin file not found", path);
        return path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || IsZip(path)
            ? InspectPackage(path)
            : InspectDll(path);
    }

    private static bool IsZip(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> sig = stackalloc byte[4];
        return fs.Read(sig) == 4 && sig[0] == 'P' && sig[1] == 'K' && sig[2] == 3 && sig[3] == 4;
    }

    private static PluginInspection InspectPackage(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var me = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidDataException("package has no manifest.json");
        string json;
        using (var r = new StreamReader(me.Open())) json = r.ReadToEnd();
        var m = ReadManifest(json);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(m.Entry, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"package has no entry assembly '{m.Entry}'");
        foreach (var e in zip.Entries) CheckEntryPath(e.FullName);

        // Signature check needs a real file: extract the entry dll to a private temp folder.
        string tmpDir = Path.Combine(Path.GetTempPath(), "pe-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            string tmp = Path.Combine(tmpDir, Path.GetFileName(m.Entry));
            entry.ExtractToFile(tmp);
            var (types, deps) = ScanAssembly(tmp);
            var insp = new PluginInspection
            {
                SourcePath = path,
                IsPackage = true,
                Manifest = m,
                PackageSha256 = PluginTrust.Sha256File(path),
                EntrySha256 = PluginTrust.Sha256File(tmp),
                Signature = PluginTrust.CheckSignature(tmp),
                PluginTypes = types,
                PluginIds = new[] { m.Id },
                Dependencies = deps,
                Files = zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith("/")).Select(e => e.FullName).ToArray(),
                InstallFolder = m.Id,
                AlreadyInstalled = Directory.Exists(Path.Combine(PluginManager.ImportDir, m.Id))
            };
            if (m.Api > PluginApi.Version) insp.Warnings.Add($"Requires plugin API {m.Api}; this PhotographerEye supports {PluginApi.Version}. It will not load.");
            if (types.Count == 0) insp.Warnings.Add("No plugin class found in the entry assembly.");
            foreach (var p in m.Permissions.Where(p => !PluginTrust.KnownPermissions.ContainsKey(p)))
                insp.Warnings.Add($"Unknown permission '{p}'.");
            return insp;
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch (Exception) { }
        }
    }

    private static PluginInspection InspectDll(string path)
    {
        var (types, deps) = ScanAssembly(path);
        var dir = Path.GetDirectoryName(path)!;
        var localDeps = deps.Where(d => File.Exists(Path.Combine(dir, d))).ToArray();
        var name = Path.GetFileNameWithoutExtension(path);
        var insp = new PluginInspection
        {
            SourcePath = path,
            IsPackage = false,
            Manifest = new PackageManifest { Id = name, Name = name, Entry = Path.GetFileName(path), Api = 0 },
            PackageSha256 = "",
            EntrySha256 = PluginTrust.Sha256File(path),
            Signature = PluginTrust.CheckSignature(path),
            PluginTypes = types,
            Dependencies = localDeps,
            Files = new[] { Path.GetFileName(path) }.Concat(localDeps).ToArray(),
            InstallFolder = "",
            AlreadyInstalled = File.Exists(Path.Combine(PluginManager.ImportDir, Path.GetFileName(path)))
        };
        insp.Warnings.Add("Loose assembly: no manifest, permissions are not declared.");
        if (types.Count == 0) insp.Warnings.Add("No plugin class (IEyePlugin / IPlugin) found in this assembly.");
        return insp;
    }

    /// <summary>Reads assembly metadata only (no code runs): plugin types and referenced assembly file names.</summary>
    public static (List<string> Types, List<string> Deps) ScanAssembly(string path)
    {
        var types = new List<string>();
        var deps = new List<string>();
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) throw new BadImageFormatException("not a .NET assembly");
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                foreach (var ih in td.GetInterfaceImplementations())
                {
                    var iface = md.GetInterfaceImplementation(ih).Interface;
                    string? iname = iface.Kind switch
                    {
                        HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)iface).Name),
                        HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)iface).Name),
                        _ => null
                    };
                    if (iname is "IEyePlugin" or "IPlugin")
                    {
                        var ns = md.GetString(td.Namespace);
                        types.Add((ns.Length > 0 ? ns + "." : "") + md.GetString(td.Name) + (iname == "IPlugin" ? " (API v1)" : ""));
                    }
                }
            }
            foreach (var ah in md.AssemblyReferences)
            {
                var n = md.GetString(md.GetAssemblyReference(ah).Name);
                if (n is "ImgViewer.Plugin.Abstractions" or "netstandard" or "mscorlib") continue;
                if (n.StartsWith("System", StringComparison.Ordinal) || n.StartsWith("Microsoft.", StringComparison.Ordinal)) continue;
                deps.Add(n + ".dll");
            }
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException("Not a .NET plugin assembly: " + ex.Message, ex);
        }
        return (types, deps);
    }

    private static void CheckEntryPath(string name)
    {
        if (name.Contains("..") || Path.IsPathRooted(name) || name.StartsWith("/") || name.StartsWith("\\"))
            throw new InvalidDataException($"package contains an unsafe path '{name}'");
    }

    /// <summary>Extract a package into <paramref name="destDir"/> (replacing an existing install).</summary>
    public static void Extract(string packagePath, string destDir)
    {
        var parent = Path.GetDirectoryName(destDir)!;
        Directory.CreateDirectory(parent);
        string staging = destDir + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Directory.CreateDirectory(staging);
        try
        {
            string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(packagePath))
            {
                foreach (var e in zip.Entries)
                {
                    CheckEntryPath(e.FullName);
                    var target = Path.GetFullPath(Path.Combine(staging, e.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("unsafe path in package");
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: true);
                }
            }
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
            Directory.Move(staging, destDir);
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Build a package from a folder (used by tools/tests).</summary>
    public static void Create(string folder, PackageManifest manifest, string outputPath)
    {
        if (File.Exists(outputPath)) File.Delete(outputPath);
        using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);
        var me = zip.CreateEntry(ManifestFileName);
        using (var w = new StreamWriter(me.Open())) w.Write(WriteManifest(manifest));
        foreach (var f in Directory.GetFiles(folder))
        {
            if (Path.GetFileName(f).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)) continue;
            zip.CreateEntryFromFile(f, Path.GetFileName(f));
        }
    }
}

public static class PluginTrust
{
    public static readonly IReadOnlyDictionary<string, string> KnownPermissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [PluginPermissions.FileSystem] = "Read and write files outside its data folder",
        [PluginPermissions.Network] = "Connect to the internet (upload / download)",
        [PluginPermissions.Process] = "Start other programs",
        [PluginPermissions.Gpu] = "Use the graphics card directly"
    };

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    public static SignatureInfo CheckSignature(string path)
    {
        string? subject = null;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            subject = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (string.IsNullOrWhiteSpace(subject)) subject = cert.Subject;
        }
        catch (Exception)
        {
            return SignatureInfo.Unsigned;
        }
        try
        {
            int rc = WinTrust.Verify(path);
            return rc == 0
                ? new SignatureInfo(true, true, subject, "Valid")
                : new SignatureInfo(true, false, subject, $"0x{rc:X8}");
        }
        catch (Exception ex)
        {
            return new SignatureInfo(true, false, subject, ex.Message);
        }
    }

    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        public static int Verify(string path)
        {
            var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            IntPtr pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,              // WTD_UI_NONE
                    fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
                    dwUnionChoice = 1,           // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,
                    dwProvFlags = 0x00000080     // WTD_CACHE_ONLY_URL_RETRIEVAL (no network)
                };
                pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
                Marshal.StructureToPtr(data, pData, false);
                return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, pData);
            }
            finally
            {
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
