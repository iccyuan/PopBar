using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;
using PopBar.Sdk;

namespace PopBar.Core.Llm;

/// <summary>Anthropic Messages API through the official SDK (streaming, effort, system prompt).</summary>
internal sealed class AnthropicProvider : ILlmProvider
{
    public string Id => LlmProviderIds.Anthropic;
    public string Name => "Anthropic（官方 SDK）";
    public LlmFeatures Features => LlmFeatures.Streaming | LlmFeatures.SystemPrompt | LlmFeatures.Effort | LlmFeatures.ModelList | LlmFeatures.Thinking;
    public string? DefaultBaseUrl => null;
    public string? DefaultModel => "claude-opus-5-5";
    public bool RequiresApiKey => true;
    public bool RequiresBaseUrl => false; // the SDK knows api.anthropic.com
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
        // Which thinking setting a model takes differs per model (see the per-model table under
        // platform.claude.com/docs/en/build-with-claude/thinking), and relays may not know effort at all. Start from the
        // best fit, step down on a 400 that names the field, and remember what worked for the next request.
        var key = (c.BaseUrl, c.Model, req.Thinking);
        var shape = Shapes.TryGetValue(key, out var known) ? known : new Shape(0, true);
        while (true)
        {
            await using var e = Client(c).Messages.CreateStreaming(Params(c, req, shape), cancellationToken: ct).GetAsyncEnumerator(ct);
            Shape? next = null;
            try { if (!await e.MoveNextAsync()) yield break; }
            catch (AnthropicBadRequestException ex) when ((next = StepDown(shape, req.Thinking, ex.ResponseBody)) != null) { }
            if (next != null) { shape = next; continue; }
            Shapes[key] = shape;
            do
            {
                if (!e.Current.TryPickContentBlockDelta(out var delta)) continue;
                // With display "omitted" thinking deltas arrive empty: nothing to show.
                if (delta.Delta.TryPickText(out var text) && text.Text.Length > 0) yield return new LlmChunk(text.Text);
                else if (delta.Delta.TryPickThinking(out var th) && th.Thinking.Length > 0) yield return new LlmChunk(th.Thinking, true);
            } while (await e.MoveNextAsync());
            yield break;
        }
    }

    /// <summary>Index into <see cref="ThinkingOn"/> / <see cref="ThinkingOff"/>, and whether output_config.effort is sent.</summary>
    private sealed record Shape(int Step, bool Effort);
    private static readonly ConcurrentDictionary<(string, string, bool), Shape> Shapes = new();

    /// <summary>Budget for models with only manual extended thinking (minimum 1024, below the raised max_tokens).</summary>
    private const int ManualThinkingBudget = LlmRequest.MinMaxTokensWithThinking / 2;

    // On: adaptive with the summary shown (display defaults to "omitted" on the 4.7+ / 5.x models); the 4.5 models only
    // know manual extended thinking; last resort sends nothing.
    private static readonly Func<ThinkingConfigParam?>[] ThinkingOn =
    [
        () => new ThinkingConfigAdaptive { Display = Display.Summarized },
        () => new ThinkingConfigEnabled { BudgetTokens = ManualThinkingBudget },
        () => null,
    ];
    // Off: "disabled" where allowed; Sonnet 5.5 rejects it and takes "between_tools"; Opus 5.5, Fable and Mythos cannot
    // turn thinking off at all, so nothing is sent and the hidden thinking is skipped.
    private static readonly Func<ThinkingConfigParam?>[] ThinkingOff =
    [
        () => new ThinkingConfigDisabled(),
        () => new ThinkingConfigBetweenTools(),
        () => null,
    ];

    private static MessageCreateParams Params(LlmConnection c, LlmRequest req, Shape shape) => new()
    {
        Model = c.Model,
        MaxTokens = req.EffectiveMaxTokens,
        Messages = [new() { Role = Role.User, Content = req.User }],
        OutputConfig = shape.Effort ? new OutputConfig { Effort = ParseEffort(req.Effort) } : null,
        Thinking = (req.Thinking ? ThinkingOn : ThinkingOff)[shape.Step](),
        System = string.IsNullOrWhiteSpace(req.System)
            ? null
            : (MessageCreateParamsSystem?)new List<TextBlockParam> { new() { Text = req.System } },
    };

    /// <summary>The next thing to try after a 400 naming effort or thinking; null when the error is about something else.</summary>
    private static Shape? StepDown(Shape shape, bool thinking, string body)
    {
        bool Says(string s) => body.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (shape.Effort && (Says("effort") || Says("output_config"))) return shape with { Effort = false };
        int last = (thinking ? ThinkingOn : ThinkingOff).Length - 1;
        if (shape.Step < last && (Says("thinking") || Says("adaptive") || Says("disabled") || Says("between_tools") || Says("budget_tokens")))
            return shape with { Step = shape.Step + 1 };
        return null;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(LlmConnection c, CancellationToken ct)
    {
        var page = await Client(c).Models.List(new ModelListParams { Limit = 1000 }, cancellationToken: ct);
        return page.Items.Select(m => m.ID.ToString()).ToList();
    }

    private static Effort ParseEffort(string e) => e.ToLowerInvariant() switch
    {
        LlmEffort.Medium => Effort.Medium,
        LlmEffort.High => Effort.High,
        LlmEffort.Max => Effort.Max,
        _ => Effort.Low,
    };
}
