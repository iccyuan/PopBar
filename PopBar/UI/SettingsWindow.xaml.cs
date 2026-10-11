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
    private readonly ObservableCollection<ActionRow> _rows = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<string> _disabledPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _excluded = new();
    private readonly List<string> _noClip = new();
    private bool _loading;

    private sealed record AppPick(string Exe, string Label);

    internal SettingsWindow(ConfigStore store, Plugins.PluginManager plugins, Sdk.ILlmService llm)
    {
        _store = store;
        _plugins = plugins;
        _llm = llm;
        ActionRow.PluginDescription = id => _plugins.TryGet(id, out _, out var a) ? a.Description : null;
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
            new("auto", "选中后自动显示"),
            new("key", "按键后显示"),
        };
        ShowKeyBox.GestureChanged += g => { _showKey = g; Changed(ShowKeyBox, new RoutedEventArgs()); };
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

        // A plugin added / removed actions (e.g. a new send-to target): show them on the actions page right away.
        _plugins.ActionsSynced += OnPluginActionsSynced;
        Closed += (_, _) => _plugins.ActionsSynced -= OnPluginActionsSynced;
    }

    private void OnPluginActionsSynced() => Dispatcher.InvokeAsync(() =>
    {
        _loading = true;
        try
        {
            _rows.Clear();
            foreach (var a in _store.Current.Actions) _rows.Add(new ActionRow(a.Clone()));
        }
        finally { _loading = false; }
    });

    // ------------------------------------------------------------------ load / save

    private void LoadFromConfig(AppConfig c)
    {
        _loading = true;
        try
        {
            EnabledSw.IsChecked = c.Enabled;
            AutoStartSw.IsChecked = c.AutoStart;
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
            BlurSlider.Value = Math.Clamp(c.BlurOpacity, 40, 100);
            BlurValueText.Text = $"{(int)BlurSlider.Value}%";
            _excluded.Clear(); _excluded.AddRange(c.ExcludedApps);
            _noClip.Clear(); _noClip.AddRange(c.NoClipboardApps);
            RenderAppLists();

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
            ShowMode = ShowModeCombo.SelectedValue as string ?? "auto",
            ShowKey = _showKey,
            TriggerOnDrag = DragSw.IsChecked == true,
            TriggerOnDoubleClick = DblSw.IsChecked == true,
            TriggerOnShiftClick = ShiftSw.IsChecked == true,
            ClipboardFallback = FallbackCombo.SelectedValue as string ?? "ibeam",
            ShowDelayMs = ParseInt(DelayBox.Text, 60, 0, 2000),
            MaxTextLength = ParseInt(MaxLenBox.Text, 10000, 1, 1_000_000),
            Theme = ThemeCombo.SelectedValue as string ?? "dark",
            ShowLabels = LabelsSw.IsChecked == true,
            BlurOpacity = (int)BlurSlider.Value,
            ExcludedApps = _excluded.ToList(),
            NoClipboardApps = _noClip.ToList(),
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
        // Controls raise change events while InitializeComponent runs (e.g. Slider coercing its value), before the timer exists.
        if (_loading || _saveTimer == null) return;
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
        PanelApps.Visibility = NavApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var iconName = plugin.Id switch { "translate" => "languages", "ai" => "sparkles", "ocr" => "type", "screenshot" => "crop", "sendto" => "send", "qr" => "qr-code", "codec" => "code", _ => "puzzle" };
            var iconImg = new IconImage { Icon = iconName, Size = 18, Brush = (Brush)FindResource("AccentHoverBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var iconBox = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Background = (Brush)FindResource("AccentSoftBrush"), Child = iconImg, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 14, 0) };
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

    private void OnThemeChanged(string effective) => Dispatcher.InvokeAsync(() => Acrylic.SetDark(this, ThemeManager.IsDark));

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/iccyuan/PopBar") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open repo", ex); }
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
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "程序 (*.exe)|*.exe", Title = "选择程序" };
        if (dlg.ShowDialog(this) != true) return;
        var exe = System.IO.Path.GetFileName(dlg.FileName);
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

    private void Llm_Changed(object sender, RoutedEventArgs e) => Changed(sender, e);

    private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurValueText != null) BlurValueText.Text = $"{(int)e.NewValue}%";
        Changed(sender, e);
    }

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

    // ---- drag to reorder ----

    private ActionRow? _dragRow;

    private void DragHandle_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var row = RowOf(sender); if (row == null) return;
        _dragRow = row;
        row.IsDragging = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(ActionRow), row), DragDropEffects.Move); }
        finally
        {
            row.IsDragging = false;
            _dragRow = null;
        }
        e.Handled = true;
    }

    /// <summary>Live reorder: move the dragged row to the slot under the cursor as it moves.</summary>
    private void ActionsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (_dragRow == null) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        var target = RowAtPoint(e.GetPosition(ActionsList));
        if (target == null || ReferenceEquals(target, _dragRow)) return;
        int from = _rows.IndexOf(_dragRow), to = _rows.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to) _rows.Move(from, to);
    }

    private void ActionsList_Drop(object sender, DragEventArgs e)
    {
        if (_dragRow == null) return;
        e.Handled = true;
        Changed(sender, e);
    }

    private ActionRow? RowAtPoint(Point p)
    {
        var hit = ActionsList.InputHitTest(p) as DependencyObject;
        while (hit != null && hit is not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        return (hit as ListBoxItem)?.DataContext as ActionRow;
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
