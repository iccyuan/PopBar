using System.Net.Http;

namespace PopBar.Sdk;

/// <summary>
/// Streaming access to the model service configured in PopBar settings, backed by a pluggable <see cref="ILlmProvider"/>.
/// The model there is only the default: a plugin may name another one per request via <see cref="LlmRequest.Model"/>
/// (same provider, endpoint and key).
/// </summary>
public interface ILlmService
{
    bool IsConfigured { get; }
    /// <summary>The default model from settings, for status lines, e.g. "claude-opus-5-5".</summary>
    string ModelName { get; }
    /// <summary>What a request with this <see cref="LlmRequest.Model"/> override will run on: the override, or the default when empty.</summary>
    string ModelNameFor(string? model) => string.IsNullOrWhiteSpace(model) ? ModelName : model.Trim();
    /// <summary>Id of the provider currently selected in settings.</summary>
    string ProviderId { get; }

    /// <summary>All registered providers (built-in + plugin-registered), in registration order.</summary>
    IReadOnlyList<ILlmProvider> Providers { get; }
    /// <summary>Register a provider so it appears in the settings "协议" list. Call from <c>IPopBarPlugin.Initialize</c>.</summary>
    void RegisterProvider(ILlmProvider provider);

    /// <summary>Stream the assistant reply as text chunks.</summary>
    IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>Model ids the configured backend offers.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Convenience: collect the whole reply.</summary>
    async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in StreamAsync(request, ct)) sb.Append(chunk);
        return sb.ToString();
    }
}

/// <summary>
/// One model backend (Anthropic, an OpenAI-compatible server, Gemini, ...). Implement this in a plugin and call
/// <see cref="ILlmService.RegisterProvider"/> to add a new backend without touching PopBar itself.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Stable id stored in config (lowercase, e.g. "anthropic", "openai", "gemini").</summary>
    string Id { get; }
    /// <summary>Shown in the settings list.</summary>
    string Name { get; }
    /// <summary>Used when the user leaves the base URL empty; null if the provider has no fixed endpoint.</summary>
    string? DefaultBaseUrl { get; }
    /// <summary>Suggested model when the model field is empty.</summary>
    string? DefaultModel { get; }
    bool RequiresApiKey { get; }
    /// <summary>What this backend supports; shown on the settings "服务" page. Defaults to streaming + system prompt.</summary>
    LlmFeatures Features => LlmFeatures.Streaming | LlmFeatures.SystemPrompt;
    /// <summary>One-line help shown next to the provider.</summary>
    string? Hint { get; }

    IAsyncEnumerable<string> StreamAsync(LlmConnection connection, LlmRequest request, CancellationToken ct);
    Task<IReadOnlyList<string>> ListModelsAsync(LlmConnection connection, CancellationToken ct);
}

/// <summary>Capabilities a model backend declares.</summary>
[Flags]
public enum LlmFeatures
{
    None = 0,
    /// <summary>Replies arrive incrementally.</summary>
    Streaming = 1,
    /// <summary>Honours <see cref="LlmRequest.System"/>.</summary>
    SystemPrompt = 2,
    /// <summary>Honours <see cref="LlmRequest.Effort"/> (reasoning depth).</summary>
    Effort = 4,
    /// <summary>Implements <see cref="ILlmProvider.ListModelsAsync"/>.</summary>
    ModelList = 8,
}

/// <summary>Resolved connection settings handed to a provider (base URL already defaulted).</summary>
public sealed record LlmConnection(string BaseUrl, string ApiKey, string Model, HttpClient Http);

public sealed class LlmRequest
{
    public string? System { get; init; }
    public required string User { get; init; }
    /// <summary>Model id to use instead of the default from settings; null / empty keeps the default.</summary>
    public string? Model { get; init; }
    public int MaxTokens { get; init; } = 4096;
    /// <summary>"low" | "medium" | "high" - reasoning effort where the backend supports it.</summary>
    public string Effort { get; init; } = "low";
}
