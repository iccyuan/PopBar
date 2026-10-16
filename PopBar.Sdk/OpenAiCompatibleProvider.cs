using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PopBar.Sdk;

/// <summary>Request fields of the chat-completions protocol that services spell differently.</summary>
public static class ChatCompletionsFields
{
    public const string MaxTokens = "max_tokens";
    /// <summary>OpenAI's replacement for <see cref="MaxTokens"/>, required by its reasoning models.</summary>
    public const string MaxCompletionTokens = "max_completion_tokens";
    public const string ReasoningEffort = "reasoning_effort";
}

/// <summary>
/// Where services speaking the OpenAI chat-completions protocol differ from one another. The protocol itself (request,
/// SSE parsing, in-stream errors, &lt;think&gt; tags, retries) lives in <see cref="OpenAiCompatibleProvider"/>; a dialect
/// only states the deviations. Derive from <see cref="ChatCompletionsDialect"/> and override what your service does
/// differently.
/// </summary>
public interface IChatCompletionsDialect
{
    /// <summary>Name of the output-limit field for <paramref name="model"/>: <see cref="ChatCompletionsFields.MaxTokens"/> or <see cref="ChatCompletionsFields.MaxCompletionTokens"/>.</summary>
    string TokenLimitField(string model);

    /// <summary>
    /// Add the thinking / reasoning-effort fields for <paramref name="request"/> (see <see cref="LlmRequest.Thinking"/>
    /// and <see cref="LlmRequest.Effort"/>). Add nothing when the service needs no field for this request. If the server
    /// rejects a field named here, the request is repeated once without calling this.
    /// </summary>
    void AddReasoning(IDictionary<string, object> body, LlmRequest request);

    /// <summary>Reasoning text carried by one streamed <c>choices[0].delta</c>, or null.</summary>
    string? ReadReasoning(JsonElement delta);

    /// <summary>The reasoning_effort values the service takes for <paramref name="model"/>, ascending (see <see cref="ILlmProvider.EffortLevels"/>). Default low / medium / high.</summary>
    IReadOnlyList<string> EffortLevels(string model) => LlmEffort.Standard;
}

/// <summary>
/// Plain chat-completions behaviour: max_tokens, reasoning_effort only when thinking is asked for (servers without
/// reasoning models reject the field), reasoning read from <c>reasoning_content</c> or <c>reasoning</c>.
/// </summary>
public class ChatCompletionsDialect : IChatCompletionsDialect
{
    public virtual string TokenLimitField(string model) => ChatCompletionsFields.MaxTokens;

    public virtual void AddReasoning(IDictionary<string, object> body, LlmRequest request)
    {
        if (request.ThinkingOn) body[ChatCompletionsFields.ReasoningEffort] = Effort(request);
    }

    /// <summary>DeepSeek / vLLM / SiliconFlow call it reasoning_content, Ollama / OpenRouter reasoning.</summary>
    public virtual string? ReadReasoning(JsonElement delta) => Str(delta, "reasoning_content") ?? Str(delta, "reasoning");

    public virtual IReadOnlyList<string> EffortLevels(string model) => LlmEffort.Standard;

    /// <summary><see cref="LlmRequest.Effort"/> snapped to the standard low / medium / high (max becomes high). Requests normally arrive already snapped to <see cref="EffortLevels"/>.</summary>
    protected virtual string Effort(LlmRequest request) => LlmEffort.Nearest(request.EffortLevel, LlmEffort.Standard);

    protected static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Identity and defaults of an OpenAI-compatible service, as shown in the settings provider list.</summary>
public sealed record OpenAiCompatibleOptions
{
    /// <summary>Stable id stored in config (lowercase).</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Endpoint used when the user leaves the address empty, up to /v1.</summary>
    public string? DefaultBaseUrl { get; init; }
    public string? DefaultModel { get; init; }
    public bool RequiresApiKey { get; init; }
    public string? Hint { get; init; }
    /// <summary>See <see cref="ILlmProvider.Logo"/>: icon name or SVG path data; null = generic.</summary>
    public string? Logo { get; init; }
}

/// <summary>
/// An <see cref="ILlmProvider"/> for any server speaking the OpenAI <c>/chat/completions</c> protocol. Register one per
/// service with <see cref="ILlmService.RegisterProvider"/>, passing a dialect for what that service does its own way:
/// <code>host.Llm.RegisterProvider(new OpenAiCompatibleProvider(new() { Id = "kimi", Name = "Kimi", DefaultBaseUrl = "https://api.moonshot.cn/v1", RequiresApiKey = true }));</code>
/// </summary>
public class OpenAiCompatibleProvider : ILlmProvider
{
    private readonly OpenAiCompatibleOptions _options;
    private readonly IChatCompletionsDialect _dialect;

