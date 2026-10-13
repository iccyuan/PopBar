using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;
using PopBar.Sdk;

namespace PopBar.Core.Llm;

/// <summary>Anthropic Messages API through the official SDK (streaming, effort, system prompt).</summary>
internal sealed class AnthropicProvider : ILlmProvider
{
    public string Id => "anthropic";
    public string Name => "Anthropic（官方 SDK）";
    public LlmFeatures Features => LlmFeatures.Streaming | LlmFeatures.SystemPrompt | LlmFeatures.Effort | LlmFeatures.ModelList | LlmFeatures.Thinking;
    public string? DefaultBaseUrl => null;
    public string? DefaultModel => "claude-opus-5-5";
    public bool RequiresApiKey => true;
    public string? Hint => "接口地址留空即 api.anthropic.com；也可填兼容 Anthropic 协议的中转地址";

    private static AnthropicClient Client(LlmConnection c) => string.IsNullOrWhiteSpace(c.BaseUrl)
        // Share PopBar's HttpClient: its connect logic (IPv4 sockets for IPv4 hosts) and system proxy apply here too.
        ? new AnthropicClient { ApiKey = string.IsNullOrWhiteSpace(c.ApiKey) ? null : c.ApiKey, HttpClient = c.Http }
        : new AnthropicClient { ApiKey = string.IsNullOrWhiteSpace(c.ApiKey) ? null : c.ApiKey, BaseUrl = c.BaseUrl.TrimEnd('/'), HttpClient = c.Http };

    public async IAsyncEnumerable<string> StreamAsync(LlmConnection c, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in StreamChunksAsync(c, req, ct))
            if (!chunk.IsThinking) yield return chunk.Text;
    }

    public async IAsyncEnumerable<LlmChunk> StreamChunksAsync(LlmConnection c, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        // Older models and Anthropic-compatible relays may reject effort / thinking with a 400. Nothing has been shown
        // yet at that point, so drop those fields and ask again rather than fail.
        for (bool extras = true; ; extras = false)
        {
            await using var e = Client(c).Messages.CreateStreaming(Params(c, req, extras), cancellationToken: ct).GetAsyncEnumerator(ct);
            bool any = false;
            while (true)
            {
                LlmChunk? chunk = null;
                try
                {
                    if (!await e.MoveNextAsync()) break;
                }
                catch (Exception ex) when (extras && !any && RejectsExtras(ex)) { goto retry; }
                if (e.Current.TryPickContentBlockDelta(out var delta))
                {
                    if (delta.Delta.TryPickText(out var text) && text.Text.Length > 0) chunk = new LlmChunk(text.Text);
                    else if (delta.Delta.TryPickThinking(out var th) && th.Thinking.Length > 0) chunk = new LlmChunk(th.Thinking, true);
                }
                if (chunk is { } ch) { any = true; yield return ch; }
            }
            yield break;
        retry:;
        }
    }

    private static MessageCreateParams Params(LlmConnection c, LlmRequest req, bool extras) => new()
    {
        Model = c.Model,
        // Thinking tokens count towards max_tokens: leave room for an answer after it.
        MaxTokens = extras && req.Thinking ? Math.Max(req.MaxTokens, 8192) : req.MaxTokens,
        Messages = [new() { Role = Role.User, Content = req.User }],
        OutputConfig = extras ? new OutputConfig { Effort = ParseEffort(req.Effort) } : null,
        Thinking = extras && req.Thinking ? new ThinkingConfigAdaptive { Display = Display.Summarized } : null,
        System = string.IsNullOrWhiteSpace(req.System)
            ? null
            : (MessageCreateParamsSystem?)new List<TextBlockParam> { new() { Text = req.System } },
    };

    /// <summary>A request error naming one of the optional fields (the SDK's message carries the API's error text).</summary>
    private static bool RejectsExtras(Exception ex) =>
        new[] { "effort", "output_config", "thinking", "adaptive" }.Any(k => ex.Message.Contains(k, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<string>> ListModelsAsync(LlmConnection c, CancellationToken ct)
    {
        var page = await Client(c).Models.List(new ModelListParams { Limit = 1000 }, cancellationToken: ct);
        return page.Items.Select(m => m.ID.ToString()).ToList();
    }

    private static Effort ParseEffort(string e) => e.ToLowerInvariant() switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Low,
    };
}
