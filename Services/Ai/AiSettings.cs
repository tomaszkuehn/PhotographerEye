using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace ImgViewer.Services.Ai;

/// <summary>AI assistant settings, stored in settings.json (<see cref="ViewerSettings.Ai"/>).</summary>
public sealed record AiSettings
{
    public const string LocalDefault = "http://localhost:11434";
    public const string CloudDefault = "https://ollama.com";

    /// <summary>"local" (Ollama on this computer, also its -cloud models) or "cloud" (ollama.com with an API key).</summary>
    public string Mode { get; init; } = "local";
    public string LocalUrl { get; init; } = LocalDefault;
    public string CloudUrl { get; init; } = CloudDefault;
    /// <summary>Model chosen for each mode (key = mode).</summary>
    public string? LocalModel { get; init; }
    public string? CloudModel { get; init; }
    /// <summary>API key encrypted with DPAPI for the current Windows user (base64), see <see cref="AiKeyProtector"/>.</summary>
    public string? ApiKeyProtected { get; init; }
    public const int DefaultMaxSteps = 8;
    public const int MaxMaxSteps = 30;

    /// <summary>Most model calls in one assistant turn (1..<see cref="MaxMaxSteps"/>); above the default the window warns.</summary>
    public int MaxSteps { get; init; } = DefaultMaxSteps;

    public const int PromptHistoryMax = 50;

    /// <summary>Requests sent to the assistant, oldest first (all photos; Up/Down in the input recalls them).</summary>
    public List<string>? PromptHistory { get; init; }

    /// <summary><paramref name="text"/> appended as the newest entry (an earlier equal entry moves up), at most <see cref="PromptHistoryMax"/>.</summary>
    public AiSettings WithPrompt(string text)
    {
        var list = (PromptHistory ?? new List<string>()).Where(p => p != text).ToList();
        list.Add(text);
        if (list.Count > PromptHistoryMax) list.RemoveRange(0, list.Count - PromptHistoryMax);
        return this with { PromptHistory = list };
    }
    /// <summary>Context window asked from Ollama (num_ctx); 0 = the server's default (often only 4k, too small with a preview).</summary>
    public int ContextTokens { get; init; } = 16384;
    /// <summary>Most tokens (read + written, all calls) one photo's conversation may use; 0 = no limit.</summary>
    public long TokenBudget { get; init; } = 200_000;
    /// <summary>Send camera, ISO, aperture, shutter, focal length and date to the model (never GPS or the file path).</summary>
    public bool SendMetadata { get; init; }
    /// <summary>The user agreed to send the preview of the photo to ollama.com.</summary>
    public bool CloudConsent { get; init; }

    public const int StyleTextMax = 2000;

    /// <summary>The user's named style prompts (the look they want); the active one is added to a conversation's first request.</summary>
    public List<AiStylePrompt>? StylePrompts { get; init; }
    /// <summary>Name of the style prompt in use; null or unknown = none.</summary>
    public string? ActiveStyle { get; init; }

    /// <summary>The style prompt in use, or null (none chosen, or it has no text).</summary>
    [JsonIgnore]
    public AiStylePrompt? Style => ActiveStyle is { Length: > 0 } n
        ? StylePrompts?.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Text))
        : null;

    /// <summary>Adds or replaces (same name, any case) the style prompt <paramref name="name"/> and makes it the active one.</summary>
    public AiSettings WithStyle(string name, string text, string? replacing = null)
    {
        name = name.Trim();
        text = text.Trim();
        if (text.Length > StyleTextMax) text = text[..StyleTextMax];
        var list = (StylePrompts ?? new List<AiStylePrompt>())
            .Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        (replacing is null || !string.Equals(p.Name, replacing, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        int at = replacing is null ? -1 : (StylePrompts ?? new()).FindIndex(p => string.Equals(p.Name, replacing, StringComparison.OrdinalIgnoreCase));
        var item = new AiStylePrompt(name, text);
        if (at >= 0 && at <= list.Count) list.Insert(at, item);
        else list.Add(item);
        return this with { StylePrompts = list, ActiveStyle = name };
    }

    /// <summary>Removes the style prompt <paramref name="name"/>; if it was active, no style is used.</summary>
    public AiSettings WithoutStyle(string name)
    {
        var list = (StylePrompts ?? new List<AiStylePrompt>()).Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        bool active = string.Equals(ActiveStyle, name, StringComparison.OrdinalIgnoreCase);
        return this with { StylePrompts = list, ActiveStyle = active ? null : ActiveStyle };
    }

    [JsonIgnore] public bool IsCloud => string.Equals(Mode, "cloud", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string BaseUrl => (IsCloud ? CloudUrl : LocalUrl) is { Length: > 0 } u ? u.TrimEnd('/') : IsCloud ? CloudDefault : LocalDefault;
    [JsonIgnore] public string? Model => IsCloud ? CloudModel : LocalModel;
    public AiSettings WithModel(string? model) => IsCloud ? this with { CloudModel = model } : this with { LocalModel = model };

    /// <summary>The API key for cloud mode: the stored one, else OLLAMA_API_KEY. Local mode sends none.</summary>
    public string? ApiKey()
    {
        if (!IsCloud) return null;
        var k = AiKeyProtector.Unprotect(ApiKeyProtected);
        if (!string.IsNullOrWhiteSpace(k)) return k;
        var env = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }
}

/// <summary>A named style prompt: how the user wants their photos to look, in their own words.</summary>
public sealed record AiStylePrompt(string Name, string Text);

/// <summary>Encrypts the API key for the current Windows user (DPAPI, CryptProtectData) without extra packages.</summary>
public static class AiKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PhotographerEye.AiKey.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var data = Transform(Encoding.UTF8.GetBytes(plain), protect: true);
        return data is null ? null : Convert.ToBase64String(data);
    }

    public static string? Unprotect(string? protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64)) return null;
        try
        {
            var data = Transform(Convert.FromBase64String(protectedB64), protect: false);
            return data is null ? null : Encoding.UTF8.GetString(data);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1;

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var hIn = GCHandle.Alloc(input, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = hIn.AddrOfPinnedObject() };
            var entBlob = new DataBlob { Size = Entropy.Length, Data = hEnt.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref inBlob, "PhotographerEye AI key", ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok || outBlob.Data == IntPtr.Zero) return null;
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
        finally
        {
            hIn.Free();
            hEnt.Free();
        }
    }
}
