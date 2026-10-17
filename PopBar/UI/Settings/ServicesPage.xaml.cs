using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PopBar.Config;

namespace PopBar.UI;

/// <summary>
/// "服务": the model services the user added, one draft per provider (keyed by provider id), plus which one is the
/// default. The form under the list edits the selected draft. "获取模型" / "测试连接" talk to the endpoint as typed, so
/// they save this page right then (they are explicit actions); every other page keeps its pending edits.
/// </summary>
internal partial class ServicesPage : SettingsPage
{
    private readonly Dictionary<string, LlmConfig> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private string _default = "";
    /// <summary>Provider id of the draft the form shows; null = form hidden.</summary>
    private string? _shown;

    public ServicesPage()
    {
        InitializeComponent();
        // Editable ComboBox: typing does not raise SelectionChanged, so listen to the inner TextBox too.
        LlmModelBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => Llm_Changed(s, e)));
    }

    protected override void OnLoad(AppConfig c)
    {
        _drafts.Clear();
        foreach (var s in c.LlmServices) _drafts[s.Protocol] = Sdk.SettingsDraft.Clone(s);
        _default = c.LlmDefaultId;
        _shown = null;
        Show(_drafts.ContainsKey(_default) ? _default : _drafts.Keys.FirstOrDefault());
    }

    public override void Apply(AppConfig c)
    {
        c.LlmServices = ServicesFromDrafts();
        c.LlmDefault = _default;
    }

    private bool IsDefault(string? id) => string.Equals(id, _default, StringComparison.OrdinalIgnoreCase);

    private Sdk.ILlmProvider? ProviderOf(string? id) => Ctx.Llm.Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Drafts in provider registration order (stable snapshots), then any whose provider is not loaded right now (kept, not dropped).</summary>
    private List<LlmConfig> ServicesFromDrafts()
    {
        StoreFields();
        var list = new List<LlmConfig>();
        foreach (var p in Ctx.Llm.Providers)
            if (_drafts.TryGetValue(p.Id, out var d)) list.Add(Sdk.SettingsDraft.Clone(d));
        foreach (var d in _drafts.Values)
            if (ProviderOf(d.Protocol) == null) list.Add(Sdk.SettingsDraft.Clone(d));
        return list;
    }

    /// <summary>The form → the draft of the provider it shows.</summary>
    private void StoreFields()
    {
        if (_shown == null || !_drafts.TryGetValue(_shown, out var d)) return;
        d.BaseUrl = LlmBaseUrlBox.Text.Trim();
        d.ApiKey = LlmKeyBox.Password.Trim();
        d.Model = (LlmModelBox.Text ?? "").Trim();
        d.Thinking = LlmThinkingSw.IsChecked == true;
        if (LlmEffortCombo.SelectedValue is string effort) d.Effort = effort;
    }

    /// <summary>Select a service: its row highlights and the form shows its draft; null (or an unknown id) hides the form.</summary>
    private void Show(string? id)
    {
        StoreFields();
        if (id == null || !_drafts.TryGetValue(id, out var d))
        {
            _shown = null;
            LlmFormHeader.Visibility = LlmFormCard.Visibility = Visibility.Collapsed;
            RebuildList();
            return;
        }
        Quietly(() =>
        {
            _shown = id;
            var p = ProviderOf(id);
            LlmFormLogo.Icon = ProviderItem.LogoOf(p);
            LlmFormName.Text = p?.Name ?? id;
            LlmFormHeader.Visibility = LlmFormCard.Visibility = Visibility.Visible;
            LlmBaseUrlBox.Text = d.BaseUrl;
            LlmKeyBox.Password = d.ApiKey;
            if (LlmKeyPlain.Visibility == Visibility.Visible) LlmKeyPlain.Text = d.ApiKey;
            LlmModelBox.ItemsSource = d.Models.Count > 0 ? d.Models.ToList() : null; // the list fetched last time, if any
            LlmModelBox.Text = d.Model;
            LlmThinkingSw.IsChecked = d.Thinking;
            LlmDefaultSw.IsChecked = IsDefault(id);
            LlmDefaultSw.IsEnabled = !IsDefault(id); // the default cannot be switched off, only moved to another provider
            ShowResult("");
            BuildFeatures();
            UpdateThinkingRow(d.Effort);
        });
        RebuildList();
    }

    /// <summary>The list of added services: logo, name, model · host, a "默认" mark, edit / delete; the selected one is outlined.</summary>
    private void RebuildList()
    {
        LlmServiceList.Children.Clear();
        var ids = Ctx.Llm.Providers.Select(p => p.Id).Where(_drafts.ContainsKey).ToList();
        ids.AddRange(_drafts.Keys.Where(k => ProviderOf(k) == null));
        LlmEmptyHint.Visibility = ids.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddLlmButton.IsEnabled = Ctx.Llm.Providers.Any(p => !_drafts.ContainsKey(p.Id));
        AddLlmButton.ToolTip = AddLlmButton.IsEnabled ? "添加一家模型服务商，各家的地址、密钥、模型分别保存" : "所有服务商都已添加";
        ToolTipService.SetShowOnDisabled(AddLlmButton, true);
        foreach (var id in ids) LlmServiceList.Children.Add(BuildRow(id, _drafts[id]));
    }

    private Border BuildRow(string id, LlmConfig d)
    {
        var p = ProviderOf(id);
        bool selected = string.Equals(id, _shown, StringComparison.OrdinalIgnoreCase);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Background = Res<Brush>("AccentSoftBrush") };
        box.Child = new IconImage { Icon = ProviderItem.LogoOf(p), Size = 16, Brush = Res<Brush>("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(box);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = p?.Name ?? id, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        if (IsDefault(id))
        {
            var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Res<Brush>("AccentSoftBrush"), ToolTip = "插件没有另选服务商时用这一家" };
            pill.Child = new TextBlock { Text = "默认", FontSize = 11, Foreground = Res<Brush>("AccentHoverBrush") };
            title.Children.Add(pill);
        }
        text.Children.Add(title);
        // Model and host at a glance, or what is still missing: the row should say whether this one can be used yet.
        string model = string.IsNullOrWhiteSpace(d.Model) ? (p?.DefaultModel ?? "") : d.Model;
        string host = string.IsNullOrWhiteSpace(d.BaseUrl) ? (p?.DefaultBaseUrl ?? (p?.RequiresBaseUrl == false ? "官方接口" : "")) : d.BaseUrl;
        bool missingKey = p?.RequiresApiKey == true && string.IsNullOrWhiteSpace(d.ApiKey) && string.IsNullOrWhiteSpace(d.BaseUrl);
        bool missingHost = host.Length == 0;
        string sub = p == null ? "提供这家服务的插件未加载"
            : missingHost ? "还没有填接口地址"
            : missingKey ? "还没有填 API Key"
            : (model.Length > 0 ? model : "未选模型") + " · " + host;
        var subText = new TextBlock { Text = sub, Style = Res<Style>("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        if (p != null && (missingKey || missingHost)) subText.Foreground = Res<Brush>("DangerBrush");
        text.Children.Add(subText);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var edit = new Button { Style = Res<Style>("IconButton"), Content = new IconImage { Icon = "pencil", Size = 14, Brush = Res<Brush>(selected ? "AccentHoverBrush" : "MutedBrush") }, ToolTip = "编辑" };
        edit.Click += (_, _) => Show(id);
        buttons.Children.Add(edit);
        var del = new Button { Style = Res<Style>("IconButton"), Content = new IconImage { Icon = "trash", Size = 14, Brush = Res<Brush>("DangerBrush") }, ToolTip = "移除这家服务商（地址、密钥一起清掉；保存后生效）" };
        del.Click += (_, _) => Remove(id);
        buttons.Children.Add(del);
        Grid.SetColumn(buttons, 2);
        g.Children.Add(buttons);

        var row = new Border { Child = g, Style = Res<Style>("Card"), Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8), Cursor = System.Windows.Input.Cursors.Hand };
        if (selected) { row.BorderBrush = Res<Brush>("AccentBrush"); row.BorderThickness = new Thickness(1.5); }
        row.MouseLeftButtonUp += (_, _) => Show(id);
        return row;
    }

    /// <summary>"＋ 添加服务商": the providers not added yet, as a menu under the button; picking one adds an empty draft and opens its form.</summary>
    private void AddLlm_Click(object sender, RoutedEventArgs e)
    {
        ShowAddMenu(AddLlmButton, Ctx.Llm.Providers.Where(p => !_drafts.ContainsKey(p.Id))
            .Select(p => (ProviderItem.LogoOf(p), p.Name, p.Hint, (Action)(() => Add(p.Id)))));
    }

    private void Add(string id)
    {
        if (_drafts.ContainsKey(id)) { Show(id); return; }
        _drafts[id] = new LlmConfig { Protocol = id };
        if (!_drafts.ContainsKey(_default)) _default = id; // the first service added is the default until another is chosen
        Show(id);
        RaiseEdited();
    }

    private void Remove(string id)
    {
        if (!_drafts.Remove(id)) return;
        if (IsDefault(id)) _default = Ctx.Llm.Providers.Select(p => p.Id).FirstOrDefault(_drafts.ContainsKey) ?? _drafts.Keys.FirstOrDefault() ?? "";
        if (string.Equals(id, _shown, StringComparison.OrdinalIgnoreCase)) _shown = null;
        Show(_shown ?? (_drafts.ContainsKey(_default) ? _default : _drafts.Keys.FirstOrDefault()));
        RaiseEdited();
    }

    private void LlmDefault_Changed(object sender, RoutedEventArgs e)
    {
        if (Loading || _shown == null || LlmDefaultSw.IsChecked != true) return;
        _default = _shown;
        LlmDefaultSw.IsEnabled = false;
        RebuildList();
        RaiseEdited();
    }

    /// <summary>
    /// Chips under the model: how answers come back (declared by the selected protocol's provider). Connection-level
    /// features are not chips: listing models enables the "获取模型" button, a system prompt is supported everywhere.
    /// </summary>
    private void BuildFeatures()
    {
        LlmFeatureChips.Children.Clear();
        var provider = ProviderOf(_shown);
        if (provider == null) return;
        bool canList = provider.Features.HasFlag(Sdk.LlmFeatures.ModelList);
        FetchModelsButton.IsEnabled = canList;
        FetchModelsButton.ToolTip = canList ? "从接口拉取可用模型" : "此协议不支持拉取模型列表，请手动填写";
        ToolTipService.SetShowOnDisabled(FetchModelsButton, true);
        foreach (var (flag, text, icon, tip) in new (Sdk.LlmFeatures, string, string, string)[]
        {
            (Sdk.LlmFeatures.Streaming, "流式输出", "zap", "回答边生成边显示"),
            (Sdk.LlmFeatures.Thinking, "思考过程", "sparkles", "可开关思考，并显示模型的推理过程"),
            (Sdk.LlmFeatures.Effort, "思考深度", "sliders", "可调思考深度，档位由服务商 / 模型决定"),
        })
        {
            bool on = provider.Features.HasFlag(flag);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var brush = Res<Brush>(on ? "AccentHoverBrush" : "MutedBrush");
            row.Children.Add(new IconImage { Icon = on ? icon : "x", Size = 13, Brush = brush, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = brush, Margin = new Thickness(5, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
            LlmFeatureChips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6),
                Background = Res<Brush>(on ? "AccentSoftBrush" : "HoverBrush"), Child = row,
                ToolTip = on ? tip : "此协议不支持：" + tip,
            });
        }
        UpdateThinkingRow();
    }

    /// <summary>
    /// The 深度思考 row: only for providers that can switch thinking; the depth only while it is on, with the levels the
    /// provider takes for the model in the box (<paramref name="wanted"/>, else the current pick, snapped to them).
    /// </summary>
    private void UpdateThinkingRow(string? wanted = null)
    {
        var provider = ProviderOf(_shown);
        bool can = provider?.Features.HasFlag(Sdk.LlmFeatures.Thinking) == true;
        LlmThinkingRow.Visibility = LlmThinkingSep.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        var model = (LlmModelBox.Text ?? "").Trim();
        if (model.Length == 0) model = provider?.DefaultModel ?? "";
        IReadOnlyList<string> levels = Array.Empty<string>();
        if (provider != null) { try { levels = provider.EffortLevels(model); } catch (Exception ex) { Log.Warn($"effort levels of {provider.Id}: {ex.Message}"); } }
        LlmEffortCombo.Visibility = levels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        wanted ??= LlmEffortCombo.SelectedValue as string ?? Sdk.LlmEffort.Medium;
        var items = levels.Select(l => new KeyValuePair<string, string>(l, Sdk.LlmEffort.Label(l))).ToList();
        if (LlmEffortCombo.ItemsSource is not List<KeyValuePair<string, string>> old || !old.SequenceEqual(items))
            Quietly(() => LlmEffortCombo.ItemsSource = items); // the list swap itself is not an edit
        if (levels.Count > 0) LlmEffortCombo.SelectedValue = Sdk.LlmEffort.Nearest(wanted, levels);
        LlmEffortCombo.IsEnabled = LlmThinkingSw.IsChecked == true;
    }

    /// <summary>Show / hide the API key. The PasswordBox stays the source of truth; the TextBox mirrors it while shown.</summary>
    private void LlmKeyEye_Click(object sender, RoutedEventArgs e)
    {
        bool show = LlmKeyPlain.Visibility != Visibility.Visible;
        if (show) LlmKeyPlain.Text = LlmKeyBox.Password;
        LlmKeyPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LlmKeyBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        LlmKeyEyeIcon.Icon = show ? "eye-off" : "eye";
        LlmKeyEye.ToolTip = show ? "隐藏 API Key" : "显示 API Key";
    }

    private void LlmKeyPlain_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LlmKeyPlain.Visibility == Visibility.Visible && LlmKeyBox.Password != LlmKeyPlain.Text)
            LlmKeyBox.Password = LlmKeyPlain.Text; // raises PasswordChanged -> dirty tracking as usual
    }

    private void Llm_Changed(object sender, RoutedEventArgs e)
    {
        if (Loading || Ctx == null) return; // also fired while InitializeComponent builds the form
        // The effort levels may differ per model, and only show while thinking is on.
        if (sender == LlmThinkingSw || sender == LlmModelBox || (sender is TextBox tb && tb.TemplatedParent == LlmModelBox)) UpdateThinkingRow();
        RaiseEdited();
    }

    /// <summary>Write this page to the store now (before talking to the endpoint) and count it as saved.</summary>
    private void SaveNow()
    {
        var services = ServicesFromDrafts();
        var def = _default;
        Ctx.Store.Update(c => { c.LlmServices = services; c.LlmDefault = def; });
        MarkSaved();
        RaiseEdited();
    }

    /// <summary>Progress in grey, success in green, failure in red with an icon.</summary>
    private void ShowResult(string text, bool? ok = null)
    {
        LlmTestResult.Inlines.Clear();
        if (ok == false)
        {
            LlmTestResult.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new IconImage
            {
                Icon = "circle-alert", Size = 14, Brush = Res<Brush>("DangerBrush"), Margin = new Thickness(0, 0, 6, -2),
            }) { BaselineAlignment = BaselineAlignment.Center });
        }
        LlmTestResult.Inlines.Add(text);
        LlmTestResult.SetResourceReference(TextBlock.ForegroundProperty, ok == null ? "MutedBrush" : ok == false ? "DangerBrush" : "SuccessBrush");
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        SaveNow();
        var current = (LlmModelBox.Text ?? "").Trim();
        var shown = _shown;
        ShowResult("获取模型列表…");
        try
        {
            var models = await Ctx.Llm.ListModelsAsync(shown);
            // Keep the list with the service (the draft, and config right away) so it is there next time without fetching.
            if (models.Count > 0 && shown != null && _drafts.TryGetValue(shown, out var draft)) { draft.Models = models.ToList(); SaveNow(); }
            if (shown != _shown) return; // the user moved on to another service meanwhile
            Quietly(() => { LlmModelBox.ItemsSource = models; LlmModelBox.Text = current; });
            ShowResult(models.Count == 0 ? "接口没有返回任何模型" : $"获取到 {models.Count} 个模型，从下拉框选择", ok: models.Count > 0);
            if (models.Count > 0) LlmModelBox.IsDropDownOpen = true;
        }
        catch (Exception ex)
        {
            ShowResult("获取失败：" + ex.Message, ok: false);
        }
    }

    private async void LlmTest_Click(object sender, RoutedEventArgs e)
    {
        SaveNow();
        ShowResult("连接中…");
        try
        {
            var reply = await Ctx.Llm.CompleteAsync(new Sdk.LlmRequest { Provider = _shown, User = "回复“OK”两个字母即可。", MaxTokens = 512, Effort = Sdk.LlmEffort.Low, Thinking = false }); // room for reasoning models that think before answering
            var text = reply.Trim().Replace("\n", " ");
            ShowResult(text.Length > 0 ? "连接成功：" + text : "连接成功，但模型没有返回文字（可能是思考型模型输出被截断）", ok: true);
        }
        catch (Exception ex)
        {
            ShowResult("连接失败：" + ex.Message, ok: false);
        }
    }
}
