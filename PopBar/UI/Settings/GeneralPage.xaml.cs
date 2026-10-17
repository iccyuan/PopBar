using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PopBar.Config;

namespace PopBar.UI;

/// <summary>
/// "通用": on/off, autostart, tray, triggers, capture, and the appearance block. Appearance is previewed live and saved
/// at once, so it is not part of <see cref="Apply"/> (it never waits for 保存).
/// </summary>
internal partial class GeneralPage : SettingsPage
{
    /// <summary>A choice of <see cref="AppConfig.ClipboardFallback"/>; Key / Value as in the other combos.</summary>
    private sealed record FallbackOption(string Key, string Value, string Tip);

    private string _showKey = "alt";
    /// <summary>Draft of <see cref="AppConfig.TrayMenuItems"/>; null = never customised.</summary>
    private List<string>? _trayMenu;
    private DispatcherTimer? _appearanceTimer;

    public GeneralPage()
    {
        InitializeComponent();
        FallbackCombo.ItemsSource = new List<FallbackOption>
        {
            new("ibeam", "只在选中的是文字时（推荐）", "鼠标是 I 形文本光标，或双击选中时才按 Ctrl+C；框选文件、图片时不会误复制"),
            new("always", "每次拖选都试", "有些程序鼠标停在文字上仍是箭头，用这一项才能取词；但框选文件、图片时也会去复制"),
            new("off", "从不", "读不出文字的程序里就不取词"),
        };
        // each option explains itself on hover, the closed box shows the chosen one's explanation
        var itemStyle = new Style(typeof(ComboBoxItem), (Style)FindResource(typeof(ComboBoxItem)));
        itemStyle.Setters.Add(new Setter(ToolTipProperty, new System.Windows.Data.Binding(nameof(FallbackOption.Tip))));
        FallbackCombo.ItemContainerStyle = itemStyle;
        FallbackCombo.SelectionChanged += (_, _) => FallbackCombo.ToolTip = (FallbackCombo.SelectedItem as FallbackOption)?.Tip;
        ShowModeCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("key", "按键后显示"),
            new("auto", "选中后自动显示"),
        };
        ThemeCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("system", "跟随系统"),
            new("dark", "深色"),
            new("light", "浅色"),
        };
        MaxItemsCombo.ItemsSource = new[] { 4, 5, 6, 7, 8, 10, 12 }.Select(n => new KeyValuePair<string, string>(n.ToString(), $"{n} 个")).Append(new("0", "不限制")).ToList();
        // A reveal key is tapped alone, so it must be one that does nothing while typing: modifiers, F-keys and the
        // lock / toggle keys (whose own effect is held back while a bar waits). Letters, Space, Enter... would eat input.
        ShowKeyBox.AcceptKey = Core.SelectionEngine.IsAllowedRevealKey;
        ShowKeyBox.RejectHint = "请按 Alt / Shift / Ctrl / Win、F1–F24、CapsLock 等";
        ShowKeyBox.GestureChanged += g =>
        {
            _showKey = string.IsNullOrWhiteSpace(g) ? "alt" : g; // cleared: back to the default
            ShowKeyBox.Gesture = _showKey;
            RaiseEdited();
        };
    }

    protected override void OnAttach()
    {
        // Built-in choices first, then whatever the loaded plugins offer as a click action (same ids as `PopBar.exe --run <id>`).
        var trayChoices = new List<KeyValuePair<string, string>> { new("settings", "打开设置"), new("menu", "打开托盘菜单") };
        trayChoices.AddRange(Ctx.TrayItems().Where(i => i.Item.AsClick).Select(i => new KeyValuePair<string, string>(i.Id, i.Item.Label)));
        TrayClickCombo.ItemsSource = trayChoices;
    }

    protected override void OnLoad(AppConfig c)
    {
        EnabledSw.IsChecked = c.Enabled;
        AutoStartSw.IsChecked = c.AutoStart;
        TrayClickCombo.SelectedValue = c.TrayClick;
        if (TrayClickCombo.SelectedIndex < 0) TrayClickCombo.SelectedIndex = 0;
        ShowModeCombo.SelectedValue = c.ShowMode;
        if (ShowModeCombo.SelectedIndex < 0) ShowModeCombo.SelectedIndex = 0;
        _showKey = string.IsNullOrWhiteSpace(c.ShowKey) ? "alt" : c.ShowKey;
        ShowKeyBox.Gesture = _showKey;
        ShowKeyBox.IsEnabled = c.ShowMode == "key";
        DragSw.IsChecked = c.TriggerOnDrag;
        DblSw.IsChecked = c.TriggerOnDoubleClick;
        ShiftSw.IsChecked = c.TriggerOnShiftClick;
        FallbackCombo.SelectedValue = c.ClipboardFallback;
        if (FallbackCombo.SelectedIndex < 0) FallbackCombo.SelectedIndex = 0;
        JavaBridgeSw.IsChecked = c.JavaAccessBridge;
        DelayBox.Text = c.ShowDelayMs.ToString();
        MaxLenBox.Text = c.MaxTextLength.ToString();
        _trayMenu = c.TrayMenuItems?.ToList();
        UpdateTrayMenuSummary();

        ThemeCombo.SelectedValue = c.Theme;
        if (ThemeCombo.SelectedIndex < 0) ThemeCombo.SelectedIndex = 0;
        LabelsSw.IsChecked = c.ShowLabels;
        MaxItemsCombo.SelectedValue = c.MaxBarItems.ToString();
        if (MaxItemsCombo.SelectedIndex < 0) MaxItemsCombo.SelectedValue = "7";
        BlurSlider.Value = Math.Clamp(c.BlurOpacity, 40, 100);
        BlurValueText.Text = $"{(int)BlurSlider.Value}%";
    }

    public override void Apply(AppConfig c)
    {
        c.Enabled = EnabledSw.IsChecked == true;
        c.AutoStart = AutoStartSw.IsChecked == true;
        c.TrayClick = TrayClickCombo.SelectedValue as string ?? "settings";
        c.ShowMode = ShowModeCombo.SelectedValue as string ?? "key";
        c.ShowKey = _showKey;
        c.TriggerOnDrag = DragSw.IsChecked == true;
        c.TriggerOnDoubleClick = DblSw.IsChecked == true;
        c.TriggerOnShiftClick = ShiftSw.IsChecked == true;
        c.ClipboardFallback = FallbackCombo.SelectedValue as string ?? "ibeam";
        c.JavaAccessBridge = JavaBridgeSw.IsChecked == true;
        c.ShowDelayMs = ParseInt(DelayBox.Text, 60, 0, 2000);
        c.MaxTextLength = ParseInt(MaxLenBox.Text, 10000, 1, 1_000_000);
        c.TrayMenuItems = _trayMenu?.ToList();
    }

    /// <summary>The enabled flag flipped elsewhere (tray): show it; when nothing else is pending it is not an edit.</summary>
    public void SyncEnabled(bool enabled)
    {
        Quietly(() => EnabledSw.IsChecked = enabled);
        Rebase(c => c.Enabled = enabled);
    }

    /// <summary>The standalone switches save at once, no 保存 needed.</summary>
    private void Switch_Changed(object sender, RoutedEventArgs e)
    {
        bool on = (sender as CheckBox)?.IsChecked == true;
        Action<AppConfig>? write = sender switch
        {
            _ when sender == EnabledSw => c => c.Enabled = on,
            _ when sender == AutoStartSw => c => c.AutoStart = on,
            _ when sender == DragSw => c => c.TriggerOnDrag = on,
            _ when sender == DblSw => c => c.TriggerOnDoubleClick = on,
            _ when sender == ShiftSw => c => c.TriggerOnShiftClick = on,
            _ when sender == JavaBridgeSw => c => c.JavaAccessBridge = on,
            _ => null,
        };
        if (write != null) SaveAtOnce(write);
    }

    private static int ParseInt(string s, int def, int min, int max) =>
        int.TryParse(s.Trim(), out var v) ? Math.Clamp(v, min, max) : def;

    private void ShowMode_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowKeyBox != null) ShowKeyBox.IsEnabled = (ShowModeCombo.SelectedValue as string) == "key";
        RaiseEdited();
    }

    // ---- appearance: previewed live and saved at once, no 保存 prompt (theme, labels, bar length, glass) ----

    private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurValueText != null) BlurValueText.Text = $"{(int)e.NewValue}%";
        Instant_Changed(sender, e);
    }

    private void Instant_Changed(object sender, RoutedEventArgs e)
    {
        if (Loading || Ctx == null) return;
        if (_appearanceTimer == null)
        {
            // Slider drags fire many changes: coalesce into one write.
            _appearanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _appearanceTimer.Tick += (_, _) => { _appearanceTimer.Stop(); SaveAppearance(); };
        }
        _appearanceTimer.Stop();
        _appearanceTimer.Start();
    }

    private void SaveAppearance()
    {
        var theme = ThemeCombo.SelectedValue as string ?? "system";
        bool labels = LabelsSw.IsChecked == true;
        int max = int.TryParse(MaxItemsCombo.SelectedValue as string, out var mx) ? mx : 7;
        int blur = (int)BlurSlider.Value;
        Ctx.Store.Update(c => { c.Theme = theme; c.ShowLabels = labels; c.MaxBarItems = max; c.BlurOpacity = blur; });
    }

    // ---- tray menu ----

    /// <summary>Width of the theme's ScrollBar (Themes/Modern.xaml) plus a hair of gap.</summary>
    private const double ScrollBarRoom = 14;

    /// <summary>The entries that support the menu (<see cref="Sdk.TrayItem.InMenu"/>), in registration order.</summary>
    private List<(string Id, string Label, string? Icon, bool ByDefault)> AllTrayItems() =>
        Ctx.TrayItems().Where(i => i.Item.InMenu).Select(i => (i.Id, i.Item.Label, i.Item.Icon, i.Item.InMenuByDefault)).ToList();

    /// <summary>The ids the menu shows now: the draft, or by default the first ones in registration order.</summary>
    private List<string> ShownTrayIds()
    {
        var all = AllTrayItems();
        var ids = _trayMenu ?? all.Where(i => i.ByDefault).Select(i => i.Id).ToList();
        return ids.Where(id => all.Any(i => i.Id == id)).Take(AppConfig.MaxTrayMenuItems).ToList();
    }

    private void UpdateTrayMenuSummary()
    {
        if (Ctx == null) return;
        TrayMenuSummary.Text = $"已选 {ShownTrayIds().Count} / {AppConfig.MaxTrayMenuItems}";
        TrayMenuTip.Text = $"插件提供的入口，最多 {AppConfig.MaxTrayMenuItems} 项；启用 / 暂停、设置、退出始终显示";
    }

    /// <summary>One entry of the tray-menu editor; its controls are built once and only updated afterwards.</summary>
    private sealed class TrayMenuRow
    {
        public required (string Id, string Label, string? Icon, bool ByDefault) Item { get; init; }
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
        var counter = new TextBlock { Style = Res<Style>("Muted"), Margin = new Thickness(2, 4, 0, 10) };
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
            Style = Res<Style>("IconButton"), ToolTip = tip,
            Content = new IconImage { Icon = icon, Size = 13, Brush = Res<Brush>("MutedBrush") },
        };

        // Chosen entries in menu order, then the rest in registration order.
        var ordered = shown.Select(id => (Item: all.First(i => i.Id == id), On: true))
            .Concat(all.Where(i => !shown.Contains(i.Id)).Select(i => (Item: i, On: false)));
        foreach (var (item, on) in ordered)
        {
            var grid = new Grid();
            foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            grid.Children.Add(new IconImage { Icon = item.Icon ?? "circle", Size = 15, Brush = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) });
            var row = new TrayMenuRow
            {
                Item = item, On = on,
                Label = new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center },
                Switch = new CheckBox { Style = Res<Style>("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) },
                Up = MoveButton("arrow-up", "上移"),
                Down = MoveButton("arrow-down", "下移"),
                Card = new Border { Style = Res<Style>("Card"), Padding = new Thickness(12, 6, 10, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(9), Child = grid },
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
        if (!Ctx.Ui.EditDialog("托盘右键菜单", content, 460)) return;

        _trayMenu = rows.Where(r => r.On).Select(r => r.Item.Id).ToList();
        UpdateTrayMenuSummary();
        RaiseEdited();
    }
}