    public OpenAiCompatibleProvider(OpenAiCompatibleOptions options, IChatCompletionsDialect? dialect = null)
    {
        _options = options;
        _dialect = dialect ?? new ChatCompletionsDialect();
    }

    public string Id => _options.Id;
    public string Name => _options.Name;
    public string? Logo => _options.Logo;
    public string? DefaultBaseUrl => _options.DefaultBaseUrl;
    public string? DefaultModel => _options.DefaultModel;
    public bool RequiresApiKey => _options.RequiresApiKey;
    public string? Hint => _options.Hint;
    // Effort applies together with thinking here, through the dialect (reasoning_effort).
    public virtual LlmFeatures Features => LlmFeatures.Streaming | LlmFeatures.SystemPrompt | LlmFeatures.ModelList | LlmFeatures.Thinking | LlmFeatures.Effort;
    public virtual IReadOnlyList<string> EffortLevels(string model) => _dialect.EffortLevels(model);

    public async IAsyncEnumerable<string> StreamAsync(LlmConnection c, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in StreamChunksAsync(c, req, ct))
            if (!chunk.IsThinking) yield return chunk.Text;
    }

    public async IAsyncEnumerable<LlmChunk> StreamChunksAsync(LlmConnection c, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var limitField = _dialect.TokenLimitField(c.Model);
        bool reasoning = true;
        HttpResponseMessage resp;
        while (true)
        {
            var body = Body(c, req, limitField, reasoning);
            resp = await SendAsync(c, () => new HttpRequestMessage(HttpMethod.Post, Root(c) + "/chat/completions") { Content = new StringContent(body, Encoding.UTF8, "application/json") },
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode) break;
            var err = await resp.Content.ReadAsStringAsync(ct);
            int status = (int)resp.StatusCode;
            var reason = resp.ReasonPhrase;
            resp.Dispose();
            // A 400 about one of the optional fields: reshape the request and ask again (each change applies once).
            if (status == 400)
            {
                bool Says(string s) => err.Contains(s, StringComparison.OrdinalIgnoreCase);
                if (limitField == ChatCompletionsFields.MaxTokens && Says(ChatCompletionsFields.MaxCompletionTokens)) { limitField = ChatCompletionsFields.MaxCompletionTokens; continue; }
                if (reasoning && (Says("reasoning") || Says("thinking"))) { reasoning = false; continue; }
            }
            throw new HttpRequestException($"{status} {reason}: {Truncate(err, 300)}");
        }

        using (resp)
        {
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var tags = new ThinkTagSplitter();
            // Not `while (!reader.EndOfStream)`: EndOfStream is a blocking read, and this loop runs on the UI thread,
            // so the panel could not repaint between chunks and the whole answer appeared at once.
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                string? content = null, thinking = null, error = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    // Relays and Ollama report failures inside a 200 stream; without this the panel just stays empty.
                    if (root.TryGetProperty("error", out var e)) error = ErrorText(e);
                    else if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                             && choices[0].TryGetProperty("delta", out var d))
                    {
                        content = d.TryGetProperty("content", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        thinking = _dialect.ReadReasoning(d);
                    }
                }
                catch (JsonException) { /* keep-alive or partial line */ }
                if (error != null) throw new HttpRequestException(error);
                if (!string.IsNullOrEmpty(thinking)) yield return new LlmChunk(thinking, true);
                if (!string.IsNullOrEmpty(content))
                    foreach (var chunk in tags.Feed(content)) yield return chunk;
            }
            foreach (var chunk in tags.Flush()) yield return chunk;
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

    private string Body(LlmConnection c, LlmRequest req, string limitField, bool reasoning)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(req.System)) messages.Add(new { role = "system", content = req.System });
        messages.Add(new { role = "user", content = req.User });
        var body = new Dictionary<string, object> { ["model"] = c.Model, ["stream"] = true, ["messages"] = messages };
        body[limitField] = req.EffectiveMaxTokens;
        if (reasoning) _dialect.AddReasoning(body, req);
        return JsonSerializer.Serialize(body);
    }

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
            catch (HttpRequestException ex) when (attempt == 0 && ex.StatusCode == null && ex.InnerException is IOException or System.Net.Sockets.SocketException)
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

    private static string ErrorText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "未知错误",
        JsonValueKind.Object when e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString()!,
        _ => e.GetRawText(),
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

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
}
