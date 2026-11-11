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
    /// <paramref name="tools"/> as function schemas. Returns the assistant message and the tokens the call used.
    /// </summary>
    Task<AiChatReply> ChatAsync(string model, JsonArray messages, JsonArray tools, CancellationToken ct);
}

/// <summary>
/// The assistant message of one call and its token counts: <paramref name="PromptTokens"/> is what the model read
/// (the whole conversation sent = the context size), <paramref name="OutputTokens"/> what it wrote. 0 = not reported.
/// </summary>
public sealed record AiChatReply(JsonObject Message, int PromptTokens = 0, int OutputTokens = 0);

/// <summary>A model offered by the server with what it can do; <paramref name="ContextLength"/> 0 = unknown.</summary>
public sealed record AiModelInfo(string Name, long Size, IReadOnlyList<string> Capabilities, bool CapabilitiesKnown, int ContextLength = 0);

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

    /// <summary>Context window asked from the server (options.num_ctx); 0 = the server's default.</summary>
    public int ContextTokens { get; set; }

    public static OllamaClient For(AiSettings s) => new(s.BaseUrl, s.ApiKey()) { ContextTokens = s.ContextTokens };

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
                var show = await ShowAsync(n.Name, ct).ConfigureAwait(false);
                var caps = show is null ? null : ParseCapabilities(show);
                return new AiModelInfo(n.Name, n.Size, caps ?? Array.Empty<string>(), caps is not null,
                    show is null ? 0 : ParseContextLength(show));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        foreach (var t in tasks) result.Add(await t.ConfigureAwait(false));
        return result;
    }

    /// <summary>/api/show of a model (capabilities, model_info); null when the server does not give it.</summary>
    public async Task<JsonObject?> ShowAsync(string model, CancellationToken ct)
    {
        try
        {
            var body = new JsonObject { ["model"] = model };
            return await PostJsonAsync("/api/show", body, ct).ConfigureAwait(false);
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

    public async Task<AiChatReply> ChatAsync(string model, JsonArray messages, JsonArray tools, CancellationToken ct)
    {
        var options = new JsonObject { ["temperature"] = 0.3 };
        if (ContextTokens > 0) options["num_ctx"] = ContextTokens;
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = Json.Clone(messages),
            ["stream"] = false,
            ["options"] = options
        };
        if (tools.Count > 0) body["tools"] = Json.Clone(tools);
        var res = await PostJsonAsync("/api/chat", body, ct).ConfigureAwait(false);
        return ParseChat(res);
    }

    /// <summary>The /api/chat answer: message and token counts (prompt_eval_count, eval_count).</summary>
    public static AiChatReply ParseChat(JsonObject res)
    {
        if (res["error"] is JsonNode err) throw new InvalidOperationException("Ollama: " + err);
        var msg = res["message"] as JsonObject ?? throw new InvalidOperationException("Ollama: the answer has no message");
        return new AiChatReply(msg, Int(res["prompt_eval_count"]), Int(res["eval_count"]));
    }

    private static int Int(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<long>(out var l) ? (int)Math.Clamp(l, 0, int.MaxValue)
        : n is JsonValue d && d.TryGetValue<double>(out var x) && double.IsFinite(x) ? (int)Math.Clamp(x, 0, int.MaxValue) : 0;

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
            throw new HttpRequestException($"Ollama {(int)resp.StatusCode} {resp.ReasonPhrase}: {detail}".Trim(), null, resp.StatusCode);
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

    /// <summary>The model's context window from /api/show model_info ("&lt;arch&gt;.context_length"); 0 = not given.</summary>
    public static int ParseContextLength(JsonObject show) =>
        show["model_info"] is JsonObject info
            ? info.Where(kv => kv.Key.EndsWith(".context_length", StringComparison.Ordinal)).Select(kv => Int(kv.Value)).DefaultIfEmpty(0).Max()
            : 0;

    public static string[]? ParseCapabilities(JsonObject show) =>
        show["capabilities"] is JsonArray caps
            ? caps.Select(c => c?.GetValue<string>() ?? "").Where(c => c.Length > 0).Select(c => c.ToLowerInvariant()).ToArray()
            : null;
}
