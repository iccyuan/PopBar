using System.Windows;
using System.Windows.Controls;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace PopBar.Plugins.Builtin;

/// <summary>Prompt-driven AI panel: each prompt becomes a button (解释 / 总结 / 润色 / 自定义).</summary>
public sealed class AiPlugin : IPopBarPlugin
{
    public sealed class Prompt
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..6];
        public string Label { get; set; } = "";
        public string Icon { get; set; } = "sparkles";
        public string System { get; set; } = "";
        /// <summary>{text} is replaced with the selection.</summary>
        public string Template { get; set; } = "{text}";
        public bool Enabled { get; set; } = true;
    }

    public sealed class Settings
    {
        public List<Prompt> Prompts { get; set; } = new();
        public string Effort { get; set; } = "low";
        public int MaxTokens { get; set; } = 2048;
    }

    private const string DefaultSystem = "你是一个嵌入在桌面工具里的助手。用户会选中一段文字，你的回答会显示在一个小面板里。请用中文、简洁直接地回答，不要寒暄，不要重复原文，必要时用短列表。";

    private IPluginHost _host = null!;
    private Settings _cfg = new();

    public string Id => "ai";
    public string Name => "AI 助手";
    public string Description => "解释、总结、润色选中内容，结果流式显示在面板里。可自定义任意提示词按钮。";
    public Version Version => new(0, 2, 0);

    public static List<Prompt> DefaultPrompts() => new()
    {
        new() { Id = "explain", Label = "解释", Icon = "sparkles", System = DefaultSystem, Template = "解释下面的内容（词语、术语、代码或句子都可以），说明含义、背景和用法：\n\n{text}" },
        new() { Id = "summarize", Label = "总结", Icon = "book-open", System = DefaultSystem, Template = "用 3-5 条要点总结下面的内容：\n\n{text}", Enabled = false },
        new() { Id = "polish", Label = "润色", Icon = "pencil", System = "你是一个文字编辑。只输出润色后的文本，保持原语言和原意，不加任何解释。", Template = "{text}", Enabled = false },
    };

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
        if (_cfg.Prompts.Count == 0) { _cfg.Prompts = DefaultPrompts(); host.Storage.Set(_cfg); }
    }

    public IReadOnlyList<PluginAction> GetActions() => _cfg.Prompts.Where(p => p.Enabled).Select(p => new PluginAction
    {
        Id = p.Id, Label = p.Label, Icon = p.Icon,
        IsApplicable = ctx => ctx.Text.Trim().Length > 0 && ctx.Text.Length <= 20000,
        Execute = (ctx, ct) => RunAsync(p, ctx, ct),
    }).ToList();

    private async Task RunAsync(Prompt prompt, SelectionContext ctx, CancellationToken ct)
    {
        var text = ctx.Text.Trim();
        var panel = _host.ShowPanel(new PanelOptions
        {
            Title = prompt.Label, Icon = prompt.Icon, AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY, Source = text, Width = 480,
        });
        panel.AddButton("复制", t => _host.CopyToClipboard(t));
        if (ctx.HasSourceWindow) panel.AddButton("替换原文", t => _host.PasteText(t, ctx.SourceWindow));

        if (!_host.Llm.IsConfigured)
        {
            panel.SetText("尚未配置 AI 模型。");
            panel.SetStatus("在 设置 → 插件 → AI 模型服务 中填写接口与密钥");
            panel.AddButton("打开设置", _ => _host.OpenSettings());
            return;
        }

        panel.SetBusy(true);
        panel.SetStatus(_host.Llm.ModelName + " · 生成中…");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, panel.Cancellation);
            var req = new LlmRequest
            {
                System = string.IsNullOrWhiteSpace(prompt.System) ? DefaultSystem : prompt.System,
                User = prompt.Template.Contains("{text}") ? prompt.Template.Replace("{text}", text) : prompt.Template + "\n\n" + text,
                Effort = _cfg.Effort,
                MaxTokens = _cfg.MaxTokens,
            };
            await foreach (var chunk in _host.Llm.StreamAsync(req, linked.Token)) panel.AppendText(chunk);
            panel.SetStatus(_host.Llm.ModelName);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            panel.SetStatus("失败：" + ex.Message);
            _host.Log.Error("ai request failed", ex);
        }
        finally { panel.SetBusy(false); }
    }

    // ------------------------------------------------------------------ settings

    public FrameworkElement? CreateSettingsView()
    {
        void Save() => _host.Storage.Set(_cfg);
        var ui = _host.Ui;
        var list = new StackPanel();

        bool Edit(Prompt p, bool isNew)
        {
            var draft = new Prompt { Id = p.Id, Label = p.Label, Icon = p.Icon, System = p.System, Template = p.Template, Enabled = p.Enabled };
            var content = Form.Stack(
                Form.Row("名称", Form.TextBox(draft.Label, 240, v => draft.Label = v)),
                Form.Row("图标", ui.IconPicker(draft.Icon, v => draft.Icon = v)),
                Form.Row("系统提示", Form.TextBox(draft.System, 320, v => draft.System = v, multiline: true), "留空用默认助手提示"),
                Form.Row("用户模板", Form.TextBox(draft.Template, 320, v => draft.Template = v, multiline: true), "{text} = 选中内容"));
            if (!ui.EditDialog(isNew ? "添加提示词按钮" : "编辑提示词按钮", content)) return false;
            if (string.IsNullOrWhiteSpace(draft.Label)) return false;
            p.Label = draft.Label.Trim(); p.Icon = draft.Icon; p.System = draft.System; p.Template = draft.Template;
            return true;
        }

        void Rebuild()
        {
            list.Children.Clear();
            foreach (var p in _cfg.Prompts)
            {
                var prompt = p;
                list.Children.Add(Form.ListRow(prompt.Label, Summary(prompt), prompt.Enabled,
                    on => { prompt.Enabled = on; Save(); },
                    () => { if (Edit(prompt, false)) { Save(); Rebuild(); } },
                    () => { _cfg.Prompts.Remove(prompt); Save(); Rebuild(); },
                    ui, prompt.Icon));
            }
        }
        Rebuild();

        var add = Form.Button("＋ 添加提示词", () =>
        {
            var p = new Prompt { Label = "", System = DefaultSystem, Template = "{text}" };
            if (Edit(p, true)) { _cfg.Prompts.Add(p); Save(); Rebuild(); }
        }, accent: true);

        return Form.Stack(
            Form.Card(
                Form.Row("回答深度", Form.Combo(new[] { ("low", "快（low）"), ("medium", "中（medium）"), ("high", "深（high）") }, _cfg.Effort, v => { _cfg.Effort = v; Save(); }, 180), "对应模型的 effort 参数"),
                Form.Row("最大输出 tokens", Form.TextBox(_cfg.MaxTokens.ToString(), 100, v => { if (int.TryParse(v, out var n) && n > 0) { _cfg.MaxTokens = n; Save(); } }))
            ),
            Form.Section("提示词按钮"),
            list,
            Form.Buttons(add),
            Form.Hint("新增或删除按钮后重启 PopBar 才会同步到操作条。"));
    }

    private static string Summary(Prompt p)
    {
        var t = p.Template.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > 60 ? t[..60] + "…" : t;
    }

    public void Dispose() { }
}
