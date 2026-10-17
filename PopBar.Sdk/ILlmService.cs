using System.Net.Http;

namespace PopBar.Sdk;

/// <summary>
/// Streaming access to the model services configured on the settings "服务" page, backed by pluggable
/// <see cref="ILlmProvider"/>s. The user configures any number of services (one per provider: Anthropic, DeepSeek,
/// a local Ollama...) and marks one as the default. A request goes to the default unless it names another configured
/// service via <see cref="LlmRequest.Provider"/>; the model there is only that service's default, and a plugin may
/// name another one per request via <see cref="LlmRequest.Model"/>.
/// </summary>
public interface ILlmService
{
    /// <summary>Whether the default service is ready to use.</summary>
    bool IsConfigured { get; }
    /// <summary>Whether the service for this provider id is configured (null / empty = the default service).</summary>
    bool IsConfiguredFor(string? providerId);
    /// <summary>The default service's model, for status lines, e.g. "claude-opus-5-5".</summary>
    string ModelName { get; }
    /// <summary>What a request with this <see cref="LlmRequest.Model"/> override will run on at the default service.</summary>
    string ModelNameFor(string? model) => ModelNameFor(null, model);
    /// <summary>What a request with these <see cref="LlmRequest.Provider"/> / <see cref="LlmRequest.Model"/> overrides will run on.</summary>
    string ModelNameFor(string? providerId, string? model);
    /// <summary>Id of the provider behind the default service.</summary>
    string ProviderId { get; }
    /// <summary>Display name of the provider behind a service (null / empty = the default service), or the id when unknown.</summary>
    string ProviderNameFor(string? providerId);
    /// <summary>Provider ids of the services the user has set up on the "服务" page, the default first. Offer these as <see cref="LlmRequest.Provider"/> choices.</summary>
    IReadOnlyList<string> ConfiguredProviderIds { get; }

    /// <summary>All registered providers (built-in + plugin-registered), in registration order.</summary>
    IReadOnlyList<ILlmProvider> Providers { get; }
    /// <summary>Register a provider so it appears in the settings "服务商" list. Call from <c>IPopBarPlugin.Initialize</c>.</summary>
    void RegisterProvider(ILlmProvider provider);

    /// <summary>
    /// The <see cref="LlmEffort"/> levels a service accepts for a model (null = that service's default model), in
    /// ascending order; empty when it has no adjustable depth. Build depth pickers from this instead of a fixed list.
    /// </summary>
    IReadOnlyList<string> EffortLevels(string? providerId, string? model);

    /// <summary>
    /// What a service can do with a model (null = that service's default model): the backend's features plus the
    /// model's own (<see cref="LlmFeatures.Vision"/>, <see cref="LlmFeatures.Speech"/>) as OpenRouter's model catalog
    /// (cached locally) lists them; a model it does not list adds nothing (<see cref="LlmFeatureSource.Unknown"/>). No network.
    /// </summary>
    LlmCapabilities CapabilitiesOf(string? providerId, string? model);
    /// <summary><see cref="CapabilitiesOf"/> after making sure the OpenRouter catalog is loaded (fetched if missing or a day old).</summary>
    Task<LlmCapabilities> RefreshCapabilitiesAsync(string? providerId, string? model, CancellationToken ct = default);

    /// <summary>Stream the assistant reply as text chunks (the answer only; thinking is dropped).</summary>
    IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>Stream the reply with thinking and answer told apart (<see cref="LlmChunk.IsThinking"/>).</summary>
    IAsyncEnumerable<LlmChunk> StreamChunksAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>
    /// Model ids a service returned the last time anyone fetched its list (kept in config), empty if never. Fill a picker
    /// from this without a network round trip; <see cref="ListModelsAsync"/> refreshes it.
    /// </summary>
    IReadOnlyList<string> KnownModels(string? providerId = null);
    /// <summary>Model ids a configured service offers (null / empty provider id = the default service); a success also refreshes <see cref="KnownModels"/>.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(string? providerId = null, CancellationToken ct = default);

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
    /// <summary>False when the provider reaches a default host by itself, so an empty address and no <see cref="DefaultBaseUrl"/> is fine.</summary>
    bool RequiresBaseUrl => DefaultBaseUrl == null;
    /// <summary>What this backend supports; shown on the settings "服务" page. Defaults to streaming + system prompt.</summary>
    LlmFeatures Features => LlmFeatures.Streaming | LlmFeatures.SystemPrompt;
    /// <summary>
    /// The <see cref="LlmEffort"/> levels <paramref name="model"/> accepts, ascending. Default: low / medium / high when
    /// <see cref="LlmFeatures.Effort"/> is declared, else none. Override when the backend takes other levels (DeepSeek:
    /// low / high / max) or they differ per model. Requests arrive with the effort already snapped to one of these.
    /// </summary>
    IReadOnlyList<string> EffortLevels(string model) => Features.HasFlag(LlmFeatures.Effort) ? LlmEffort.Standard : Array.Empty<string>();
    /// <summary>
    /// Logo drawn in front of <see cref="Name"/> in the provider pickers: a name from PopBar's icon set (built-in brand logos
    /// "openai", "anthropic", "claude", "deepseek", "ollama", or any Lucide icon such as "bot"), or raw SVG path data in a
    /// 24x24 viewbox (filled, single colour). Null = a generic robot.
    /// </summary>
    string? Logo => null;
    /// <summary>One-line help shown next to the provider.</summary>
    string? Hint { get; }

