using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PopBar.Sdk;

namespace PopBar.Core.Llm;

/// <summary>
/// Any server speaking the OpenAI chat-completions protocol (LiteLLM, Ollama, DeepSeek, vLLM, ...).
/// Presets are just instances with a different id / default URL / model.
/// </summary>
internal class OpenAiCompatibleProvider : ILlmProvider
{
    public OpenAiCompatibleProvider(string id, string name, string? defaultBaseUrl, string? defaultModel, bool requiresApiKey, string? hint)
    {
        Id = id; Name = name; DefaultBaseUrl = defaultBaseUrl; DefaultModel = defaultModel; RequiresApiKey = requiresApiKey; Hint = hint;
    }

    public string Id { get; }
    public string Name { get; }
    public string? DefaultBaseUrl { get; }
    public string? DefaultModel { get; }
    public bool RequiresApiKey { get; }
    // No effort: /chat/completions has no portable reasoning-depth field.
    public LlmFeatures Features => LlmFeatures.Streaming | LlmFeatures.SystemPrompt | LlmFeatures.ModelList;
    public string? Hint { get; }

    public static OpenAiCompatibleProvider Generic() => new("openai", "OpenAI 兼容", null, null, false,
        "填到 /v1 为止，如 http://localhost:4000/v1（LiteLLM）或 https://api.openai.com/v1");
    public static OpenAiCompatibleProvider Ollama() => new("ollama", "Ollama（本地）", "http://localhost:11434/v1", "qwen2.5", false,
        "本机 Ollama，无需密钥；模型名用「获取模型」拉取");
    public static OpenAiCompatibleProvider DeepSeek() => new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", true,
        "需要 DeepSeek API Key");

    /// <summary>
    /// Send with one retry for transport-level failures (a TLS record mangled by a proxy / TUN, a reset connection).
    /// Nothing of the response has been consumed at that point, so repeating the request is safe.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(LlmConnection c, Func<HttpRequestMessage> make, HttpCompletionOption option, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var msg = make();
            if (!string.IsNullOrWhiteSpace(c.ApiKey)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.ApiKey);
            try { return await c.Http.SendAsync(msg, option, ct); }
            catch (HttpRequestException ex) when (attempt == 0 && ex.StatusCode == null && ex.InnerException is System.IO.IOException or System.Net.Sockets.SocketException)
            {
                msg.Dispose();
                await Task.Delay(300, ct);
            }
        }
    }

    private static string Root(LlmConnection c)
    {
        var url = c.BaseUrl.TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) url = url[..^"/chat/completions".Length];
        return url;
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmConnection c, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(req.System)) messages.Add(new { role = "system", content = req.System });
        messages.Add(new { role = "user", content = req.User });
        var body = JsonSerializer.Serialize(new { model = c.Model, max_tokens = req.MaxTokens, stream = true, messages });

        using var resp = await SendAsync(c, () => new HttpRequestMessage(HttpMethod.Post, Root(c) + "/chat/completions") { Content = new StringContent(body, Encoding.UTF8, "application/json") },
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(err, 300)}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            string? chunk = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var c0 = choices[0];
                    if (c0.TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        chunk = content.GetString();
                }
            }
            catch (JsonException) { /* keep-alive or partial line */ }
            if (!string.IsNullOrEmpty(chunk)) yield return chunk;
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(LlmConnection c, CancellationToken ct)
    {
        using var resp = await SendAsync(c, () => new HttpRequestMessage(HttpMethod.Get, Root(c) + "/models"), HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 300)}");
        using var doc = JsonDocument.Parse(body);
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) list.Add(id.GetString()!);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
