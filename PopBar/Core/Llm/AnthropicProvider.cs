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
    public LlmFeatures Features => LlmFeatures.Streaming | LlmFeatures.SystemPrompt | LlmFeatures.Effort | LlmFeatures.ModelList;
    public string? DefaultBaseUrl => null;
    public string? DefaultModel => "claude-opus-5-5";
    public bool RequiresApiKey => true;
    public string? Hint => "接口地址留空即 api.anthropic.com；也可填兼容 Anthropic 协议的中转地址";

    private static AnthropicClient Client(LlmConnection c) => string.IsNullOrWhiteSpace(c.BaseUrl)
        ? new AnthropicClient { ApiKey = string.IsNullOrWhiteSpace(c.ApiKey) ? null : c.ApiKey }
        : new AnthropicClient { ApiKey = string.IsNullOrWhiteSpace(c.ApiKey) ? null : c.ApiKey, BaseUrl = c.BaseUrl.TrimEnd('/') };

    public async IAsyncEnumerable<string> StreamAsync(LlmConnection c, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var p = new MessageCreateParams
        {
            Model = c.Model,
            MaxTokens = req.MaxTokens,
            Messages = [new() { Role = Role.User, Content = req.User }],
            OutputConfig = new OutputConfig { Effort = ParseEffort(req.Effort) },
            System = string.IsNullOrWhiteSpace(req.System)
                ? null
                : (MessageCreateParamsSystem?)new List<TextBlockParam> { new() { Text = req.System } },
        };
        await foreach (var ev in Client(c).Messages.CreateStreaming(p, cancellationToken: ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text) && text.Text.Length > 0)
                yield return text.Text;
        }
    }

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