    IAsyncEnumerable<string> StreamAsync(LlmConnection connection, LlmRequest request, CancellationToken ct);
    /// <summary>
    /// Thinking and answer as separate chunks. Override when the backend streams its reasoning (and declare
    /// <see cref="LlmFeatures.Thinking"/>); the default treats everything from <see cref="StreamAsync"/> as answer.
    /// </summary>
    async IAsyncEnumerable<LlmChunk> StreamChunksAsync(LlmConnection connection, LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var s in StreamAsync(connection, request, ct)) yield return new LlmChunk(s);
    }
    Task<IReadOnlyList<string>> ListModelsAsync(LlmConnection connection, CancellationToken ct);
}

/// <summary>A piece of the reply: answer text, or (with <see cref="IsThinking"/>) the model's reasoning before it.</summary>
public readonly record struct LlmChunk(string Text, bool IsThinking = false);

/// <summary>Capabilities a model backend declares.</summary>
[Flags]
public enum LlmFeatures
{
    None = 0,
    /// <summary>Replies arrive incrementally.</summary>
    Streaming = 1,
    /// <summary>Honours <see cref="LlmRequest.System"/>.</summary>
    SystemPrompt = 2,
    /// <summary>Honours <see cref="LlmRequest.Effort"/> (reasoning depth; some backends only while thinking). See <see cref="ILlmProvider.EffortLevels"/>.</summary>
    Effort = 4,
    /// <summary>Implements <see cref="ILlmProvider.ListModelsAsync"/>.</summary>
    ModelList = 8,
    /// <summary>Honours <see cref="LlmRequest.Thinking"/> and streams the reasoning as thinking chunks.</summary>
    Thinking = 16,
    /// <summary>The model understands images (vision). Per model, from OpenRouter's catalog: see <see cref="ILlmService.CapabilitiesOf"/>.</summary>
    Vision = 32,
    /// <summary>The model speaks: text-to-speech / audio output. Per model, from OpenRouter's catalog: see <see cref="ILlmService.CapabilitiesOf"/>.</summary>
    Speech = 64,
}

/// <summary>Whether the model's part of <see cref="LlmCapabilities"/> is known.</summary>
public enum LlmFeatureSource
{
    /// <summary>OpenRouter's catalog does not list the model (or is not loaded yet): its own features are unknown, none are claimed.</summary>
    Unknown,
    /// <summary>OpenRouter's model catalog lists the model with its input / output modalities.</summary>
    Catalog,
}

/// <summary>What a service can do with a model, and whether the model's own features are known.</summary>
public readonly record struct LlmCapabilities(LlmFeatures Features, LlmFeatureSource Source);

