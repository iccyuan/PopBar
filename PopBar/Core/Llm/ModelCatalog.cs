using System.IO;
using System.Net.Http;
using System.Text.Json;
using PopBar.Sdk;

namespace PopBar.Core.Llm;

/// <summary>
/// What models can do (Vision / Speech), from OpenRouter's public model catalog (no key needed), which lists hundreds of
/// models from every vendor with their input / output modalities. Fetched at most once a day and kept in a file next to
/// the config, so lookups need no network. Models from any service are matched to catalog entries by a normalized id
/// (<see cref="Lookup"/>); one the catalog does not list is unknown, never guessed.
/// </summary>
internal sealed class ModelCatalog
{
    public const string Url = "https://openrouter.ai/api/v1/models";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private readonly HttpClient _http;
    private readonly string _file;
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private volatile Entry[] _entries = [];
    private DateTime _loadedAt = DateTime.MinValue;

    /// <summary>A catalog model: its full id ("openai/gpt-4o"), the normalized id and words used for matching, its features.</summary>
    private sealed record Entry(string Id, string Key, string[] Words, LlmFeatures Features);

    public ModelCatalog(HttpClient http, string file)
    {
        _http = http;
        _file = file;
        try { if (File.Exists(file)) { Load(File.ReadAllText(file)); _loadedAt = File.GetLastWriteTimeUtc(file); } }
        catch (Exception ex) { Log.Warn("model catalog cache: " + ex.Message); }
    }

    /// <summary>The catalog has entries (from the cache file or a fetch).</summary>
    public bool IsLoaded => _entries.Length > 0;

    /// <summary>Raised (on a worker thread) when a fetch brought new entries.</summary>
    public event Action? Updated;

    /// <summary>Fetch the catalog when it is missing or older than a day; failures leave what is there.</summary>
    public async Task EnsureFreshAsync(CancellationToken ct = default)
    {
        if (IsLoaded && DateTime.UtcNow - _loadedAt < MaxAge) return;
        await _fetching.WaitAsync(ct);
        try
        {
            if (IsLoaded && DateTime.UtcNow - _loadedAt < MaxAge) return;
            using var resp = await _http.GetAsync(Url, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            Load(json);
            _loadedAt = DateTime.UtcNow;
            try { File.WriteAllText(_file, json); } catch (Exception ex) { Log.Warn("model catalog cache: " + ex.Message); }
            Log.Info($"model catalog: {_entries.Length} models from OpenRouter");
            Updated?.Invoke();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn("model catalog fetch: " + ex.Message); }
        finally { _fetching.Release(); }
    }

    /// <summary>Read a catalog response (the shape of <see cref="Url"/>): every entry with its id(s) and modalities.</summary>
    internal void Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;
        var list = new List<Entry>();
        foreach (var m in data.EnumerateArray())
        {
            if (!m.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
            if (ModelCapabilities.FromModelEntry(m) is not { } f) continue;
            Add(id);
            // the dated release behind an alias ("openai/gpt-4o-2024-08-06"), matched too
            if (m.TryGetProperty("canonical_slug", out var slug) && slug.GetString() is { Length: > 0 } s && !string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) Add(s);

            void Add(string fullId) => list.Add(new Entry(fullId.ToLowerInvariant(), Key(fullId), Words(fullId), f));
        }
        if (list.Count > 0) _entries = list.ToArray();
    }

    /// <summary>
    /// The features of the catalog model <paramref name="model"/> stands for, or null when the catalog does not list it.
    /// Ids differ between services, so after the exact full id ("openai/gpt-4o", as OpenRouter and relays name them) the
    /// match uses the part after the vendor, with case, punctuation and variant tags ignored ("claude-opus-5-5" =
    /// "anthropic/claude-opus-5.5", "qwen3-vl:235b" = "qwen/qwen3-vl-235b-a22b-instruct", "gpt-4o-2024-08-06" = "openai/gpt-4o"):
    /// the same name, else the shortest entry starting with it, else the longest entry whose words it starts with.
    /// </summary>
    public LlmFeatures? Lookup(string? model)
    {
        var entries = _entries;
        var m = (model ?? "").Trim().ToLowerInvariant();
        if (m.Length == 0 || entries.Length == 0) return null;
        foreach (var e in entries) if (e.Id == m) return e.Features;
        var key = Key(m);
        if (key.Length < 2) return null;
        foreach (var e in entries) if (e.Key == key) return e.Features;
        var words = Words(m);
        // trailing words left off, whole words only: "qwen3-vl:235b" -> "qwen3-vl-235b-a22b-instruct"
        var longer = entries.Where(e => e.Words.Length > words.Length && e.Words.Take(words.Length).SequenceEqual(words)).MinBy(e => e.Words.Length);
        if (longer != null) return longer.Features;
        // a release date / version number added: "gpt-4o-2024-08-06" -> "gpt-4o"; nothing else, so neither "gpt-4o-mini"
        // nor "gpt-4o-audio-preview" (other models) is ever taken for "gpt-4o"
        var shorter = entries.Where(e => e.Words.Length >= 2 && e.Words.Length < words.Length && e.Words.SequenceEqual(words.Take(e.Words.Length))
                                         && words.Skip(e.Words.Length).All(w => w.All(char.IsDigit))).MaxBy(e => e.Words.Length);
        return shorter?.Features;
    }

    /// <summary>The model name without vendor and variant tag, letters and digits only: "anthropic/claude-opus-5.5:thinking" -> "claudeopus55".</summary>
    internal static string Key(string id) => new(Name(id).Where(char.IsLetterOrDigit).ToArray());

    private static string[] Words(string id) => Name(id).Split(['-', '.', '_', ':', ' '], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Lower case, the part after the last '/', without OpenRouter's variant tags (":free", ":thinking"...), Ollama's
    /// ":latest" or a "-latest" alias suffix ("~deepseek/deepseek-flash-latest" is what "deepseek-flash" points at).
    /// </summary>
    private static string Name(string id)
    {
        var s = id.ToLowerInvariant();
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        int colon = s.LastIndexOf(':');
        if (colon >= 0 && s[(colon + 1)..] is "free" or "beta" or "extended" or "thinking" or "nitro" or "online" or "floor" or "latest" or "exacto" or "batch") s = s[..colon];
        if (s.EndsWith("-latest", StringComparison.Ordinal)) s = s[..^"-latest".Length];
        return s;
    }
}
