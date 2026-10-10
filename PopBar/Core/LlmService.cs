using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using PopBar.Config;
using PopBar.Sdk;

namespace PopBar.Core;

/// <summary>
/// The model backend configured in settings. Two protocols:
///  - "anthropic": official Anthropic SDK (Messages API, streaming).
///  - "openai": any OpenAI-compatible /chat/completions endpoint (LiteLLM, Ollama, DeepSeek, ...), raw SSE.
/// </summary>
internal sealed class LlmService : ILlmService
{
    private readonly ConfigStore _store;
    private readonly HttpClient _http;

    public LlmService(ConfigStore store, HttpClient http)
    {
        _store = store;
        _http = http;
    }

    private LlmConfig Cfg => _store.Current.Llm;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Cfg.Model) &&
        (Cfg.Protocol == "openai" ? !string.IsNullOrWhiteSpace(Cfg.BaseUrl) : !string.IsNullOrWhiteSpace(Cfg.ApiKey) || !string.IsNullOrWhiteSpace(Cfg.BaseUrl));

    public string ModelName => Cfg.Model;

    public IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken ct = default)
    {
        var cfg = Cfg;
        if (!IsConfigured) throw new InvalidOperationException("尚未配置 AI 模型，请在 设置 → 插件 → AI 模型服务 中填写。");
        return string.Equals(cfg.Protocol, "openai", StringComparison.OrdinalIgnoreCase)
            ? StreamOpenAi(cfg, request, ct)
            : StreamAnthropic(cfg, request, ct);
    }

    // ------------------------------------------------------------------ Anthropic (official SDK)

    private static async IAsyncEnumerable<string> StreamAnthropic(LlmConfig cfg, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var client = string.IsNullOrWhiteSpace(cfg.BaseUrl)
            ? new AnthropicClient { ApiKey = string.IsNullOrWhiteSpace(cfg.ApiKey) ? null : cfg.ApiKey }
            : new AnthropicClient { ApiKey = string.IsNullOrWhiteSpace(cfg.ApiKey) ? null : cfg.ApiKey, BaseUrl = cfg.BaseUrl.TrimEnd('/') };

        var p = new MessageCreateParams
        {
            Model = cfg.Model,
            MaxTokens = req.MaxTokens,
            Messages = [new() { Role = Role.User, Content = req.User }],
            OutputConfig = new OutputConfig { Effort = ParseEffort(req.Effort) },
            System = string.IsNullOrWhiteSpace(req.System)
                ? null
                : (MessageCreateParamsSystem?)new List<TextBlockParam> { new() { Text = req.System } },
        };

        await foreach (var ev in client.Messages.CreateStreaming(p, cancellationToken: ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text) && text.Text.Length > 0)
                yield return text.Text;
        }
    }

    private static Effort ParseEffort(string e) => e.ToLowerInvariant() switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Low,
    };

    // ------------------------------------------------------------------ OpenAI-compatible (raw SSE)

    private async IAsyncEnumerable<string> StreamOpenAi(LlmConfig cfg, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = cfg.BaseUrl.TrimEnd('/');
        if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) url += "/chat/completions";

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(req.System)) messages.Add(new { role = "system", content = req.System });
        messages.Add(new { role = "user", content = req.User });
        var body = JsonSerializer.Serialize(new { model = cfg.Model, max_tokens = req.MaxTokens, stream = true, messages });

        using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
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

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
