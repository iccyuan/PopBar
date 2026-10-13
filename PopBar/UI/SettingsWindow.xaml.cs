using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PopBar.Actions;
using PopBar.Config;
using PopBar.Core;

namespace PopBar.UI;

/// <summary>Several actions contributed by one plugin, shown as one card so their relation is visible.</summary>
public sealed class ActionGroupRow : INotifyPropertyChanged
{
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public required string Icon { get; init; }
    public string Note { get; set; } = "";
    public ObservableCollection<ActionRow> Members { get; } = new();
    /// <summary>Expanded: how many actions; collapsed: how many of them are on, since the switches are hidden.</summary>
    public string CountText => IsExpanded ? $"{Members.Count} 个动作" : $"已开启 {Members.Count(m => m.Enabled)}/{Members.Count}";

    /// <summary>Groups start folded; the ones the user opened are kept for the app's lifetime (reopening settings keeps them).</summary>
    private static readonly HashSet<string> Expanded = new(StringComparer.OrdinalIgnoreCase);

    public bool IsExpanded
    {
        get => Expanded.Contains(Owner);
        set
        {
            if (value) Expanded.Add(Owner); else Expanded.Remove(Owner);
            Notify(nameof(IsExpanded)); Notify(nameof(ChevronIcon)); Notify(nameof(CountText));
        }
    }
    public string ChevronIcon => IsExpanded ? "chevron-down" : "chevron-right";

    public void Add(ActionRow row)
    {
        Members.Add(row);
        // A member switch flipping changes the collapsed summary.
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ActionRow.Enabled)) Notify(nameof(CountText)); };
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _isDragging;
    public bool IsDragging { get => _isDragging; set { _isDragging = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DragOpacity))); } }
    public double DragOpacity => _isDragging ? 0.45 : 1.0;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Row view-model for the actions list.</summary>
public sealed class ActionRow : INotifyPropertyChanged
{
    public ActionDefinition Def { get; }
    public ActionRow(ActionDefinition def) => Def = def;

    /// <summary>Resolves a plugin action ref ("sendto:notepad") to the plugin's own description, if any.</summary>
    internal static Func<string, string?>? PluginDescription;

    public string Label => string.IsNullOrWhiteSpace(Def.Label) ? "(未命名)" : Def.Label;
    public Geometry IconGeometry => Icons.Get(Def.Icon);
    /// <summary>Plugin actions are configured inside their plugin; the generic editor does not apply.</summary>
    public Visibility EditVisibility => Def.IsPlugin ? Visibility.Collapsed : Visibility.Visible;
    public bool Enabled
    {
        get => Def.Enabled;
        set { Def.Enabled = value; OnChanged(nameof(Enabled)); }
    }
    private bool _isDragging;
    /// <summary>The row being dragged is dimmed so the drop slot is obvious.</summary>
    public bool IsDragging { get => _isDragging; set { _isDragging = value; OnChanged(nameof(DragOpacity)); } }
    public double DragOpacity => _isDragging ? 0.45 : 1.0;
    public string Summary
    {
        get
        {
            // "Open" covers both links and addresses; say which one this row handles.
            if (Def.IsPlugin && PluginDescription?.Invoke(Def.Ref ?? "") is { Length: > 0 } pd) return pd;
            var desc = (Def.Type, Def.Match) switch
            {
                (ActionTypes.Open, MatchKinds.Url) => "在浏览器中打开选中的链接",
                (ActionTypes.Open, MatchKinds.Email) => "用默认邮件程序给选中的地址写邮件",
                (ActionTypes.Url, _) when Uri.TryCreate(Def.Url?.Replace("{text}", "x"), UriKind.Absolute, out var u) && u.Host.Length > 0
                    => "在 " + u.Host + " 中打开选中内容",
                _ => ActionTypes.Describe(Def.Type),
            };
            var detail = Def.Type switch
            {
                ActionTypes.Plugin => Def.Ref,
                ActionTypes.Url when !desc.StartsWith("在 ") => Def.Url,
                ActionTypes.Transform => Def.Op + (string.IsNullOrEmpty(Def.Template) ? "" : "  " + Def.Template),
                ActionTypes.Command => Def.Command + " " + Def.Args,
                _ => null,
            };
            var cond = Def.Type == ActionTypes.Open ? "" : Def.Match switch
            {
                MatchKinds.Url => "  · 仅链接",
                MatchKinds.Email => "  · 仅邮箱",
                MatchKinds.Regex => "  · 正则 " + Def.Regex,
                _ => "",
            };
            return desc + (string.IsNullOrWhiteSpace(detail) ? "" : "  —  " + detail) + cond;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        OnChanged(nameof(Label)); OnChanged(nameof(IconGeometry)); OnChanged(nameof(Summary)); OnChanged(nameof(Enabled));
    }
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly Plugins.PluginManager _plugins;
    private readonly Sdk.ILlmService _llm;
    /// <summary>Flat action order, as saved to config.</summary>
    private readonly ObservableCollection<ActionRow> _rows = new();
    /// <summary>What the list shows: stand-alone rows and plugin groups (rebuilt from <see cref="_rows"/>).</summary>
    private readonly ObservableCollection<object> _items = new();
    private bool _flattening;
    /// <summary>Form state as last loaded / saved (serialized); the form is dirty when it differs.</summary>
    private string _baseline = "";
    private readonly HashSet<string> _disabledPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _excluded = new();
    private readonly List<string> _noClip = new();
    /// <summary>Draft of <see cref="AppConfig.TrayMenuItems"/>; null = never customised.</summary>
    private List<string>? _trayMenu;
    private bool _loading;

    private sealed record AppPick(string Exe, string Label);

    internal SettingsWindow(ConfigStore store, Plugins.PluginManager plugins, Sdk.ILlmService llm)
    {
        _store = store;
        _plugins = plugins;
        _llm = llm;
        ActionRow.PluginDescription = id => _plugins.TryGet(id, out _, out var a) ? a.Description : null;
        _rows.CollectionChanged += (_, _) => { if (!_flattening) Regroup(); };
        InitializeComponent();

        LlmProtocolCombo.ItemsSource = _llm.Providers.Select(p => new KeyValuePair<string, string>(p.Id, p.Name)).ToList();
        PluginDirText.Text = "第三方插件目录：" + Plugins.PluginStorage.PluginsDir;
        // Editable ComboBox: typing does not raise SelectionChanged, so listen to the inner TextBox too.
        LlmModelBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => Llm_Changed(s, e)));
        BuildPluginList();

        FallbackCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("ibeam", "文本光标 / 双击时（推荐）"),
            new("always", "总是"),
            new("off", "关闭"),
        };
        ShowModeCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("key", "按键后显示"),
            new("auto", "选中后自动显示"),
        };
        // Built-in choices first, then whatever the loaded plugins put in the tray menu (same ids as `PopBar.exe --run <id>`).
        var trayChoices = new List<KeyValuePair<string, string>> { new("settings", "打开设置"), new("menu", "打开托盘菜单") };
        if (TrayItems != null) trayChoices.AddRange(TrayItems().Select(i => new KeyValuePair<string, string>(i.Id, i.Label)));
        TrayClickCombo.ItemsSource = trayChoices;
        ShowKeyBox.GestureChanged += g => { _showKey = g; Changed(ShowKeyBox, new RoutedEventArgs()); };
        ThemeCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("system", "跟随系统"),
            new("dark", "深色"),
            new("light", "浅色"),
        };
        MaxItemsCombo.ItemsSource = new[] { 4, 5, 6, 7, 8, 10, 12 }.Select(n => new KeyValuePair<string, string>(n.ToString(), $"{n} 个")).Append(new("0", "不限制")).ToList();
        SourceInitialized += (_, _) => Acrylic.Apply(this, BackdropKind.Acrylic, ThemeManager.IsDark, roundCorners: true);
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        ActionsList.ItemsSource = _items;

        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        var vs = "v" + (ver == null ? "0.1.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}");
        VersionText.Text = vs;
        AboutVersion.Text = vs + "  ·  .NET " + Environment.Version;
        ConfigPathRun.Text = ConfigStore.FilePath;
        LogPathRun.Text = System.IO.Path.Combine(ConfigStore.Dir, "popbar.log");

        LoadFromConfig(_store.Current);
        if (PluginDrafts != null)
        {
            PluginDrafts.Changed += UpdateSaveBar;
            Closed += (_, _) => { PluginDrafts.Changed -= UpdateSaveBar; PluginDrafts.DiscardAll(); };
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                if (IsDirty) SaveNow();
                e.Handled = true;
            }
        };

        // A plugin added / removed actions (e.g. a new send-to target): show them on the actions page right away.
        _plugins.ActionsSynced += OnPluginActionsSynced;
        Closed += (_, _) => _plugins.ActionsSynced -= OnPluginActionsSynced;
    }

    private void OnPluginActionsSynced() => Dispatcher.InvokeAsync(() =>
    {
        bool dirty = IsDirty;
        _loading = true;
        try
        {
            var stored = _store.Current.Actions;
            if (!dirty)
            {
                _rows.Clear();
                foreach (var a in stored) _rows.Add(new ActionRow(a.Clone()));
            }
            else
            {
                // Unsaved edits stay; only bring in what the plugin itself added, removed or renamed.
                var byRef = stored.Where(a => a.IsPlugin && a.Ref != null).ToDictionary(a => a.Ref!, StringComparer.OrdinalIgnoreCase);
                foreach (var r in _rows.Where(r => r.Def.IsPlugin && (r.Def.Ref == null || !byRef.ContainsKey(r.Def.Ref))).ToList()) _rows.Remove(r);
                for (int i = 0; i < stored.Count; i++)
                {
                    var a = stored[i];
                    if (!a.IsPlugin) continue;
                    var row = _rows.FirstOrDefault(r => string.Equals(r.Def.Ref, a.Ref, StringComparison.OrdinalIgnoreCase));
                    if (row == null) _rows.Insert(Math.Min(i, _rows.Count), new ActionRow(a.Clone()));
                    else if (row.Def.Label != a.Label || row.Def.Icon != a.Icon) { row.Def.Label = a.Label; row.Def.Icon = a.Icon; row.Refresh(); }
                }
            }
        }
        finally { _loading = false; }
        // The plugin already wrote these to config: they are part of the saved state, not unsaved edits.
        if (!dirty) _baseline = Snapshot(); else UpdateSaveBar();
    });

    // ------------------------------------------------------------------ load / save

    private void LoadFromConfig(AppConfig c)
    {
        _loading = true;
        try
        {
            EnabledSw.IsChecked = c.Enabled;
            AutoStartSw.IsChecked = c.AutoStart;
            TrayClickCombo.SelectedValue = c.TrayClick;
            if (TrayClickCombo.SelectedIndex < 0) TrayClickCombo.SelectedIndex = 0;
            ShowModeCombo.SelectedValue = c.ShowMode;
            if (ShowModeCombo.SelectedIndex < 0) ShowModeCombo.SelectedIndex = 0;
            _showKey = string.IsNullOrWhiteSpace(c.ShowKey) ? "ctrl" : c.ShowKey;
            ShowKeyBox.Gesture = _showKey;
            ShowKeyBox.IsEnabled = c.ShowMode == "key";
            DragSw.IsChecked = c.TriggerOnDrag;
            DblSw.IsChecked = c.TriggerOnDoubleClick;
            ShiftSw.IsChecked = c.TriggerOnShiftClick;
            FallbackCombo.SelectedValue = c.ClipboardFallback;
            if (FallbackCombo.SelectedIndex < 0) FallbackCombo.SelectedIndex = 0;
            DelayBox.Text = c.ShowDelayMs.ToString();
            MaxLenBox.Text = c.MaxTextLength.ToString();
            ThemeCombo.SelectedValue = c.Theme;
            if (ThemeCombo.SelectedIndex < 0) ThemeCombo.SelectedIndex = 0;
            LabelsSw.IsChecked = c.ShowLabels;
            MaxItemsCombo.SelectedValue = c.MaxBarItems.ToString();
            if (MaxItemsCombo.SelectedIndex < 0) MaxItemsCombo.SelectedValue = "7";
            BlurSlider.Value = Math.Clamp(c.BlurOpacity, 40, 100);
            BlurValueText.Text = $"{(int)BlurSlider.Value}%";
            _excluded.Clear(); _excluded.AddRange(c.ExcludedApps);
            _noClip.Clear(); _noClip.AddRange(c.NoClipboardApps);
            RenderAppLists();
            _trayMenu = c.TrayMenuItems?.ToList();
            UpdateTrayMenuSummary();

            LlmProtocolCombo.SelectedValue = c.Llm.Protocol;
            if (LlmProtocolCombo.SelectedIndex < 0) LlmProtocolCombo.SelectedIndex = 0;
            LlmBaseUrlBox.Text = c.Llm.BaseUrl;
            LlmKeyBox.Password = c.Llm.ApiKey;
            if (LlmKeyPlain.Visibility == Visibility.Visible) LlmKeyPlain.Text = c.Llm.ApiKey;
            LlmModelBox.Text = c.Llm.Model;
            _disabledPlugins.Clear();
            foreach (var id in c.DisabledPlugins) _disabledPlugins.Add(id);

            _rows.Clear();
            foreach (var a in c.Actions) _rows.Add(new ActionRow(a.Clone()));
        }
        finally { _loading = false; }
        _baseline = Snapshot();
        UpdateSaveBar();
    }

    private AppConfig BuildConfig()
    {
        var c = new AppConfig
        {
            Enabled = EnabledSw.IsChecked == true,
            AutoStart = AutoStartSw.IsChecked == true,
            TrayClick = TrayClickCombo.SelectedValue as string ?? "settings",
            ShowMode = ShowModeCombo.SelectedValue as string ?? "key",
            ShowKey = _showKey,
            TriggerOnDrag = DragSw.IsChecked == true,
            TriggerOnDoubleClick = DblSw.IsChecked == true,
            TriggerOnShiftClick = ShiftSw.IsChecked == true,
            ClipboardFallback = FallbackCombo.SelectedValue as string ?? "ibeam",
            ShowDelayMs = ParseInt(DelayBox.Text, 60, 0, 2000),
            MaxTextLength = ParseInt(MaxLenBox.Text, 10000, 1, 1_000_000),
            Theme = ThemeCombo.SelectedValue as string ?? "system",
            ShowLabels = LabelsSw.IsChecked == true,
            MaxBarItems = int.TryParse(MaxItemsCombo.SelectedValue as string, out var mx) ? mx : 7,
            BlurOpacity = (int)BlurSlider.Value,
            ExcludedApps = _excluded.ToList(),
            NoClipboardApps = _noClip.ToList(),
            TrayMenuItems = _trayMenu?.ToList(),
            Actions = _rows.Select(r => r.Def.Clone()).ToList(),
            DisabledPlugins = _disabledPlugins.ToList(),
            Llm = new LlmConfig
            {
                Protocol = LlmProtocolCombo.SelectedValue as string ?? Core.Llm.LlmProviderIds.Anthropic,
                BaseUrl = LlmBaseUrlBox.Text.Trim(),
                ApiKey = LlmKeyBox.Password.Trim(),
                Model = (LlmModelBox.Text ?? "").Trim(),
            },
        };
        return c;
    }

    private static int ParseInt(string s, int def, int min, int max) =>
        int.TryParse(s.Trim(), out var v) ? Math.Clamp(v, min, max) : def;

    private static List<string> Lines(string s) =>
        s.Split('\n').Select(l => l.Trim().TrimEnd('\r')).Where(l => l.Length > 0).ToList();

    private void Changed(object sender, RoutedEventArgs e)
    {
        // Controls raise change events while InitializeComponent runs (e.g. Slider coercing its value), before the form is loaded.
        if (_loading || _baseline.Length == 0) return;
        UpdateSaveBar();
    }

    private string Snapshot() => System.Text.Json.JsonSerializer.Serialize(BuildConfig());

    /// <summary>Edits are held in the form until saved; changing something back clears the flag again.</summary>
    private bool IsDirty => (_baseline.Length > 0 && Snapshot() != _baseline) || PluginDrafts?.AnyPending == true;

    private void UpdateSaveBar()
    {
        if (SaveBar == null) return;
        bool dirty = IsDirty;
        SaveBar.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        // Room under the last card only while the bar floats over it.
        ContentRoot.Margin = new Thickness(32, 12, 32, dirty ? 96 : 32);
    }

    private void SaveNow()
    {
        if (_loading) return;
        _store.Replace(BuildConfig());
        _baseline = Snapshot();
        PluginDrafts?.ApplyAll(); // plugin settings edited in their cards
        UpdateSaveBar();
        StatusText.Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void SaveChanges_Click(object sender, RoutedEventArgs e) => SaveNow();

    private void CancelChanges_Click(object sender, RoutedEventArgs e)
    {
        PluginDrafts?.DiscardAll();
        LoadFromConfig(_store.Current);
        BuildPluginList(); // plugin switches and settings views start again from the live settings
        StatusText.Text = "已撤销未保存的更改";
    }

    /// <summary>Called by the tray when the enabled flag flips elsewhere.</summary>
    public void RefreshFromStore()
    {
        var c = _store.Current;
        bool dirty = IsDirty;
        _loading = true;
        EnabledSw.IsChecked = c.Enabled;
        _loading = false;
        // Toggled from the tray, i.e. already saved: not an unsaved edit of this form.
        if (!dirty) _baseline = Snapshot(); else UpdateSaveBar();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsDirty)
        {
            var answer = MessageBox.Show(this, "有未保存的更改，关闭前保存吗？", "PopBar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (answer == MessageBoxResult.Yes) SaveNow();
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ nav

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelGeneral == null) return;
        PanelGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelActions.Visibility = NavActions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelApps.Visibility = NavApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelPlugins.Visibility = NavPlugins.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelServices.Visibility = NavServices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ plugins

    /// <summary>Set by the app: plugin settings drafts waiting for Save.</summary>
    internal static Plugins.SettingsHub? PluginDrafts;
    /// <summary>Tray menu entries registered by plugins, offered as choices for the tray left-click action.</summary>
    internal static Func<List<(string Id, string Label, string? Icon)>>? TrayItems;

    /// <summary>Chips under the AI service: what the selected protocol supports (declared by its provider).</summary>
    private void BuildLlmFeatures()
    {
        if (LlmFeatureChips == null) return;
        LlmFeatureChips.Children.Clear();
        var id = LlmProtocolCombo.SelectedValue as string;
        var provider = _llm.Providers.FirstOrDefault(p => p.Id == id);
        if (provider == null) return;
        foreach (var (flag, text, icon, tip) in new (Sdk.LlmFeatures, string, string, string)[]
        {
            (Sdk.LlmFeatures.Streaming, "流式输出", "zap", "回答边生成边显示"),
            (Sdk.LlmFeatures.SystemPrompt, "系统提示词", "message-square", "支持为请求设置系统提示（角色、口吻）"),
            (Sdk.LlmFeatures.Effort, "思考深度", "sliders", "支持 low / medium / high 推理深度"),
            (Sdk.LlmFeatures.ModelList, "获取模型列表", "search", "可从接口拉取可用模型"),
            (Sdk.LlmFeatures.Thinking, "思考过程", "sparkles", "可开关思考，并显示模型的推理过程"),
        })
        {
            bool on = provider.Features.HasFlag(flag);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var brush = (Brush)FindResource(on ? "AccentHoverBrush" : "MutedBrush");
            row.Children.Add(new IconImage { Icon = on ? icon : "x", Size = 13, Brush = brush, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = brush, Margin = new Thickness(5, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
            LlmFeatureChips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6),
                Background = (Brush)FindResource(on ? "AccentSoftBrush" : "HoverBrush"), Child = row,
                ToolTip = on ? tip : "此协议不支持：" + tip,
            });
        }
    }

    public void ShowPluginsPage(string? pluginId = null)
    {
        NavPlugins.IsChecked = true;
    }

    private void BuildPluginList()
    {
        PluginList.Children.Clear();
        foreach (var l in _plugins.Plugins)
        {
            var plugin = l.Plugin;
            var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 0, 10) };
            var stack = new StackPanel();
            card.Child = stack;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var iconName = PluginIcon(plugin.Id);
            var iconImg = new IconImage { Icon = iconName, Size = 18, Brush = (Brush)FindResource("AccentHoverBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var iconBox = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Background = (Brush)FindResource("AccentSoftBrush"), Child = iconImg, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 14, 0) };
            head.Children.Add(iconBox);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(info, 1);
            var title = new TextBlock { FontSize = 14 }; // YaHei UI has no semibold: SemiBold would render as heavy Bold
            title.Inlines.Add(new System.Windows.Documents.Run(plugin.Name));
            title.Inlines.Add(new System.Windows.Documents.Run($"  v{plugin.Version}" + (l.BuiltIn ? "  · 内置" : "  · 第三方")) { Foreground = (Brush)FindResource("MutedBrush"), FontWeight = FontWeights.Normal, FontSize = 12 });
            info.Children.Add(title);
            var desc = new TextBlock { Text = l.Error != null ? "加载失败：" + l.Error : plugin.Description, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            if (l.Error != null) desc.Foreground = (Brush)FindResource("DangerBrush");
            info.Children.Add(desc);
            head.Children.Add(info);
            var toggle = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = !_disabledPlugins.Contains(plugin.Id), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            toggle.Checked += (s, e) => { _disabledPlugins.Remove(plugin.Id); Changed(s, e); };
            toggle.Unchecked += (s, e) => { _disabledPlugins.Add(plugin.Id); Changed(s, e); };
            Grid.SetColumn(toggle, 3);
            head.Children.Add(toggle);
            stack.Children.Add(head);

            FrameworkElement? view = null;
            if (l.Error == null)
            {
                try { view = plugin.CreateSettingsView(); }
                catch (Exception ex) { Log.Error($"plugin {plugin.Id} settings view", ex); }
            }
            if (view != null)
            {
                // Gear button in the header (left of the switch) toggles an in-card settings area.
                var muted = (Brush)FindResource("MutedBrush");
                var accent = (Brush)FindResource("AccentHoverBrush");
                var gearIcon = new IconImage { Icon = "settings", Size = 16, Brush = muted };
                var gear = new Button { Style = (Style)FindResource("IconButton"), Width = 32, Height = 32, Content = gearIcon, ToolTip = "设置", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
                Grid.SetColumn(gear, 2);
                head.Children.Add(gear);

                var body = new StackPanel { Visibility = Visibility.Collapsed };
                body.Children.Add(new System.Windows.Shapes.Rectangle { Height = 1, Fill = (Brush)FindResource("BorderBrush"), Margin = new Thickness(0, 12, 0, 12) });
                body.Children.Add(view);
                stack.Children.Add(body);

                gear.Click += (_, _) =>
                {
                    bool open = body.Visibility != Visibility.Visible;
                    body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                    gearIcon.Brush = open ? accent : muted;
                    gear.Background = open ? (Brush)FindResource("AccentSoftBrush") : Brushes.Transparent;
                };
            }
            PluginList.Children.Add(card);
        }
    }

    private void OnThemeChanged(string effective) => Dispatcher.InvokeAsync(() => CrossFadeTheme(ThemeManager.IsDark));

    /// <summary>
    /// The Mica backdrop flips the instant the dark flag changes, while the palette brushes fade over
    /// <see cref="ThemeManager.FadeDuration"/>: the page would jump before its content. Hide the backdrop behind a
    /// solid cover that fades from the old page colour to the new one, then lift the cover to reveal the new Mica.
    /// </summary>
    private void CrossFadeTheme(bool dark)
    {
        var from = ThemeManager.PageColor(!dark);
        var to = ThemeManager.PageColor(dark);
        var cover = new SolidColorBrush(from);
        Background = cover;
        Acrylic.SetDark(this, dark);
        var fade = new System.Windows.Media.Animation.ColorAnimation(to, ThemeManager.FadeDuration) { EasingFunction = new System.Windows.Media.Animation.SineEase() };
        fade.Completed += (_, _) =>
        {
            var lift = new System.Windows.Media.Animation.ColorAnimation(Color.FromArgb(0, to.R, to.G, to.B), TimeSpan.FromMilliseconds(240));
            lift.Completed += (_, _) => SetResourceReference(BackgroundProperty, "WindowBgBrush");
            cover.BeginAnimation(SolidColorBrush.ColorProperty, lift);
        };
        cover.BeginAnimation(SolidColorBrush.ColorProperty, fade);
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/iccyuan/PopBar") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open repo", ex); }
    }

    // ------------------------------------------------------------------ tray menu

    /// <summary>Width of the theme's ScrollBar (Themes/Modern.xaml) plus a hair of gap.</summary>
    private const double ScrollBarRoom = 14;

    private List<(string Id, string Label, string? Icon)> AllTrayItems() => TrayItems?.Invoke() ?? new();

    /// <summary>The ids the menu shows now: the draft, or by default the first ones in registration order.</summary>
    private List<string> ShownTrayIds()
    {
        var all = AllTrayItems();
        var ids = _trayMenu ?? all.Select(i => i.Id).ToList();
        return ids.Where(id => all.Any(i => i.Id == id)).Take(AppConfig.MaxTrayMenuItems).ToList();
    }

    private void UpdateTrayMenuSummary()
    {
        TrayMenuSummary.Text = $"已选 {ShownTrayIds().Count} / {AppConfig.MaxTrayMenuItems}";
        TrayMenuTip.Text = $"插件提供的入口，最多 {AppConfig.MaxTrayMenuItems} 项；启用 / 暂停、设置、退出始终显示";
    }

    /// <summary>One entry of the tray-menu editor; its controls are built once and only updated afterwards.</summary>
    private sealed class TrayMenuRow
    {
        public required (string Id, string Label, string? Icon) Item { get; init; }
        public bool On { get; set; }
        public required Border Card { get; init; }
        public required TextBlock Label { get; init; }
        public required CheckBox Switch { get; init; }
        public required Button Up { get; init; }
        public required Button Down { get; init; }
    }

    /// <summary>
    /// Pick and order the menu's plugin entries: a switch per entry, up / down to reorder, capped at the limit. Rows are
    /// not rebuilt on a change (that replayed every switch's animation): toggles update state, moves shift one card.
    /// </summary>
    private void TrayMenu_Click(object sender, RoutedEventArgs e)
    {
        var all = AllTrayItems();
        var shown = ShownTrayIds();
        var list = new StackPanel();
        var counter = new TextBlock { Style = (Style)FindResource("Muted"), Margin = new Thickness(2, 4, 0, 10) };
        var rows = new List<TrayMenuRow>();

        void Refresh()
        {
            int on = rows.Count(r => r.On);
            bool full = on >= AppConfig.MaxTrayMenuItems;
            counter.Text = full
                ? $"已选 {on} / {AppConfig.MaxTrayMenuItems}，已达上限：先关掉一项再开别的"
                : $"已选 {on} / {AppConfig.MaxTrayMenuItems}";
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                r.Switch.IsEnabled = r.On || !full;
                r.Label.Opacity = r.On ? 1 : 0.6;
                r.Up.IsEnabled = i > 0;
                r.Down.IsEnabled = i < rows.Count - 1;
            }
        }

        void Move(TrayMenuRow row, int by)
        {
            int from = rows.IndexOf(row), to = from + by;
            if (to < 0 || to >= rows.Count) return;
            rows.RemoveAt(from); rows.Insert(to, row);
            list.Children.RemoveAt(from); list.Children.Insert(to, row.Card);
            Refresh();
        }

        Button MoveButton(string icon, string tip) => new()
        {
            Style = (Style)FindResource("IconButton"), ToolTip = tip,
            Content = new IconImage { Icon = icon, Size = 13, Brush = (Brush)FindResource("MutedBrush") },
        };

        // Chosen entries in menu order, then the rest in registration order.
        var ordered = shown.Select(id => (Item: all.First(i => i.Id == id), On: true))
            .Concat(all.Where(i => !shown.Contains(i.Id)).Select(i => (Item: i, On: false)));
        foreach (var (item, on) in ordered)
        {
            var grid = new Grid();
            foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            grid.Children.Add(new IconImage { Icon = item.Icon ?? "circle", Size = 15, Brush = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) });
            var row = new TrayMenuRow
            {
                Item = item, On = on,
                Label = new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center },
                Switch = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) },
                Up = MoveButton("arrow-up", "上移"),
                Down = MoveButton("arrow-down", "下移"),
                Card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(12, 6, 10, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(9), Child = grid },
            };
            Grid.SetColumn(row.Label, 1); Grid.SetColumn(row.Up, 2); Grid.SetColumn(row.Down, 3); Grid.SetColumn(row.Switch, 4);
            grid.Children.Add(row.Label); grid.Children.Add(row.Up); grid.Children.Add(row.Down); grid.Children.Add(row.Switch);
            row.Switch.Click += (_, _) => { row.On = row.Switch.IsChecked == true; Refresh(); };
            row.Up.Click += (_, _) => Move(row, -1);
            row.Down.Click += (_, _) => Move(row, 1);
            rows.Add(row);
            list.Children.Add(row.Card);
        }
        Refresh();

        var content = new StackPanel();
        content.Children.Add(counter);
        // The theme's scroll bar floats over the content: make room for it only while the list actually scrolls.
        var scroller = new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroller.ScrollChanged += (_, _) => list.Margin = new Thickness(0, 0, scroller.ScrollableHeight > 0 ? ScrollBarRoom : 0, 0);
        content.Children.Add(scroller);
        if (!new Plugins.HostUi(_llm).EditDialog("托盘右键菜单", content, 460)) return;

        _trayMenu = rows.Where(r => r.On).Select(r => r.Item.Id).ToList();
        UpdateTrayMenuSummary();
        Changed(sender, e);
    }

    // ------------------------------------------------------------------ apps page

    private void RenderAppLists()
    {
        RenderAppList(ExcludedList, _excluded);
        RenderAppList(NoClipList, _noClip);
    }

    private void RenderAppList(StackPanel host, List<string> items)
    {
        host.Children.Clear();
        foreach (var exe in items)
        {
            var name = exe;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new IconImage { Icon = "app-window", Size = 15, Brush = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
            row.Children.Add(icon);
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var del = new Button { Style = (Style)FindResource("IconButton"), ToolTip = "移除", Content = new IconImage { Icon = "x", Size = 13, Brush = (Brush)FindResource("MutedBrush") } };
            del.Click += (s, e) => { items.Remove(name); RenderAppLists(); Changed(s, e); };
            Grid.SetColumn(del, 2);
            row.Children.Add(del);
            var chip = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(12, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6), Child = row };
            chip.CornerRadius = new CornerRadius(9);
            row.Margin = new Thickness(0);
            host.Children.Add(chip);
        }
    }

    /// <summary>Fill the picker with the processes that currently own a visible window.</summary>
    private void ProcessPick_Opened(object sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;
        combo.ItemsSource = RunningApps.List().Select(a => new AppPick(a.Exe, $"{a.Exe}  —  {a.Title}")).ToList();
    }

    private void AddApp(ComboBox picker, List<string> list)
    {
        var exe = (picker.SelectedItem as AppPick)?.Exe ?? (picker.Text ?? "").Trim();
        if (exe.Length == 0) return;
        if (!list.Contains(exe, StringComparer.OrdinalIgnoreCase)) list.Add(exe);
        picker.SelectedItem = null;
        RenderAppLists();
        Changed(picker, new RoutedEventArgs());
    }

    private void BrowseApp(List<string> list)
    {
        var picked = new Plugins.HostUi(_llm).PickFile("程序 (*.exe)|*.exe", "选择程序");
        if (picked == null) return;
        var exe = System.IO.Path.GetFileName(picked);
        if (!list.Contains(exe, StringComparer.OrdinalIgnoreCase)) list.Add(exe);
        RenderAppLists();
        Changed(this, new RoutedEventArgs());
    }

    private void AddExcluded_Click(object sender, RoutedEventArgs e) => AddApp(ExcludedPick, _excluded);
    private void AddNoClip_Click(object sender, RoutedEventArgs e) => AddApp(NoClipPick, _noClip);
    private void BrowseExcluded_Click(object sender, RoutedEventArgs e) => BrowseApp(_excluded);
    private void BrowseNoClip_Click(object sender, RoutedEventArgs e) => BrowseApp(_noClip);

    private void ResetNoClip_Click(object sender, RoutedEventArgs e)
    {
        _noClip.Clear();
        _noClip.AddRange(new AppConfig().NoClipboardApps);
        RenderAppLists();
        Changed(sender, e);
    }

    private string _showKey = "ctrl";

    private void ShowMode_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowKeyBox != null) ShowKeyBox.IsEnabled = (ShowModeCombo.SelectedValue as string) == "key";
        Changed(sender, e);
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
        if (sender == LlmProtocolCombo) BuildLlmFeatures();
        Changed(sender, e);
    }

    /// <summary>
    /// "获取模型" / "测试连接" talk to the endpoint typed in the form, so the AI service fields are saved right then
    /// (the buttons are an explicit action); every other pending edit stays pending.
    /// </summary>
    private void ApplyLlmOnly()
    {
        var llm = BuildConfig().Llm;
        _store.Update(c => c.Llm = llm);
        var baseline = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(_baseline)!;
        baseline.Llm = llm;
        _baseline = System.Text.Json.JsonSerializer.Serialize(baseline);
        UpdateSaveBar();
    }

    private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurValueText != null) BlurValueText.Text = $"{(int)e.NewValue}%";
        Instant_Changed(sender, e);
    }

    // ---- appearance: previewed live and saved at once, no 保存 prompt (theme, labels, bar length, glass) ----

    private DispatcherTimer? _instantTimer;

    private void Instant_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _baseline.Length == 0) return;
        if (_instantTimer == null)
        {
            // Slider drags fire many changes: coalesce into one write.
            _instantTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _instantTimer.Tick += (_, _) => { _instantTimer.Stop(); ApplyAppearance(); };
        }
        _instantTimer.Stop();
        _instantTimer.Start();
    }

    private void ApplyAppearance()
    {
        var cur = BuildConfig();
        void Copy(AppConfig to) { to.Theme = cur.Theme; to.ShowLabels = cur.ShowLabels; to.MaxBarItems = cur.MaxBarItems; to.BlurOpacity = cur.BlurOpacity; }
        _store.Update(Copy);
        // These are saved now, so they must not count as unsaved edits of the rest of the form.
        var baseline = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(_baseline)!;
        Copy(baseline);
        _baseline = System.Text.Json.JsonSerializer.Serialize(baseline);
        UpdateSaveBar();
    }

    /// <summary>Progress in grey, success in green, failure in red with an icon.</summary>
    private void ShowLlmResult(string text, bool? ok = null)
    {
        LlmTestResult.Inlines.Clear();
        if (ok == false)
        {
            LlmTestResult.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new IconImage
            {
                Icon = "circle-alert", Size = 14, Brush = (Brush)FindResource("DangerBrush"), Margin = new Thickness(0, 0, 6, -2),
            }) { BaselineAlignment = BaselineAlignment.Center });
        }
        LlmTestResult.Inlines.Add(text);
        if (ok == null) LlmTestResult.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        else if (ok == false) LlmTestResult.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        else LlmTestResult.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        ApplyLlmOnly();
        var current = (LlmModelBox.Text ?? "").Trim();
        ShowLlmResult("获取模型列表…");
        try
        {
            var models = await _llm.ListModelsAsync();
            _loading = true;
            LlmModelBox.ItemsSource = models;
            LlmModelBox.Text = current;
            _loading = false;
            ShowLlmResult(models.Count == 0 ? "接口没有返回任何模型" : $"获取到 {models.Count} 个模型，从下拉框选择", ok: models.Count > 0);
            if (models.Count > 0) LlmModelBox.IsDropDownOpen = true;
        }
        catch (Exception ex)
        {
            _loading = false;
            ShowLlmResult("获取失败：" + ex.Message, ok: false);
        }
    }

    private async void LlmTest_Click(object sender, RoutedEventArgs e)
    {
        ApplyLlmOnly();
        ShowLlmResult("连接中…");
        try
        {
            var reply = await _llm.CompleteAsync(new Sdk.LlmRequest { User = "回复“OK”两个字母即可。", MaxTokens = 512, Effort = Sdk.LlmEffort.Low }); // room for reasoning models that think before answering
            var text = reply.Trim().Replace("\n", " ");
            ShowLlmResult(text.Length > 0 ? "连接成功：" + text : "连接成功，但模型没有返回文字（可能是思考型模型输出被截断）", ok: true);
        }
        catch (Exception ex)
        {
            ShowLlmResult("连接失败：" + ex.Message, ok: false);
        }
    }

    // ------------------------------------------------------------------ actions

    private ActionRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ActionRow;

    private static string PluginIcon(string id) => id switch
    {
        "translate" => "languages", "ai" => "sparkles", "ocr" => "type", "screenshot" => "crop", "sendto" => "send",
        "qr" => "qr-code", "codec" => "code", "notes" => "sticky-note", "screentranslate" => "scan-text", _ => "puzzle",
    };

    private static string? OwnerOf(ActionRow r) =>
        r.Def.IsPlugin && r.Def.Ref is { } id && id.IndexOf(':') is var i and > 0 ? id[..i] : null;

    /// <summary>
    /// Rebuild the displayed list: a plugin with two or more actions becomes one group card, placed where its first
    /// action is. Members of a group are kept together, so the saved order is normalised on the next save.
    /// </summary>
    private void Regroup()
    {
        _items.Clear();
        var multi = _rows.Select(OwnerOf).Where(o => o != null).GroupBy(o => o!).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var groups = new Dictionary<string, ActionGroupRow>();
        foreach (var r in _rows)
        {
            var owner = OwnerOf(r);
            if (owner == null || !multi.Contains(owner)) { _items.Add(r); continue; }
            if (!groups.TryGetValue(owner, out var g))
            {
                var plugin = _plugins.Plugins.FirstOrDefault(l => string.Equals(l.Plugin.Id, owner, StringComparison.OrdinalIgnoreCase))?.Plugin;
                groups[owner] = g = new ActionGroupRow { Owner = owner, Name = plugin?.Name ?? owner, Icon = PluginIcon(owner) };
                // Folded groups say how they look on the bar; others fall back to what the plugin does.
                g.Note = _plugins.GroupOf(r.Def) is { } bar
                    ? $"操作条上合并为一个「{bar.Label}」按钮，点击后展开选择"
                    : plugin?.Description ?? "";
                _items.Add(g);
            }
            g.Add(r);
        }
    }

    /// <summary>Write the displayed order back into the flat list.</summary>
    private void Flatten()
    {
        _flattening = true;
        try
        {
            _rows.Clear();
            foreach (var it in _items)
            {
                if (it is ActionRow r) _rows.Add(r);
                else if (it is ActionGroupRow g) foreach (var m in g.Members) _rows.Add(m);
            }
        }
        finally { _flattening = false; }
    }

    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActionGroupRow g) g.IsExpanded = !g.IsExpanded;
    }

    // ---- drag to reorder: stand-alone rows and whole groups move in the list, members move inside their group ----

    private object? _dragItem;
    private ActionGroupRow? _dragParent;
    private bool _dragMoved;

    private static void SetDragging(object item, bool on)
    {
        if (item is ActionRow r) r.IsDragging = on;
        else if (item is ActionGroupRow g) g.IsDragging = on;
    }

    private void DragHandle_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext;
        if (item is not (ActionRow or ActionGroupRow)) return;
        _dragItem = item;
        _dragParent = item is ActionRow row ? _items.OfType<ActionGroupRow>().FirstOrDefault(g => g.Members.Contains(row)) : null;
        _dragMoved = false;
        SetDragging(item, true);
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("PopBarAction", item), DragDropEffects.Move); }
        finally
        {
            SetDragging(item, false);
            _dragItem = null;
            _dragParent = null;
            if (_dragMoved) { Flatten(); Changed(sender, e); }
        }
        e.Handled = true;
    }

    /// <summary>Live reorder: move the dragged item to the slot under the cursor as it moves.</summary>
    private void ActionsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (_dragItem == null) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        var hit = ActionsList.InputHitTest(e.GetPosition(ActionsList)) as DependencyObject;
        if (_dragParent != null)
        {
            // Inside a group: the nearest element bound to a member row.
            var members = _dragParent.Members;
            for (var d = hit; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is FrameworkElement { DataContext: ActionRow t } && members.Contains(t))
                {
                    int from = members.IndexOf((ActionRow)_dragItem), to = members.IndexOf(t);
                    if (from != to) { members.Move(from, to); _dragMoved = true; }
                    return;
                }
            }
            return;
        }
        while (hit != null && hit is not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        if ((hit as ListBoxItem)?.DataContext is { } target && !ReferenceEquals(target, _dragItem))
        {
            int from = _items.IndexOf(_dragItem), to = _items.IndexOf(target);
            if (from >= 0 && to >= 0) { _items.Move(from, to); _dragMoved = true; }
        }
    }

    private void ActionsList_Drop(object sender, DragEventArgs e)
    {
        if (_dragItem != null) e.Handled = true;
    }

    private void EditAction_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender); if (r == null) return;
        var dlg = new ActionEditWindow(r.Def.Clone()) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            int i = _rows.IndexOf(r);
            _rows[i] = new ActionRow(dlg.Result);
            Changed(sender, e);
        }
    }

    private void DeleteAction_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender); if (r == null) return;
        if (MessageBox.Show(this, $"删除动作“{r.Label}”？", "PopBar", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _rows.Remove(r);
        Changed(sender, e);
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ActionEditWindow(new ActionDefinition { Label = "新动作", Icon = "star" }) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            _rows.Add(new ActionRow(dlg.Result));
            Changed(sender, e);
        }
    }

    private void ResetActions_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "恢复默认动作列表？当前自定义动作会被移除。", "PopBar", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _rows.Clear();
        foreach (var a in DefaultActions.Create()) _rows.Add(new ActionRow(a));
        Changed(sender, e);
    }

    // ------------------------------------------------------------------ about

    private void OpenConfigDir_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ConfigStore.FilePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open config dir", ex); }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(System.IO.Path.Combine(ConfigStore.Dir, "popbar.log")) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open log", ex); }
    }
}
