using System.Net.Http;
using PopBar.Config;
using PopBar.Core.Llm;
using PopBar.Sdk;

namespace PopBar.Core;

/// <summary>
/// Routes LLM calls to the services configured on the settings "服务" page: one per provider, one of them the default.
/// Providers are pluggable (<see cref="ILlmProvider"/>): built-ins are registered here, plugins add their own through
/// <see cref="RegisterProvider"/>.
/// </summary>
internal sealed class LlmService : ILlmService
{
    private readonly ConfigStore _store;
    private readonly ProxyManager _proxies;
    private readonly List<ILlmProvider> _providers = new();
    private readonly object _gate = new();

    public LlmService(ConfigStore store, ProxyManager proxies)
    {
        _store = store;
        _proxies = proxies;
        foreach (var p in BuiltinProviders.Create()) RegisterProvider(p);
        Catalog = new ModelCatalog(proxies.Main, System.IO.Path.Combine(ConfigStore.Dir, "model-catalog.json"));
        _ = Task.Run(() => Catalog.EnsureFreshAsync()); // at most daily, in the background
    }

    public IReadOnlyList<ILlmProvider> Providers { get { lock (_gate) return _providers.ToList(); } }

    /// <summary>Drop the providers an unloaded plugin registered (identified by the assembly they come from).</summary>
    public void RemoveProvidersFrom(System.Reflection.Assembly asm)
    {
        lock (_gate) _providers.RemoveAll(p => p.GetType().Assembly == asm);
    }

