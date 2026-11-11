using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ImgViewer.Services.Ai;

/// <summary>Chat transport of the assistant (Ollama in the app, a scripted fake in the harness).</summary>
public interface IAiChat
{
    /// <summary>
    /// One model call: <paramref name="messages"/> in Ollama format (role, content, images, tool_calls, tool_name),
    /// <paramref name="tools"/> as function schemas. Returns the assistant message object.
    /// </summary>
    Task<JsonObject> ChatAsync(string model, JsonArray messages, JsonArray tools, CancellationToken ct);
}

/// <summary>A model offered by the server with what it can do.</summary>
public sealed record AiModelInfo(string Name, long Size, IReadOnlyList<string> Capabilities, bool CapabilitiesKnown);

/// <summary>HTTP client of the Ollama API (local server or ollama.com): model list, model capabilities, chat.</summary>
public sealed class OllamaClient : IAiChat, IDisposable
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }

    public OllamaClient(string baseUrl, string? apiKey, TimeSpan? timeout = null)
    {
        BaseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? AiSettings.LocalDefault : baseUrl.Trim()).TrimEnd('/');
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotographerEye");
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    public static OllamaClient For(AiSettings s) => new(s.BaseUrl, s.ApiKey());

    public void Dispose() => _http.Dispose();

    /// <summary>Models of the server (/api/tags), capabilities filled from /api/show where the server gives them.</summary>
    public async Task<List<AiModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var tags = await GetJsonAsync("/api/tags", ct).ConfigureAwait(false);
        var names = ParseTags(tags);
        var result = new List<AiModelInfo>();
        // /api/show per model, a few at a time (a local server answers fast; the cloud may not offer it)
        using var gate = new SemaphoreSlim(4);
        var tasks = names.Select(async n =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var caps = await ShowCapabilitiesAsync(n.Name, ct).ConfigureAwait(false);
                return new AiModelInfo(n.Name, n.Size, caps ?? Array.Empty<string>(), caps is not null);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        foreach (var t in tasks) result.Add(await t.ConfigureAwait(false));
        return result;
    }

    /// <summary>Capabilities of a model ("completion", "tools", "vision", "thinking", ...); null when the server does not say.</summary>
    public async Task<string[]?> ShowCapabilitiesAsync(string model, CancellationToken ct)
    {
        try
        {
            var body = new JsonObject { ["model"] = model };
            var res = await PostJsonAsync("/api/show", body, ct).ConfigureAwait(false);
            return ParseCapabilities(res);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<JsonObject> ChatAsync(string model, JsonArray messages, JsonArray tools, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = Json.Clone(messages),
            ["stream"] = false,
            ["options"] = new JsonObject { ["temperature"] = 0.3 }
        };
        if (tools.Count > 0) body["tools"] = Json.Clone(tools);
        var res = await PostJsonAsync("/api/chat", body, ct).ConfigureAwait(false);
        if (res["error"] is JsonNode err) throw new InvalidOperationException("Ollama: " + err);
        return res["message"] as JsonObject ?? throw new InvalidOperationException("Ollama: the answer has no message");
    }

    private async Task<JsonObject> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(BaseUrl + path, ct).ConfigureAwait(false);
        return await ReadAsync(resp, ct).ConfigureAwait(false);
    }

    private async Task<JsonObject> PostJsonAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BaseUrl + path, content, ct).ConfigureAwait(false);
        return await ReadAsync(resp, ct).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string detail = text;
            try
            {
                if (JsonNode.Parse(text) is JsonObject o && o["error"] is JsonNode e) detail = e.ToString();
            }
            catch (Exception)
            {
            }
            if (detail.Length > 300) detail = detail[..300] + "…";
            throw new InvalidOperationException($"Ollama {(int)resp.StatusCode} {resp.ReasonPhrase}: {detail}".Trim());
        }
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("Ollama: not a JSON object");
    }

    // ---- parsing (public for the harness) ----

    public static List<(string Name, long Size)> ParseTags(JsonObject tags)
    {
        var list = new List<(string, long)>();
        if (tags["models"] is not JsonArray arr) return list;
        foreach (var m in arr.OfType<JsonObject>())
        {
            var name = (m["name"] ?? m["model"])?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            long size = m["size"] is JsonValue v && v.TryGetValue<long>(out var sz) ? sz : 0;
            list.Add((name, size));
        }
        return list.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string[]? ParseCapabilities(JsonObject show) =>
        show["capabilities"] is JsonArray caps
            ? caps.Select(c => c?.GetValue<string>() ?? "").Where(c => c.Length > 0).Select(c => c.ToLowerInvariant()).ToArray()
            : null;
}
