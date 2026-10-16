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
        public int MaxTokens { get; set; } = 2048;
        /// <summary>Show the answer as it is generated (off = show it once complete).</summary>
        public bool Stream { get; set; } = true;
        /// <summary>Service to use (an <see cref="LlmRequest.Provider"/> id); empty = the default from the "服务" page.</summary>
        public string Provider { get; set; } = "";
        /// <summary>Model override for this plugin; empty = the service's default model.</summary>
        public string Model { get; set; } = "";
        /// <summary>
        /// 深度思考 for this plugin: null = follow the "服务" page, true / false = override it. Named "override" so a
        /// stale "effort" / "thinking" from v0.2.0 (then the plugin's own, absolute setting) is not read as one.
        /// </summary>
        public bool? ThinkingOverride { get; set; }
        /// <summary>Thinking depth (<see cref="LlmEffort"/>) for this plugin; empty = follow the "服务" page.</summary>
        public string EffortOverride { get; set; } = "";
    }

    private const string DefaultSystem = "你是一个嵌入在桌面工具里的助手。用户会选中一段文字，你的回答会显示在一个小面板里。请用中文、简洁直接地回答，不要寒暄，不要重复原文，必要时用短列表。";

    private IPluginHost _host = null!;
    private Settings _cfg = new();

    public string Id => "ai";
    public string Name => "AI 助手";
    public string Description => "解释、总结、润色选中内容，按钮可自定义。";
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
        Description = Describe(p),
        IsApplicable = ctx => ctx.Text.Trim().Length > 0 && ctx.Text.Length <= 20000,
        Execute = (ctx, ct) => RunAsync(p, ctx, ct),
    }).ToList();

    /// <summary>"AI · " plus the gist of the prompt (template minus {text}, else the system prompt).</summary>
    private static string Describe(Prompt p)
    {
        var gist = p.Template.Replace("{text}", "").Trim();
        if (gist.Length == 0) gist = p.System.Trim();
        gist = gist.Split('\n', '（', '(', '，', ',', '。')[0].TrimEnd('：', ':');
        if (gist.Length > 36) gist = gist[..36] + "…";
        return gist.Length == 0 ? "AI 处理选中内容" : "AI · " + gist;
    }

    private async Task RunAsync(Prompt prompt, SelectionContext ctx, CancellationToken ct)
    {
        var text = ctx.Text.Trim();
        var panel = _host.ShowPanel(new PanelOptions
        {
            Title = prompt.Label, Icon = prompt.Icon, AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY, Source = text, Width = 520,
            Badge = _host.Llm.ModelNameFor(_cfg.Provider, _cfg.Model),
        });
        panel.AddButton("复制", t => _host.CopyToClipboard(t));
        if (ctx.HasSourceWindow) panel.AddButton("替换原文", t => _host.PasteText(t, ctx.SourceWindow));

        if (!_host.Llm.IsConfiguredFor(_cfg.Provider))
        {
            panel.SetText($"尚未配置 {_host.Llm.ProviderNameFor(_cfg.Provider)}。");
            panel.SetStatus("在 设置 → 服务 中填写接口与密钥");
            panel.AddButton("打开设置", _ => _host.OpenSettings());
            return;
        }

        panel.SetBusy(true);
        panel.SetStatus("生成中…");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, panel.Cancellation);
            var req = new LlmRequest
            {
                System = string.IsNullOrWhiteSpace(prompt.System) ? DefaultSystem : prompt.System,
                User = prompt.Template.Contains("{text}") ? prompt.Template.Replace("{text}", text) : prompt.Template + "\n\n" + text,
                MaxTokens = _cfg.MaxTokens,
                Provider = string.IsNullOrWhiteSpace(_cfg.Provider) ? null : _cfg.Provider,
                Model = _cfg.Model,
                // null = the 深度思考 setting of the service page; the reasoning shows above the answer.
                Thinking = _cfg.ThinkingOverride,
                Effort = string.IsNullOrWhiteSpace(_cfg.EffortOverride) ? null : _cfg.EffortOverride,
            };
            if (_cfg.Stream)
            {
                bool? wasThinking = null;
                await foreach (var chunk in _host.Llm.StreamChunksAsync(req, linked.Token))
                {
                    if (chunk.IsThinking != wasThinking) panel.SetStatus(chunk.IsThinking ? "思考中…" : "生成中…");
                    wasThinking = chunk.IsThinking;
                    if (chunk.IsThinking) panel.AppendThinking(chunk.Text);
                    else panel.AppendText(chunk.Text);
                }
            }
            else
            {
                var thinking = new System.Text.StringBuilder();
                var answer = new System.Text.StringBuilder();
                await foreach (var chunk in _host.Llm.StreamChunksAsync(req, linked.Token))
                    (chunk.IsThinking ? thinking : answer).Append(chunk.Text);
                if (thinking.Length > 0) panel.AppendThinking(thinking.ToString().Trim());
                panel.SetText(answer.ToString().Trim());
            }
            panel.SetStatus("");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            panel.SetError("请求失败：" + Errors.Describe(ex));
            _host.Log.Error("ai request failed", ex);
        }
        finally { panel.SetBusy(false); }
    }

    // ------------------------------------------------------------------ settings

    public FrameworkElement? CreateSettingsView()
    {
        // Edit a draft; it only takes effect when the settings window is saved.
        var d = SettingsDraft.Clone(_cfg);
        void Changed() => _host.Settings.Changed();
        _host.Settings.OnApply(() =>
        {
            _cfg = SettingsDraft.Clone(d);
            _host.Storage.Set(_cfg);
            _host.RefreshActions(); // prompt buttons may have been added / removed / renamed
        });
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
            foreach (var p in d.Prompts)
            {
                var prompt = p;
                list.Children.Add(Form.ListRow(prompt.Label, Summary(prompt), prompt.Enabled,
                    on => { prompt.Enabled = on; Changed(); },
                    () => { if (Edit(prompt, false)) { Changed(); Rebuild(); } },
                    () => { d.Prompts.Remove(prompt); Changed(); Rebuild(); },
                    ui, prompt.Icon));
            }
        }
        Rebuild();

        var add = Form.Button("＋ 添加提示词", () =>
        {
            var p = new Prompt { Label = "", System = DefaultSystem, Template = "{text}" };
            if (Edit(p, true)) { d.Prompts.Add(p); Changed(); Rebuild(); }
        }, accent: true);

        // Service → model → depth: the model picker and the depth levels follow the chosen service, so they are rebuilt on change.
        const string Follow = "", On = "on", Off = "off";
        var modelHost = new ContentControl();
        var effortHost = new ContentControl();
        void SyncEffortEnabled() { if (effortHost.Content is ComboBox c) c.IsEnabled = d.ThinkingOverride != false && c.Items.Count > 1; } // depth only matters while thinking
        void BuildEffort()
        {
            var levels = _host.Llm.EffortLevels(d.Provider, d.Model);
            if (d.EffortOverride.Length > 0 && !levels.Contains(d.EffortOverride, StringComparer.OrdinalIgnoreCase)) d.EffortOverride = Follow; // the old pick means nothing here
            var items = new List<(string, string)> { (Follow, "跟随「服务」页") };
            foreach (var l in levels) items.Add((l, LlmEffort.Label(l)));
            var combo = Form.Combo(items, d.EffortOverride, v => { d.EffortOverride = v; Changed(); }, 180);
            if (levels.Count == 0) combo.ToolTip = "这家服务商的模型不支持调节思考深度";
            effortHost.Content = combo;
            SyncEffortEnabled();
        }
        void BuildModel() => modelHost.Content = ui.ModelPicker(d.Provider, d.Model, v => { d.Model = v; BuildEffort(); Changed(); });
        BuildModel();
        BuildEffort();
        var provider = ui.ProviderPicker(d.Provider, v => { d.Provider = v; d.Model = ""; BuildModel(); BuildEffort(); Changed(); });
        var thinking = Form.Combo(new[] { (Follow, "跟随「服务」页"), (On, "开启"), (Off, "关闭") },
            d.ThinkingOverride switch { true => On, false => Off, null => Follow },
            v => { d.ThinkingOverride = v switch { On => true, Off => false, _ => null }; SyncEffortEnabled(); Changed(); }, 180);

        return Form.Stack(
            Form.Card(
                Form.Row("服务商", provider, "「服务」页里配置的服务商"),
                Form.Row("模型", modelHost, "初始为该服务商在「服务」页的模型；可改，清空即恢复"),
                Form.Row("深度思考", thinking, "先推理再回答，思考过程显示在答案上方，可收起；服务商不支持时无效"),
                Form.Row("思考深度", effortHost, "开启深度思考时的推理深度，档位由服务商 / 模型决定"),
                Form.Row("流式输出", Form.Toggle(d.Stream, v => { d.Stream = v; Changed(); }), "边生成边显示"),
                Form.Row("最大输出 tokens", Form.TextBox(d.MaxTokens.ToString(), 100, v => { if (int.TryParse(v, out var n) && n > 0) { d.MaxTokens = n; Changed(); } }))
            ),
            Form.Section("提示词按钮"),
            list,
            Form.Buttons(add),
            Form.Hint("每个提示词是操作条上的一个按钮。"));
    }

    private static string Summary(Prompt p)
    {
        var t = p.Template.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > 60 ? t[..60] + "…" : t;
    }

    public void Dispose() { }
}
