using System.Net.Http;
using PopBar.Config;
using PopBar.Core.Llm;
using PopBar.Sdk;

namespace PopBar.Core;

/// <summary>
/// Routes LLM calls to the provider selected in settings. Providers are pluggable (<see cref="ILlmProvider"/>):
/// built-ins are registered here, plugins add their own through <see cref="RegisterProvider"/>.
/// </summary>
internal sealed class LlmService : ILlmService
{
    private readonly ConfigStore _store;
    private readonly HttpClient _http;
    private readonly List<ILlmProvider> _providers = new();
    private readonly object _gate = new();

    public LlmService(ConfigStore store, HttpClient http)
    {
        _store = store;
        _http = http;
        RegisterProvider(new AnthropicProvider());
        RegisterProvider(OpenAiCompatibleProvider.Generic());
        RegisterProvider(OpenAiCompatibleProvider.Ollama());
        RegisterProvider(OpenAiCompatibleProvider.DeepSeek());
    }

    public IReadOnlyList<ILlmProvider> Providers { get { lock (_gate) return _providers.ToList(); } }

    public void RegisterProvider(ILlmProvider provider)
    {
        lock (_gate)
        {
            _providers.RemoveAll(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
            _providers.Add(provider);
        }
        Log.Info($"llm provider registered: {provider.Id}");
    }

    public ILlmProvider? Current
    {
        get
        {
            var id = _store.Current.Llm.Protocol;
            lock (_gate) return _providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public string ProviderId => _store.Current.Llm.Protocol;
    public string ModelName => Resolve().Model;

    public bool IsConfigured
    {
        get
        {
            var p = Current;
            if (p == null) return false;
            var c = Resolve(p);
            bool hasEndpoint = !string.IsNullOrWhiteSpace(c.BaseUrl) || p.Id == "anthropic"; // the SDK knows its own default host
            bool hasKey = !p.RequiresApiKey || !string.IsNullOrWhiteSpace(c.ApiKey) || !string.IsNullOrWhiteSpace(_store.Current.Llm.BaseUrl); // a custom relay may not need one
            return !string.IsNullOrWhiteSpace(c.Model) && hasEndpoint && hasKey;
        }
    }

    /// <summary>Config + provider defaults → concrete connection.</summary>
    private LlmConnection Resolve(ILlmProvider? p = null)
    {
        p ??= Current;
        var cfg = _store.Current.Llm;
        var baseUrl = string.IsNullOrWhiteSpace(cfg.BaseUrl) ? (p?.DefaultBaseUrl ?? "") : cfg.BaseUrl.Trim();
        var model = string.IsNullOrWhiteSpace(cfg.Model) ? (p?.DefaultModel ?? "") : cfg.Model.Trim();
        return new LlmConnection(baseUrl, cfg.ApiKey?.Trim() ?? "", model, _http);
    }

    public IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken ct = default)
    {
        var p = Current ?? throw new InvalidOperationException($"未知的模型提供商“{ProviderId}”，请在 设置 → 插件 → AI 模型服务 重新选择。");
        if (!IsConfigured) throw new InvalidOperationException("尚未配置 AI 模型，请在 设置 → 插件 → AI 模型服务 中填写。");
        return p.StreamAsync(Resolve(p), request, ct);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var p = Current ?? throw new InvalidOperationException("请先选择模型提供商。");
        var c = Resolve(p);
        if (string.IsNullOrWhiteSpace(c.BaseUrl) && p.Id != "anthropic") throw new InvalidOperationException("请先填写接口地址。");
        return p.ListModelsAsync(c, ct);
    }
}