/// <summary>Reading what model lists say about models (how PopBar reads OpenRouter's catalog).</summary>
public static class ModelCapabilities
{
    /// <summary>
    /// What one entry of a <c>/models</c> list says about the model (how the OpenRouter catalog is read), in the shapes gateways use: OpenRouter's
    /// <c>architecture.input_modalities</c> / <c>output_modalities</c>, top-level <c>input_modalities</c> /
    /// <c>output_modalities</c> / <c>modalities</c>, or <c>capabilities</c> as a list or an object of flags. Null when the
    /// entry says nothing about it.
    /// </summary>
    public static LlmFeatures? FromModelEntry(System.Text.Json.JsonElement entry)
    {
        if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        bool any = false;
        var f = LlmFeatures.None;
        void Inputs(System.Text.Json.JsonElement list)
        {
            if (list.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            any = true;
            foreach (var v in list.EnumerateArray())
                if (v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString() is "image" or "images" or "vision") f |= LlmFeatures.Vision;
        }
        void Outputs(System.Text.Json.JsonElement list)
        {
            if (list.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            any = true;
            foreach (var v in list.EnumerateArray())
                if (v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString() is "audio" or "speech") f |= LlmFeatures.Speech;
        }
        var scope = entry.TryGetProperty("architecture", out var arch) && arch.ValueKind == System.Text.Json.JsonValueKind.Object ? arch : entry;
        if (scope.TryGetProperty("input_modalities", out var input)) Inputs(input);
        if (scope.TryGetProperty("output_modalities", out var output)) Outputs(output);
        if (!any && entry.TryGetProperty("modalities", out var both)) Inputs(both);
        if (entry.TryGetProperty("capabilities", out var caps))
        {
            if (caps.ValueKind == System.Text.Json.JsonValueKind.Array) Inputs(caps);
            else if (caps.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                any = true;
                if (caps.TryGetProperty("vision", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True) f |= LlmFeatures.Vision;
                if ((caps.TryGetProperty("tts", out var t) || caps.TryGetProperty("audio_output", out t)) && t.ValueKind == System.Text.Json.JsonValueKind.True) f |= LlmFeatures.Speech;
            }
        }
        return any ? f : null;
    }
}

/// <summary>Values of <see cref="LlmRequest.Effort"/>, lowest to highest.</summary>
public static class LlmEffort
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Max = "max";

    /// <summary>All levels, ascending.</summary>
    public static readonly IReadOnlyList<string> All = [Low, Medium, High, Max];
    /// <summary>What most chat-completions servers take.</summary>
    public static readonly IReadOnlyList<string> Standard = [Low, Medium, High];

    /// <summary>Position in <see cref="All"/>; unknown strings rank as <see cref="Low"/>.</summary>
    public static int Rank(string level)
    {
        int i = All.ToList().FindIndex(l => string.Equals(l, level, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 0 : i;
    }

    /// <summary>Chinese label for a depth picker, e.g. "中（medium）".</summary>
    public static string Label(string level) => level.ToLowerInvariant() switch
    {
        Low => "浅（low）",
        Medium => "中（medium）",
        High => "深（high）",
        Max => "最深（max）",
        _ => level,
    };

    /// <summary>
    /// <paramref name="wanted"/> snapped to what a backend accepts: the highest of <paramref name="levels"/> not above
    /// it, else the lowest offered. Empty levels return <paramref name="wanted"/> unchanged.
    /// </summary>
    public static string Nearest(string wanted, IReadOnlyList<string> levels)
    {
        if (levels.Count == 0) return wanted;
        int want = Rank(wanted);
        string? best = null;
        foreach (var l in levels)
            if (Rank(l) <= want && (best == null || Rank(l) > Rank(best))) best = l;
        return best ?? levels.OrderBy(Rank).First();
    }
}

/// <summary>Resolved connection settings handed to a provider (base URL already defaulted).</summary>
public sealed record LlmConnection(string BaseUrl, string ApiKey, string Model, HttpClient Http);

public sealed record LlmRequest
{
    public string? System { get; init; }
    public required string User { get; init; }
    /// <summary>
    /// Provider id of the configured service to use (one of <see cref="ILlmService.ConfiguredProviderIds"/>); null / empty
    /// = the default service from the "服务" page.
    /// </summary>
    public string? Provider { get; init; }
    /// <summary>Model id to use instead of the service's default; null / empty keeps the default.</summary>
    public string? Model { get; init; }
    public int MaxTokens { get; init; } = 4096;
    /// <summary>
    /// One of <see cref="LlmEffort"/>: reasoning effort where the backend supports it. Null = the "深度思考" level of the
    /// service on the settings "服务" page (low while thinking is off). Snapped to <see cref="ILlmProvider.EffortLevels"/>.
    /// </summary>
    public string? Effort { get; init; }
    /// <summary>
    /// Ask the model to think before answering, where the backend can switch it on (reasoning models may think
    /// regardless). Null = the "深度思考" switch of the service on the settings "服务" page; false for quick jobs such as translation.
    /// </summary>
    public bool? Thinking { get; init; }

    /// <summary>Whether to think. Providers get requests with the service settings already filled in.</summary>
    public bool ThinkingOn => Thinking == true;
    /// <summary><see cref="Effort"/>, <see cref="LlmEffort.Low"/> when unset.</summary>
    public string EffortLevel => Effort ?? LlmEffort.Low;

    /// <summary>Output floor while thinking: thinking tokens count towards the limit, so a small one leaves no room for the answer.</summary>
    public const int MinMaxTokensWithThinking = 8192;
    /// <summary>The output limit providers should send: <see cref="MaxTokens"/>, raised to <see cref="MinMaxTokensWithThinking"/> when thinking.</summary>
    public int EffectiveMaxTokens => ThinkingOn ? Math.Max(MaxTokens, MinMaxTokensWithThinking) : MaxTokens;
}
