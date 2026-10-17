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
        LlmProxyCombo.ItemsSource = Plugins.HostUi.ProxyRoutes.Select(r => new KeyValuePair<Sdk.ProxyRoute, string>(r.Route, r.Label)).ToList();
        LlmProxyTip.Text = "发往这家服务商的请求怎么连网，不管是哪个插件调用的。" + Plugins.HostUi.ProxyRouteTip;
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
        d.Proxy = LlmProxyCombo.SelectedValue is Sdk.ProxyRoute route ? route : Sdk.ProxyRoute.Default;
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
            LlmFormCard.Visibility = Visibility.Collapsed;
            RebuildList();
            return;
        }
        Quietly(() =>
        {
            _shown = id;
            var p = ProviderOf(id);
            LlmFormCard.Visibility = Visibility.Visible;
            LlmBaseUrlBox.Text = d.BaseUrl;
            LlmKeyBox.Password = d.ApiKey;
            LlmProxyCombo.SelectedValue = d.Proxy;
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

    /// <summary>The added services as chips: logo, name, a "默认" mark, a warning while incomplete, remove; the selected one is outlined.</summary>
    private void RebuildList()
    {
        LlmServiceList.Children.Clear();
        var ids = Ctx.Llm.Providers.Select(p => p.Id).Where(_drafts.ContainsKey).ToList();
        ids.AddRange(_drafts.Keys.Where(k => ProviderOf(k) == null));
        LlmEmptyHint.Visibility = ids.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddLlmButton.IsEnabled = Ctx.Llm.Providers.Any(p => !_drafts.ContainsKey(p.Id));
        AddLlmButton.ToolTip = AddLlmButton.IsEnabled ? "添加一家模型服务商，各家的地址、密钥、模型分别保存" : "所有服务商都已添加";
        ToolTipService.SetShowOnDisabled(AddLlmButton, true);
        foreach (var id in ids) LlmServiceList.Children.Add(BuildChip(id, _drafts[id]));
    }

    private Border BuildChip(string id, LlmConfig d)
    {
        var p = ProviderOf(id);
        bool selected = string.Equals(id, _shown, StringComparison.OrdinalIgnoreCase);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new IconImage { Icon = ProviderItem.LogoOf(p), Size = 16, Brush = Res<Brush>("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = p?.Name ?? id, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        if (IsDefault(id))
        {
            var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Res<Brush>("AccentSoftBrush"), ToolTip = "插件没有另选服务商时用这一家" };
            pill.Child = new TextBlock { Text = "默认", FontSize = 11, Foreground = Res<Brush>("AccentHoverBrush") };
            row.Children.Add(pill);
        }
        // Model and host, or what is still missing, on hover; an incomplete service also shows a warning mark.
        string model = string.IsNullOrWhiteSpace(d.Model) ? (p?.DefaultModel ?? "") : d.Model;
        string host = string.IsNullOrWhiteSpace(d.BaseUrl) ? (p?.DefaultBaseUrl ?? (p?.RequiresBaseUrl == false ? "官方接口" : "")) : d.BaseUrl;
        bool missingKey = p?.RequiresApiKey == true && string.IsNullOrWhiteSpace(d.ApiKey) && string.IsNullOrWhiteSpace(d.BaseUrl);
        bool missingHost = host.Length == 0;
        string summary = p == null ? "提供这家服务的插件未加载"
            : missingHost ? "还没有填接口地址"
            : missingKey ? "还没有填 API Key"
            : (model.Length > 0 ? model : "未选模型") + " · " + host;
        if (p == null || missingKey || missingHost)
            row.Children.Add(new IconImage { Icon = "circle-alert", Size = 14, Brush = Res<Brush>("DangerBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        var del = new Button
        {
            Style = Res<Style>("IconButton"), Width = 22, Height = 22, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Content = new IconImage { Icon = "x", Size = 12, Brush = Res<Brush>("MutedBrush") }, ToolTip = "移除这家服务商（地址、密钥一起清掉；保存后生效）",
        };
        del.Click += (_, e) => { e.Handled = true; Remove(id); };
        row.Children.Add(del);

        var chip = new Border
        {
            Child = row, Style = Res<Style>("Card"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 5, 5, 5), Margin = new Thickness(0, 0, 8, 8),
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = summary,
        };
        if (selected) { chip.BorderBrush = Res<Brush>("AccentBrush"); chip.BorderThickness = new Thickness(1.5); }
        chip.MouseLeftButtonUp += (_, _) => Show(id);
        return chip;
    }

    /// <summary>"＋ 添加服务商": the providers not added yet, as a menu under the button; picking one adds an empty draft and opens its form.</summary>
    private void AddLlm_Click(object sender, RoutedEventArgs e)
    {
        ShowAddMenu(AddLlmButton, Ctx.Llm.Providers.Where(p => !_drafts.ContainsKey(p.Id))
            .Select(p => ((string?)ProviderItem.LogoOf(p), p.Name, (Action)(() => Add(p.Id)))));
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
    /// Chips under the model: how answers come back (declared by the selected protocol's provider) and what the model in
    /// the box can take or give - images, speech - rebuilt as it changes. Connection-level features are not chips:
    /// listing models enables the "获取模型" button, a system prompt is supported everywhere.
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
        // Only what is supported shows: the backend's features, the depth when this model takes levels, and what OpenRouter's
        // catalog lists for the model in the box (images, speech) - a model it does not know gets no such chip.
        // On a pick from the list SelectionChanged comes before Text follows.
        var model = ((LlmModelBox.SelectedItem as string) ?? LlmModelBox.Text ?? "").Trim();
        if (model.Length == 0) model = provider.DefaultModel ?? "";
        if (Ctx.Llm is not Core.LlmService llm) return;
        var features = llm.CapabilitiesOf(provider, model).Features;
        bool depth = false;
        try { depth = provider.EffortLevels(model).Count > 0; } catch { /* a faulty provider: no depth chip */ }
        if (!llm.Catalog.IsLoaded) LoadCatalogThenRebuild(llm);
        foreach (var (on, text, icon, tip) in new (bool, string, string, string)[]
        {
            (features.HasFlag(Sdk.LlmFeatures.Streaming), "流式输出", "zap", "回答边生成边显示"),
            (features.HasFlag(Sdk.LlmFeatures.Thinking), "思考过程", "sparkles", "可开关思考，并显示模型的推理过程"),
            (features.HasFlag(Sdk.LlmFeatures.Effort) && depth, "思考深度", "sliders", "可调思考深度，档位由服务商 / 模型决定"),
            (features.HasFlag(Sdk.LlmFeatures.Vision), "图片识别", "image", "能看懂图片（据 OpenRouter 模型目录）"),
            (features.HasFlag(Sdk.LlmFeatures.Speech), "语音合成", "volume-2", "能输出语音（据 OpenRouter 模型目录）"),
        })
        {
            if (!on) continue;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var brush = Res<Brush>("AccentHoverBrush");
            row.Children.Add(new IconImage { Icon = icon, Size = 13, Brush = brush, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = brush, Margin = new Thickness(5, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
            LlmFeatureChips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6),
                Background = Res<Brush>("AccentSoftBrush"), Child = row, ToolTip = tip,
            });
        }
        UpdateThinkingRow();
    }

    private bool _catalogLoading;

    /// <summary>First run (no cached catalog yet): fetch OpenRouter's catalog once, then redraw the chips with it.</summary>
    private async void LoadCatalogThenRebuild(Core.LlmService llm)
    {
        if (_catalogLoading) return;
        _catalogLoading = true;
        try { await llm.Catalog.EnsureFreshAsync(); }
        finally { _catalogLoading = false; }
        if (llm.Catalog.IsLoaded) BuildFeatures();
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
        if (sender == LlmModelBox || (sender is TextBox tb && tb.TemplatedParent == LlmModelBox)) BuildFeatures(); // per-model chips and depth levels
        else if (sender == LlmThinkingSw) UpdateThinkingRow();
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