    public void RegisterProvider(ILlmProvider provider)
    {
        lock (_gate)
        {
            _providers.RemoveAll(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
            _providers.Add(provider);
        }
        Log.Info($"llm provider registered: {provider.Id}");
    }

    private ILlmProvider? Provider(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate) return _providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The service a request names (null / empty = the default) and the provider behind it; the config is null when it is not set up.</summary>
    private (string Id, LlmConfig? Cfg, ILlmProvider? Provider) Service(string? providerId)
    {
        var c = _store.Current;
        var id = string.IsNullOrWhiteSpace(providerId) ? c.LlmDefaultId : providerId.Trim();
        var cfg = c.LlmServiceFor(id);
        return (id, cfg, Provider(id));
    }

    public string ProviderId => _store.Current.LlmDefaultId;
    public string ModelName => ModelNameFor(null, null);
    public string ModelNameFor(string? providerId, string? model) => Resolve(providerId, model).Model;
    public string ProviderNameFor(string? providerId)
    {
        var (id, _, p) = Service(providerId);
        return p?.Name ?? id;
    }

    public IReadOnlyList<string> ConfiguredProviderIds
    {
        get
        {
            var c = _store.Current;
            var ids = c.LlmServices.Select(s => s.Protocol).Where(IsConfiguredFor).ToList();
            // the default first, so pickers list it where users look first
            ids.Sort((a, b) => string.Equals(a, c.LlmDefaultId, StringComparison.OrdinalIgnoreCase) ? -1 : string.Equals(b, c.LlmDefaultId, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            return ids;
        }
    }

    public bool IsConfigured => IsConfiguredFor(null);

    public bool IsConfiguredFor(string? providerId)
    {
        var (_, cfg, p) = Service(providerId);
        if (cfg == null || p == null) return false;
        var c = Resolve(providerId);
        bool hasEndpoint = !string.IsNullOrWhiteSpace(c.BaseUrl) || !p.RequiresBaseUrl;
        bool hasKey = !p.RequiresApiKey || !string.IsNullOrWhiteSpace(c.ApiKey) || !string.IsNullOrWhiteSpace(cfg.BaseUrl); // a custom relay may not need one
        return !string.IsNullOrWhiteSpace(c.Model) && hasEndpoint && hasKey;
    }

    /// <summary>Config + provider defaults → concrete connection. <paramref name="model"/> (a per-request override) wins over the service's default.</summary>
    private LlmConnection Resolve(string? providerId = null, string? model = null)
    {
        var (id, cfg, p) = Service(providerId);
        cfg ??= new LlmConfig();
        var baseUrl = string.IsNullOrWhiteSpace(cfg.BaseUrl) ? (p?.DefaultBaseUrl ?? "") : cfg.BaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(model)) model = string.IsNullOrWhiteSpace(cfg.Model) ? (p?.DefaultModel ?? "") : cfg.Model.Trim();
        // the service's own proxy setting (else PopBar's), whichever plugin is asking
        return new LlmConnection(baseUrl, cfg.ApiKey?.Trim() ?? "", model.Trim(), _proxies.ForService(id));
    }

    public IReadOnlyList<string> EffortLevels(string? providerId, string? model)
    {
        var (_, _, p) = Service(providerId);
        if (p == null) return Array.Empty<string>();
        try { return p.EffortLevels(Resolve(providerId, model).Model); }
        catch (Exception ex) { Log.Warn($"effort levels of {p.Id}: {ex.Message}"); return Array.Empty<string>(); }
    }

    /// <summary>OpenRouter's model catalog, the one source of per-model features.</summary>
    public ModelCatalog Catalog { get; }

    public LlmCapabilities CapabilitiesOf(string? providerId, string? model)
    {
        var (_, _, p) = Service(providerId);
        return p == null ? new(LlmFeatures.None, LlmFeatureSource.Unknown) : CapabilitiesOf(p, Resolve(providerId, model).Model);
    }

    /// <summary>A provider's features plus what the catalog lists for <paramref name="model"/> (the settings page passes its unsaved pick).</summary>
    public LlmCapabilities CapabilitiesOf(ILlmProvider p, string model) =>
        Catalog.Lookup(model) is { } f ? new(p.Features | f, LlmFeatureSource.Catalog) : new(p.Features, LlmFeatureSource.Unknown);

    public async Task<LlmCapabilities> RefreshCapabilitiesAsync(string? providerId, string? model, CancellationToken ct = default)
    {
        await Catalog.EnsureFreshAsync(ct);
        return CapabilitiesOf(providerId, model);
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var c in StreamChunksAsync(request, ct))
            if (!c.IsThinking) yield return c.Text;
    }

    public IAsyncEnumerable<LlmChunk> StreamChunksAsync(LlmRequest request, CancellationToken ct = default)
    {
        var (id, cfg, p) = Service(request.Provider);
        if (p == null) throw new InvalidOperationException($"未知的模型服务商“{id}”，请在 设置 → 服务 重新选择。");
        if (cfg == null || !IsConfiguredFor(request.Provider)) throw new InvalidOperationException($"尚未配置 {p.Name}，请在 设置 → 服务 中填写。");
        var connection = Resolve(request.Provider, request.Model);
        return p.StreamChunksAsync(connection, WithServiceDefaults(p, cfg, connection.Model, request), ct);
    }

    /// <summary>
    /// Fills what the request leaves to the service's "深度思考" settings (while not thinking the effort stays low) and
    /// snaps the effort to the levels the provider takes for this model.
    /// </summary>
    private static LlmRequest WithServiceDefaults(ILlmProvider p, LlmConfig cfg, string model, LlmRequest request)
    {
        bool thinking = request.Thinking ?? (cfg.Thinking && p.Features.HasFlag(LlmFeatures.Thinking));
        var effort = request.Effort ?? (thinking ? cfg.Effort : LlmEffort.Low);
        var levels = p.EffortLevels(model);
        if (levels.Count > 0) effort = LlmEffort.Nearest(effort, levels);
        return request with { Thinking = thinking, Effort = effort };
    }

    public IReadOnlyList<string> KnownModels(string? providerId = null) => _store.Current.LlmServiceFor(providerId)?.Models?.ToList() ?? new List<string>();

    public async Task<IReadOnlyList<string>> ListModelsAsync(string? providerId = null, CancellationToken ct = default)
    {
        var (id, _, p) = Service(providerId);
        if (p == null) throw new InvalidOperationException($"未知的模型服务商“{id}”，请先在 设置 → 服务 选择。");
        var c = Resolve(providerId);
        if (string.IsNullOrWhiteSpace(c.BaseUrl) && p.RequiresBaseUrl) throw new InvalidOperationException("请先填写接口地址。");
        var models = await p.ListModelsAsync(c, ct);
        // Remember the list so pickers can show it next time without asking the server again.
        if (models.Count > 0)
            _store.Update(cfg => { var s = cfg.LlmServiceFor(id); if (s != null) s.Models = models.ToList(); });
        return models;
    }
}
