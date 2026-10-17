using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace PopBar.Plugins.Builtin;

/// <summary>Embedded translation panel. Providers: Google (free endpoint), Microsoft (Edge token), or the configured LLM.</summary>
public sealed class TranslatePlugin : IPopBarPlugin, ITextTranslator
{
    public sealed class Settings
    {
        public string Provider { get; set; } = "google";   // google | microsoft | llm
        public string TargetLang { get; set; } = "zh-CN";
        /// <summary>Used when the source is already the target language.</summary>
        public string AltLang { get; set; } = "en";
        /// <summary>AI mode: show the translation as it is generated (off = show it once complete).</summary>
        public bool Stream { get; set; } = true;
        /// <summary>AI mode: service to use (an <see cref="LlmRequest.Provider"/> id); empty = the default from the "服务" page.</summary>
        public string LlmProvider { get; set; } = "";
        /// <summary>AI mode: model override; empty = the service's default model.</summary>
        public string Model { get; set; } = "";
    }

    private IPluginHost _host = null!;
    private Settings _cfg = new();

    public string Id => "translate";
    public string Icon => "languages";
    public string Name => "翻译";
    public string Description => "面板里直接出译文，用 Google 或 AI 模型。";
    public Version Version => new(0, 2, 0);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
        host.Services.Provide<ITextTranslator>(this);
    }

    /// <summary>Non-streaming translation for other plugins (screen translate...), same provider and language rules.</summary>
    public async Task<TranslationResult> TranslateAsync(string text, string? targetLang = null, CancellationToken ct = default)
    {
        string target = targetLang ?? TargetFor(text);
        if (_cfg.Provider == "llm")
        {
            var sb = new StringBuilder();
            await foreach (var chunk in _host.Llm.StreamAsync(LlmRequestFor(text, target), ct)) sb.Append(chunk);
            return new TranslationResult(sb.ToString().Trim(), "auto", target, _host.Llm.ModelNameFor(_cfg.LlmProvider, _cfg.Model));
        }
        var (result, detected) = await GoogleAsync(text, target, ct);
        return new TranslationResult(result, detected, target, "Google");
    }

    private LlmRequest LlmRequestFor(string text, string target) => new()
    {
        System = "You are a professional translator. Translate the user's text into the requested language. Output only the translation, no explanations, keep formatting and line breaks.",
        User = $"Target language: {target}\n\n{text}",
        Effort = LlmEffort.Low,
        Thinking = false, // a translation should come back fast, whatever the service's 深度思考 setting
        Provider = string.IsNullOrWhiteSpace(_cfg.LlmProvider) ? null : _cfg.LlmProvider,
        Model = _cfg.Model,
    };

    public IReadOnlyList<PluginAction> GetActions() => new[]
    {
        new PluginAction
        {
            Id = "panel", Label = "翻译", Icon = "languages",
            Description = "在浮动面板中翻译选中内容",
            PlaceAfter = "search", // where the stock bar used to have "翻译"
            IsApplicable = ctx => ctx.Text.Trim().Length > 0 && ctx.Text.Length <= 5000,
            Execute = RunAsync,
        },
    };

    private async Task RunAsync(SelectionContext ctx, CancellationToken ct)
    {
        var text = ctx.Text.Trim();
        var panel = await _host.ShowPanelAsync(new PanelOptions
        {
            Title = "翻译", Icon = "languages", AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY, Source = text, Width = 520,
            Badge = _cfg.Provider == "llm" ? _host.Llm.ModelNameFor(_cfg.LlmProvider, _cfg.Model) : "Google 翻译",
        });
        panel.AddButton("复制", t => _host.CopyToClipboard(t));
        if (ctx.HasSourceWindow) panel.AddButton("替换原文", t => _host.PasteText(t, ctx.SourceWindow));
        panel.SetBusy(true);
        panel.SetStatus("翻译中…");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, panel.Cancellation);
            string target = TargetFor(text);
            string? source = DetectLang(text);
            switch (_cfg.Provider)
            {
                case "llm":
                {
                    if (_cfg.Stream)
                    {
                        panel.SetStatus(Direction(source, target));
                        await foreach (var chunk in _host.Llm.StreamAsync(LlmRequestFor(text, target), linked.Token)) panel.AppendText(chunk);
                    }
                    else
                    {
                        var full = await _host.Llm.CompleteAsync(LlmRequestFor(text, target), linked.Token);
                        panel.SetText(full.Trim());
                        panel.SetStatus(Direction(source, target));
                    }
                    break;
                }
                default:
                {
                    var (result, detected) = await GoogleAsync(text, target, linked.Token);
                    panel.SetText(result);
                    panel.SetStatus(Direction(detected, target));
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            panel.SetError("Google 翻译暂时限流（429），稍等几秒再试，或在插件设置里改用 AI 模型");
        }
        catch (Exception ex)
        {
            panel.SetError("翻译失败：" + Errors.Describe(ex));
            _host.Log.Error("translate failed", ex);
        }
        finally { panel.SetBusy(false); }
    }

    // ------------------------------------------------------------------ languages

    /// <summary>Always the target language, unless the text already is in it: then the alternative one.</summary>
    private string TargetFor(string text) => SameLang(DetectLang(text), _cfg.TargetLang) ? _cfg.AltLang : _cfg.TargetLang;

    /// <summary>
    /// Script-based source detection, good enough to pick a direction and label it: kana → ja, Hangul → ko, Han → zh,
    /// Cyrillic / Arabic / Thai, plain ASCII words → en. Accented Latin (fr, de, es...) is left to the translator (null).
    /// Latin is counted per word and CJK per character, so "我爱Python编程" stays Chinese.
    /// </summary>
    internal static string? DetectLang(string text)
    {
        int han = 0, kana = 0, hangul = 0, cyr = 0, arab = 0, thai = 0, latinWords = 0;
        bool inWord = false, accented = false;
        foreach (char c in text)
        {
            bool latin = false;
            if (c is >= '\u3040' and <= '\u30FF') kana++;
            else if (c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF') han++;
            else if (c is >= '\uAC00' and <= '\uD7AF' or >= '\u1100' and <= '\u11FF') hangul++;
            else if (c is >= '\u0400' and <= '\u04FF') cyr++;
            else if (c is >= '\u0600' and <= '\u06FF') arab++;
            else if (c is >= '\u0E00' and <= '\u0E7F') thai++;
            else if (char.IsLetter(c)) { latin = true; if (c > 127) accented = true; }
            if (latin && !inWord) latinWords++;
            inWord = latin;
        }
        int total = han + kana + hangul + cyr + arab + thai + latinWords;
        if (total == 0) return null;
        if (kana > 0 && kana * 5 >= kana + han) return "ja";
        if (hangul * 2 >= total) return "ko";
        if ((han + kana) * 2 >= total) return "zh";
        if (cyr * 2 >= total) return "ru";
        if (arab * 2 >= total) return "ar";
        if (thai * 2 >= total) return "th";
        if (latinWords * 2 >= total) return accented ? null : "en";
        return null;
    }

    private static bool SameLang(string? a, string b) =>
        a != null && string.Equals(a.Split('-')[0], b.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    internal static readonly (string Code, string Name)[] Languages =
    {
        ("zh-CN", "中文（简体）"), ("zh-TW", "中文（繁体）"), ("en", "英语"), ("ja", "日语"), ("ko", "韩语"),
        ("fr", "法语"), ("de", "德语"), ("es", "西班牙语"), ("ru", "俄语"), ("pt", "葡萄牙语"),
        ("it", "意大利语"), ("ar", "阿拉伯语"), ("th", "泰语"), ("vi", "越南语"),
    };

    private static string LangName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == "auto") return "自动检测";
        var hit = Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        if (hit.Name != null) return hit.Name;
        hit = Languages.FirstOrDefault(l => SameLang(l.Code, code)); // "zh" → 中文（简体）
        return hit.Name ?? code;
    }

    private static string Direction(string? source, string target) => $"{LangName(source)} → {LangName(target)}";

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

    // ------------------------------------------------------------------ settings

    /// <summary>The common languages, plus the current value if it was typed by hand in an older version.</summary>
    private static (string, string)[] LangItems(string current) =>
        Languages.Any(l => l.Code == current) || string.IsNullOrWhiteSpace(current) ? Languages : Languages.Append((current, current)).ToArray();

    public FrameworkElement? CreateSettingsView()
    {
        // Edit a draft; it only takes effect when the settings window is saved.
        var d = SettingsDraft.Clone(_cfg);
        void Changed() => _host.Settings.Changed();
        _host.Settings.OnApply(() =>
        {
            _cfg = SettingsDraft.Clone(d);
            _host.Storage.Set(_cfg);
        });
        // Service, model and streaming only apply to the AI provider: grey them out otherwise. The model picker follows the service.
        var modelHost = new ContentControl();
        void BuildModel() => modelHost.Content = _host.Ui.ModelPicker(d.LlmProvider, d.Model, v => { d.Model = v; Changed(); });
        BuildModel();
        var serviceRow = Form.Row("服务商", _host.Ui.ProviderPicker(d.LlmProvider, v => { d.LlmProvider = v; d.Model = ""; BuildModel(); Changed(); }), "「服务」页里配置的服务商");
        var modelRow = Form.Row("模型", modelHost, "初始为该服务商在「服务」页的模型；可改，清空即恢复");
        var streamRow = Form.Row("流式输出", Form.Toggle(d.Stream, v => { d.Stream = v; Changed(); }), "边生成边显示");
        void SyncStream()
        {
            bool llm = d.Provider == "llm";
            foreach (var row in new[] { serviceRow, modelRow, streamRow }) { row.IsEnabled = llm; row.Opacity = llm ? 1 : 0.45; }
        }
        SyncStream();
        return Form.Stack(
            Form.Card(
                Form.Row("翻译服务", Form.Combo(new[] { ("google", "Google 翻译（免费）"), ("llm", "AI 模型（「服务」页配置）") }, d.Provider, v => { d.Provider = v; SyncStream(); Changed(); }, 260), "Google 需要代理"),
                serviceRow,
                modelRow,
                streamRow,
                Form.Row("目标语言", Form.Combo(LangItems(d.TargetLang), d.TargetLang, v => { d.TargetLang = v; Changed(); }, 180), "译成这种语言"),
                Form.Row("备用语言", Form.Combo(LangItems(d.AltLang), d.AltLang, v => { d.AltLang = v; Changed(); }, 180), "原文已是目标语言时改译这种")
            ));
    }

    public void Dispose() { }
}
