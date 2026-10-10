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

/// <summary>Row view-model for the actions list.</summary>
public sealed class ActionRow : INotifyPropertyChanged
{
    public ActionDefinition Def { get; }
    public ActionRow(ActionDefinition def) => Def = def;

    public string Label => string.IsNullOrWhiteSpace(Def.Label) ? "(未命名)" : Def.Label;
    public Geometry IconGeometry => Icons.Get(Def.Icon);
    /// <summary>Plugin actions are configured inside their plugin; the generic editor does not apply.</summary>
    public Visibility EditVisibility => Def.IsPlugin ? Visibility.Collapsed : Visibility.Visible;
    public bool Enabled
    {
        get => Def.Enabled;
        set { Def.Enabled = value; OnChanged(nameof(Enabled)); }
    }
    public string Summary
    {
        get
        {
            var desc = ActionTypes.Describe(Def.Type);
            var detail = Def.Type switch
            {
                ActionTypes.Plugin => Def.Ref,
                ActionTypes.Url => Def.Url,
                ActionTypes.Transform => Def.Op + (string.IsNullOrEmpty(Def.Template) ? "" : "  " + Def.Template),
                ActionTypes.Command => Def.Command + " " + Def.Args,
                _ => null,
            };
            var cond = Def.Match switch
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
    private readonly ObservableCollection<ActionRow> _rows = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<string> _disabledPlugins = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    internal SettingsWindow(ConfigStore store, Plugins.PluginManager plugins, Sdk.ILlmService llm)
    {
        _store = store;
        _plugins = plugins;
        _llm = llm;
        InitializeComponent();

        LlmProtocolCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("anthropic", "Anthropic（官方 SDK）"),
            new("openai", "OpenAI 兼容"),
        };
        PluginDirText.Text = "第三方插件放在 " + Plugins.PluginStorage.PluginsDir + "\\<插件名>\\*.dll，重启后加载。";
        // Editable ComboBox: typing does not raise SelectionChanged, so listen to the inner TextBox too.
        LlmModelBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => Llm_Changed(s, e)));
        BuildPluginList();

        FallbackCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("ibeam", "文本光标 / 双击时（推荐）"),
            new("always", "总是"),
            new("off", "关闭"),
        };
        ThemeCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("system", "跟随系统"),
            new("dark", "深色"),
            new("light", "浅色"),
        };
        SourceInitialized += (_, _) => Acrylic.Apply(this, BackdropKind.Mica, ThemeManager.IsDark, roundCorners: true);
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        ActionsList.ItemsSource = _rows;

        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        var vs = "v" + (ver == null ? "0.1.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}");
        VersionText.Text = vs;
        AboutVersion.Text = vs + "  ·  .NET " + Environment.Version;
        ConfigPathRun.Text = ConfigStore.FilePath;
        LogPathRun.Text = System.IO.Path.Combine(ConfigStore.Dir, "popbar.log");

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        LoadFromConfig(_store.Current);
    }

    // ------------------------------------------------------------------ load / save

    private void LoadFromConfig(AppConfig c)
    {
        _loading = true;
        try
        {
            EnabledSw.IsChecked = c.Enabled;
            AutoStartSw.IsChecked = c.AutoStart;
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
            ExcludedBox.Text = string.Join(Environment.NewLine, c.ExcludedApps);
            NoClipBox.Text = string.Join(Environment.NewLine, c.NoClipboardApps);

            LlmProtocolCombo.SelectedValue = c.Llm.Protocol;
            if (LlmProtocolCombo.SelectedIndex < 0) LlmProtocolCombo.SelectedIndex = 0;
            LlmBaseUrlBox.Text = c.Llm.BaseUrl;
            LlmKeyBox.Password = c.Llm.ApiKey;
            LlmModelBox.Text = c.Llm.Model;
            _disabledPlugins.Clear();
            foreach (var id in c.DisabledPlugins) _disabledPlugins.Add(id);

            _rows.Clear();
            foreach (var a in c.Actions) _rows.Add(new ActionRow(a.Clone()));
        }
        finally { _loading = false; }
    }

    private AppConfig BuildConfig()
    {
        var c = new AppConfig
        {
            Enabled = EnabledSw.IsChecked == true,
            AutoStart = AutoStartSw.IsChecked == true,
            TriggerOnDrag = DragSw.IsChecked == true,
            TriggerOnDoubleClick = DblSw.IsChecked == true,
            TriggerOnShiftClick = ShiftSw.IsChecked == true,
            ClipboardFallback = FallbackCombo.SelectedValue as string ?? "ibeam",
            ShowDelayMs = ParseInt(DelayBox.Text, 60, 0, 2000),
            MaxTextLength = ParseInt(MaxLenBox.Text, 10000, 1, 1_000_000),
            Theme = ThemeCombo.SelectedValue as string ?? "dark",
            ShowLabels = LabelsSw.IsChecked == true,
            ExcludedApps = Lines(ExcludedBox.Text),
            NoClipboardApps = Lines(NoClipBox.Text),
            Actions = _rows.Select(r => r.Def.Clone()).ToList(),
            DisabledPlugins = _disabledPlugins.ToList(),
            Llm = new LlmConfig
            {
                Protocol = LlmProtocolCombo.SelectedValue as string ?? "anthropic",
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
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
        StatusText.Text = "正在保存…";
    }

    private void SaveNow()
    {
        if (_loading) return;
        _store.Replace(BuildConfig());
        StatusText.Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>Called by the tray when the enabled flag flips elsewhere.</summary>
    public void RefreshFromStore()
    {
        var c = _store.Current;
        _loading = true;
        EnabledSw.IsChecked = c.Enabled;
        _loading = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ nav

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelGeneral == null) return;
        PanelGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelActions.Visibility = NavActions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelPlugins.Visibility = NavPlugins.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ plugins

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
            var iconName = plugin.Id switch { "translate" => "languages", "ai" => "sparkles", "ocr" => "type", "sendto" => "external-link", _ => "puzzle" };
            var iconCanvas = new Canvas { Width = 24, Height = 24, LayoutTransform = new ScaleTransform(0.75, 0.75) };
            iconCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Icons.Get(iconName), Stroke = (Brush)FindResource("AccentHoverBrush"), StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            var iconBox = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Background = (Brush)FindResource("AccentSoftBrush"), Child = iconCanvas, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 14, 0) };
            head.Children.Add(iconBox);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(info, 1);
            var title = new TextBlock { FontWeight = FontWeights.SemiBold };
            title.Inlines.Add(new System.Windows.Documents.Run(plugin.Name));
            title.Inlines.Add(new System.Windows.Documents.Run($"  v{plugin.Version}" + (l.BuiltIn ? "  · 内置" : "  · 第三方")) { Foreground = (Brush)FindResource("MutedBrush"), FontWeight = FontWeights.Normal, FontSize = 12 });
            info.Children.Add(title);
            info.Children.Add(new TextBlock { Text = l.Error != null ? "加载失败：" + l.Error : plugin.Description, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) });
            head.Children.Add(info);
            var toggle = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = !_disabledPlugins.Contains(plugin.Id), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            toggle.Checked += (s, e) => { _disabledPlugins.Remove(plugin.Id); Changed(s, e); };
            toggle.Unchecked += (s, e) => { _disabledPlugins.Add(plugin.Id); Changed(s, e); };
            Grid.SetColumn(toggle, 2);
            head.Children.Add(toggle);
            stack.Children.Add(head);

            if (l.Error == null)
            {
                FrameworkElement? view = null;
                try { view = plugin.CreateSettingsView(); }
                catch (Exception ex) { Log.Error($"plugin {plugin.Id} settings view", ex); }
                if (view != null)
                {
                    var expander = new Expander { Header = "设置", Margin = new Thickness(0, 8, 0, 0), Content = view, IsExpanded = false };
                    stack.Children.Add(expander);
                }
            }
            PluginList.Children.Add(card);
        }
    }

    private void OnThemeChanged(string effective) => Dispatcher.InvokeAsync(() => Acrylic.SetDark(this, ThemeManager.IsDark));

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/iccyuan/PopBar") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open repo", ex); }
    }

    private void Llm_Changed(object sender, RoutedEventArgs e) => Changed(sender, e);

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); }
        var current = (LlmModelBox.Text ?? "").Trim();
        LlmTestResult.Text = "获取模型列表…";
        try
        {
            var models = await _llm.ListModelsAsync();
            _loading = true;
            LlmModelBox.ItemsSource = models;
            LlmModelBox.Text = current;
            _loading = false;
            LlmTestResult.Text = models.Count == 0 ? "接口没有返回任何模型" : $"获取到 {models.Count} 个模型，从下拉框选择";
            if (models.Count > 0) LlmModelBox.IsDropDownOpen = true;
        }
        catch (Exception ex)
        {
            _loading = false;
            LlmTestResult.Text = "获取失败：" + ex.Message;
        }
    }

    private async void LlmTest_Click(object sender, RoutedEventArgs e)
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); }
        LlmTestResult.Text = "连接中…";
        try
        {
            var reply = await _llm.CompleteAsync(new Sdk.LlmRequest { User = "回复“OK”两个字母即可。", MaxTokens = 32, Effort = "low" });
            LlmTestResult.Text = "成功：" + reply.Trim().Replace("\n", " ");
        }
        catch (Exception ex)
        {
            LlmTestResult.Text = "失败：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------ actions

    private ActionRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ActionRow;

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender); if (r == null) return;
        int i = _rows.IndexOf(r);
        if (i > 0) { _rows.Move(i, i - 1); Changed(sender, e); }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender); if (r == null) return;
        int i = _rows.IndexOf(r);
        if (i >= 0 && i < _rows.Count - 1) { _rows.Move(i, i + 1); Changed(sender, e); }
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
