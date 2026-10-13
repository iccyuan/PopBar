using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public LlmFeatures Features => LlmFeatures.Streaming | LlmFeatures.SystemPrompt | LlmFeatures.ModelList | LlmFeatures.Thinking;
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
        await foreach (var chunk in StreamChunksAsync(c, req, ct))
            if (!chunk.IsThinking) yield return chunk.Text;
    }

    public async IAsyncEnumerable<LlmChunk> StreamChunksAsync(LlmConnection c, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var shape = new BodyShape(CompletionTokens: UsesCompletionTokens(c.Model), Thinking: req.Thinking);
        HttpResponseMessage resp;
        while (true)
        {
            var body = Body(c, req, shape);
            resp = await SendAsync(c, () => new HttpRequestMessage(HttpMethod.Post, Root(c) + "/chat/completions") { Content = new StringContent(body, Encoding.UTF8, "application/json") },
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode) break;
            var err = await resp.Content.ReadAsStringAsync(ct);
            int status = (int)resp.StatusCode;
            var reason = resp.ReasonPhrase;
            resp.Dispose();
            // A 400 about one of our optional fields: reshape the request and ask again (each field flips once).
            if (status == 400 && Reshape(shape, err) is { } next) { shape = next; continue; }
            throw new HttpRequestException($"{status} {reason}: {Truncate(err, 300)}");
        }

        using (resp)
        {
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);
            var tags = new ThinkTagSplitter();
            // Not `while (!reader.EndOfStream)`: EndOfStream is a blocking read, and this loop runs on the UI thread,
            // so the panel could not repaint between chunks and the whole answer appeared at once.
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                string? content = null, reasoning = null, error = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    // Relays and Ollama report failures inside a 200 stream; without this the panel just stays empty.
                    if (root.TryGetProperty("error", out var e)) error = ErrorText(e);
                    else if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                             && choices[0].TryGetProperty("delta", out var d))
                    {
                        content = Str(d, "content");
                        // DeepSeek / vLLM / SiliconFlow call it reasoning_content, Ollama / OpenRouter reasoning.
                        reasoning = Str(d, "reasoning_content") ?? Str(d, "reasoning");
                    }
                }
                catch (JsonException) { /* keep-alive or partial line */ }
                if (error != null) throw new HttpRequestException(error);
                if (!string.IsNullOrEmpty(reasoning)) yield return new LlmChunk(reasoning, true);
                if (!string.IsNullOrEmpty(content))
                    foreach (var chunk in tags.Feed(content)) yield return chunk;
            }
            foreach (var chunk in tags.Flush()) yield return chunk;
        }
    }

    /// <summary>Which optional fields the request carries; flipped one at a time when the server rejects them.</summary>
    private sealed record BodyShape(bool CompletionTokens, bool Thinking);

    private string Body(LlmConnection c, LlmRequest req, BodyShape shape)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(req.System)) messages.Add(new { role = "system", content = req.System });
        messages.Add(new { role = "user", content = req.User });
        var body = new Dictionary<string, object> { ["model"] = c.Model, ["stream"] = true, ["messages"] = messages };
        // Thinking tokens count towards the limit: leave room for an answer after it.
        body[shape.CompletionTokens ? "max_completion_tokens" : "max_tokens"] = req.Thinking ? Math.Max(req.MaxTokens, 8192) : req.MaxTokens;
        if (shape.Thinking)
        {
            // DeepSeek switches thinking with its own field; OpenAI, Ollama and LiteLLM take reasoning_effort.
            if (Id == "deepseek") body["thinking"] = new { type = "enabled" };
            else body["reasoning_effort"] = req.Effort is "medium" or "high" ? req.Effort : "low";
        }
        return JsonSerializer.Serialize(body);
    }

    /// <summary>OpenAI's reasoning models (o1, o3, o4-mini, gpt-5...) refuse max_tokens and want max_completion_tokens.</summary>
    private bool UsesCompletionTokens(string model)
    {
        if (Id != "openai") return false;
        var m = model[(model.LastIndexOf('/') + 1)..]; // LiteLLM style "openai/gpt-5"
        return Regex.IsMatch(m, @"^(o\d|gpt-5)", RegexOptions.IgnoreCase);
    }

    private static BodyShape? Reshape(BodyShape shape, string error)
    {
        bool Says(string s) => error.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (!shape.CompletionTokens && Says("max_completion_tokens")) return shape with { CompletionTokens = true };
        if (shape.Thinking && (Says("reasoning_effort") || Says("thinking"))) return shape with { Thinking = false };
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ErrorText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "未知错误",
        JsonValueKind.Object => Str(e, "message") ?? e.GetRawText(),
        _ => e.GetRawText(),
    };

    /// <summary>
    /// Some models (DeepSeek-R1 / QwQ on older Ollama, many relays) put their reasoning at the start of the answer as
    /// &lt;think&gt;…&lt;/think&gt;. Split it off as thinking, holding back a few characters while a tag may be cut between chunks.
    /// </summary>
    private sealed class ThinkTagSplitter
    {
        private const string Open = "<think>", Close = "</think>";
        private enum State { Start, Thinking, Answer }
        private State _state;
        private string _buf = "";

        public IEnumerable<LlmChunk> Feed(string s)
        {
            if (_state == State.Answer) { yield return new LlmChunk(s); yield break; }
            _buf += s;
            if (_state == State.Start)
            {
                var t = _buf.TrimStart();
                if (t.StartsWith(Open, StringComparison.Ordinal)) { _state = State.Thinking; _buf = t[Open.Length..]; }
                else if (Open.StartsWith(t, StringComparison.Ordinal)) yield break; // "", "<", "<thi"…: wait for more
                else { _state = State.Answer; yield return new LlmChunk(_buf); _buf = ""; yield break; }
            }
            int end = _buf.IndexOf(Close, StringComparison.Ordinal);
            if (end >= 0)
            {
                if (end > 0) yield return new LlmChunk(_buf[..end], true);
                var rest = _buf[(end + Close.Length)..].TrimStart('\r', '\n');
                _state = State.Answer;
                _buf = "";
                if (rest.Length > 0) yield return new LlmChunk(rest);
                yield break;
            }
            int keep = Math.Min(_buf.Length, Close.Length - 1);
            while (keep > 0 && !Close.StartsWith(_buf[^keep..], StringComparison.Ordinal)) keep--;
            if (_buf.Length > keep) { yield return new LlmChunk(_buf[..^keep], true); _buf = _buf[^keep..]; }
        }

        public IEnumerable<LlmChunk> Flush()
        {
            if (_buf.Length > 0) yield return new LlmChunk(_buf, _state == State.Thinking);
            _buf = "";
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
