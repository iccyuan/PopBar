namespace PopBar.Sdk;

/// <summary>Streaming access to the model configured in PopBar settings (Anthropic or OpenAI-compatible).</summary>
public interface ILlmService
{
    bool IsConfigured { get; }
    /// <summary>Short description for status lines, e.g. "claude-opus-5-5".</summary>
    string ModelName { get; }

    /// <summary>Stream the assistant reply as text chunks.</summary>
    IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>Convenience: collect the whole reply.</summary>
    async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in StreamAsync(request, ct)) sb.Append(chunk);
        return sb.ToString();
    }
}

public sealed class LlmRequest
{
    public string? System { get; init; }
    public required string User { get; init; }
    public int MaxTokens { get; init; } = 4096;
    /// <summary>"low" | "medium" | "high" - reasoning effort where the backend supports it.</summary>
    public string Effort { get; init; } = "low";
}
