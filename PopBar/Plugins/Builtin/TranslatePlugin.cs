using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace PopBar.Plugins.Builtin;

/// <summary>Embedded translation panel. Providers: Google (free endpoint), Microsoft (Edge token), or the configured LLM.</summary>
public sealed class TranslatePlugin : IPopBarPlugin
{
    public sealed class Settings
    {
        public string Provider { get; set; } = "google";   // google | microsoft | llm
        public string TargetLang { get; set; } = "zh-CN";
        /// <summary>Used when the source is already the target language.</summary>
        public string AltLang { get; set; } = "en";
    }

    private IPluginHost _host = null!;
    private Settings _cfg = new();
    private string? _msToken;
    private DateTime _msTokenAt;

    public string Id => "translate";
    public string Name => "翻译";
    public string Description => "在悬浮面板里直接显示译文；支持 Google / 微软免费接口或 AI 模型。";
    public Version Version => new(0, 2, 0);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
    }

    public IReadOnlyList<PluginAction> GetActions() => new[]
    {
        new PluginAction
        {
            Id = "panel", Label = "翻译", Icon = "languages",
            IsApplicable = ctx => ctx.Text.Trim().Length > 0 && ctx.Text.Length <= 5000,
            Execute = RunAsync,
        },
    };

    private async Task RunAsync(SelectionContext ctx, CancellationToken ct)
    {
        var text = ctx.Text.Trim();
        var panel = _host.ShowPanel(new PanelOptions
        {
            Title = "翻译", Icon = "languages", AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY, Source = text,
        });
        panel.AddButton("复制", t => _host.CopyToClipboard(t));
        if (ctx.HasSourceWindow) panel.AddButton("替换原文", t => _host.PasteText(t, ctx.SourceWindow));
        panel.SetBusy(true);
        panel.SetStatus("翻译中…");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, panel.Cancellation);
            string target = LooksLike(text, _cfg.TargetLang) ? _cfg.AltLang : _cfg.TargetLang;
            switch (_cfg.Provider)
            {
                case "microsoft":
                {
                    var (result, detected) = await MicrosoftAsync(text, target, linked.Token);
                    panel.SetText(result);
                    panel.SetStatus($"{detected} → {target} · 微软翻译");
                    break;
                }
                case "llm":
                {
                    panel.SetStatus($"→ {target} · {_host.Llm.ModelName}");
                    var req = new LlmRequest
                    {
                        System = "You are a professional translator. Translate the user's text into the requested language. Output only the translation, no explanations, keep formatting.",
                        User = $"Target language: {target}\n\n{text}",
                        Effort = "low",
                    };
                    await foreach (var chunk in _host.Llm.StreamAsync(req, linked.Token)) panel.AppendText(chunk);
                    break;
                }
                default:
                {
                    var (result, detected) = await GoogleAsync(text, target, linked.Token);
                    panel.SetText(result);
                    panel.SetStatus($"{detected} → {target} · Google");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            panel.SetStatus("失败：" + ex.Message);
            _host.Log.Error("translate failed", ex);
        }
        finally { panel.SetBusy(false); }
    }

    /// <summary>Cheap script heuristic: is the text already (mostly) in the target language?</summary>
    private static bool LooksLike(string text, string lang)
    {
        int cjk = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        int letters = text.Count(char.IsLetter);
        if (letters == 0) return false;
        bool isCjkTarget = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || lang.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        double ratio = (double)cjk / letters;
        return isCjkTarget ? ratio > 0.5 : ratio < 0.1 && lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) && IsAscii(text);
    }

    private static bool IsAscii(string s) => s.All(c => c < 128);

    // ------------------------------------------------------------------ providers

    private async Task<(string Text, string Detected)> GoogleAsync(string text, string target, CancellationToken ct)
    {
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(target)}&dt=t&q={Uri.EscapeDataString(text)}";
        using var resp = await _host.Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var sb = new StringBuilder();
        foreach (var seg in root[0].EnumerateArray())
            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());
        string detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String ? root[2].GetString() ?? "auto" : "auto";
        return (sb.ToString(), detected);
    }

    private async Task<(string Text, string Detected)> MicrosoftAsync(string text, string target, CancellationToken ct)
    {
        if (_msToken == null || DateTime.UtcNow - _msTokenAt > TimeSpan.FromMinutes(8))
        {
            _msToken = (await _host.Http.GetStringAsync("https://edge.microsoft.com/translate/auth", ct)).Trim();
            _msTokenAt = DateTime.UtcNow;
        }
        var to = target.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : target;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api-edge.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Uri.EscapeDataString(to)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new[] { new { Text = text } }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _msToken);
        using var resp = await _host.Http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) { _msToken = null; }
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var first = doc.RootElement[0];
        string detected = first.TryGetProperty("detectedLanguage", out var dl) && dl.TryGetProperty("language", out var l) ? l.GetString() ?? "auto" : "auto";
        var translated = first.GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
        return (translated, detected);
    }

    // ------------------------------------------------------------------ settings

    public FrameworkElement? CreateSettingsView()
    {
        void Save() => _host.Storage.Set(_cfg);
        return Form.Stack(
            Form.Card(
                Form.Row("翻译服务", Form.Combo(new[] { ("google", "Google 翻译（免费）"), ("microsoft", "微软翻译（免费）"), ("llm", "AI 模型（设置里配置的模型）") }, _cfg.Provider, v => { _cfg.Provider = v; Save(); }, 260), "Google 在国内需要代理；微软接口直连可用"),
                Form.Row("目标语言", Form.TextBox(_cfg.TargetLang, 120, v => { _cfg.TargetLang = v.Trim(); Save(); }), "如 zh-CN、en、ja"),
                Form.Row("备用语言", Form.TextBox(_cfg.AltLang, 120, v => { _cfg.AltLang = v.Trim(); Save(); }), "选中内容已经是目标语言时，改译成这种语言")
            ));
    }

    public void Dispose() { }
}
