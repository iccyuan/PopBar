using System.Text.RegularExpressions;
using PopBar.Sdk;

namespace PopBar.Core.Llm;

/// <summary>Ids of the built-in providers, as stored in <c>config.json</c> (<see cref="Config.LlmConfig.Protocol"/>).</summary>
internal static class LlmProviderIds
{
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string Ollama = "ollama";
    public const string DeepSeek = "deepseek";
}

/// <summary>The OpenAI-compatible services PopBar ships with; each is the shared protocol plus its dialect.</summary>
internal static class BuiltinProviders
{
    public static IEnumerable<ILlmProvider> Create() =>
    [
        new AnthropicProvider(),
        new OpenAiCompatibleProvider(new()
        {
            Id = LlmProviderIds.OpenAi, Name = "OpenAI 兼容",
            Hint = "填到 /v1 为止，如 http://localhost:4000/v1（LiteLLM）或 https://api.openai.com/v1",
        }, new OpenAiDialect()),
        new OpenAiCompatibleProvider(new()
        {
            Id = LlmProviderIds.Ollama, Name = "Ollama（本地）", DefaultBaseUrl = "http://localhost:11434/v1", DefaultModel = "qwen2.5",
            Hint = "本机 Ollama，无需密钥；模型名用「获取模型」拉取",
        }, new OllamaDialect()),
        new OpenAiCompatibleProvider(new()
        {
            Id = LlmProviderIds.DeepSeek, Name = "DeepSeek", DefaultBaseUrl = "https://api.deepseek.com/v1", DefaultModel = "deepseek-flash",
            RequiresApiKey = true, Hint = "需要 DeepSeek API Key",
        }, new DeepSeekDialect()),
    ];
}

/// <summary>
/// OpenAI itself (and LiteLLM / vLLM in front of anything): max_tokens is deprecated and refused by the reasoning models
/// (o-series, gpt-5), which want max_completion_tokens. Other servers mostly know only max_tokens, so switch by model name.
/// </summary>
internal sealed class OpenAiDialect : ChatCompletionsDialect
{
    private static readonly Regex ReasoningModel = new(@"^(o\d|gpt-5)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override string TokenLimitField(string model)
    {
        var name = model[(model.LastIndexOf('/') + 1)..]; // LiteLLM style "openai/gpt-5"
        return ReasoningModel.IsMatch(name) ? ChatCompletionsFields.MaxCompletionTokens : ChatCompletionsFields.MaxTokens;
    }
}

/// <summary>
/// docs.ollama.com/api/openai-compatibility: reasoning_effort drives thinking models and "none" turns thinking off, so
/// the field is always sent. Models without thinking that refuse it are retried without.
/// </summary>
internal sealed class OllamaDialect : ChatCompletionsDialect
{
    private const string Off = "none";

    public override void AddReasoning(IDictionary<string, object> body, LlmRequest request) =>
        body[ChatCompletionsFields.ReasoningEffort] = request.ThinkingOn ? Effort(request) : Off;
}

/// <summary>
/// api-docs.deepseek.com/guides/thinking_mode: thinking is on by default and switched by its own "thinking" field, so
/// "off" has to be sent; reasoning_effort takes low / high / max (medium is mapped to high by the server).
/// </summary>
internal sealed class DeepSeekDialect : ChatCompletionsDialect
{
    private const string Field = "thinking", Enabled = "enabled", Disabled = "disabled";

    public override void AddReasoning(IDictionary<string, object> body, LlmRequest request)
    {
        body[Field] = new { type = request.ThinkingOn ? Enabled : Disabled };
        if (request.ThinkingOn) body[ChatCompletionsFields.ReasoningEffort] = Effort(request);
    }

    protected override string Effort(LlmRequest request) => request.EffortLevel == LlmEffort.Max ? LlmEffort.Max : base.Effort(request);
}
