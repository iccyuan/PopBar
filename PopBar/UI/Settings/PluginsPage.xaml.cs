using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PopBar.Config;

namespace PopBar.UI;

/// <summary>
/// "插件": one card per plugin (on/off switch, its own settings view behind the gear) and one per DLL that failed to
/// load. The switches are this page's state; what plugins edit in their views goes through <see cref="SettingsContext.PluginDrafts"/>.
/// </summary>
internal partial class PluginsPage : SettingsPage
{
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Opens a plugin card's settings area, scrolled into view.</summary>
    private readonly Dictionary<string, Action> _openSettings = new(StringComparer.OrdinalIgnoreCase);

    public PluginsPage() => InitializeComponent();

    protected override void OnAttach() =>
        PluginDirText.Text = "第三方插件目录：" + Plugins.PluginStorage.PluginsDir + "\n第三方插件和 PopBar 拥有相同权限（读剪贴板、按键、联网），只安装信得过的来源。";

    protected override void OnLoad(AppConfig c)
    {
        _disabled.Clear();
        foreach (var id in c.DisabledPlugins) _disabled.Add(id);
        // Rebuilt on every load: the plugins' settings views start again from their live settings.
        BuildList();
    }

    public override void Apply(AppConfig c) => c.DisabledPlugins = _disabled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Open a plugin's settings area (once the page is laid out).</summary>
    public void Open(string pluginId)
    {
        if (_openSettings.TryGetValue(pluginId, out var open)) Dispatcher.InvokeAsync(open, DispatcherPriority.Loaded);
    }

    private void BuildList()
    {
        PluginList.Children.Clear();
        _openSettings.Clear();
        foreach (var l in Ctx.Plugins.Plugins) PluginList.Children.Add(BuildCard(l));
        // DLLs that never produced a plugin object: still worth a card, or the only trace is the log.
        foreach (var f in Ctx.Plugins.LoadFailures)
        {
            var info = new StackPanel { ToolTip = f.Path };
            var title = new TextBlock { FontSize = 14 };
            title.Inlines.Add(new System.Windows.Documents.Run(System.IO.Path.GetFileName(f.Path)));
            title.Inlines.Add(new System.Windows.Documents.Run("  · 第三方，来源未验证") { Foreground = Res<Brush>("MutedBrush"), FontSize = 12 });
            info.Children.Add(title);
            info.Children.Add(new TextBlock { Text = "无法加载：" + f.Error + "（修好后重启 PopBar 生效）", Style = Res<Style>("Muted"), Foreground = Res<Brush>("DangerBrush"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            PluginList.Children.Add(new Border { Style = Res<Style>("Card"), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 0, 10), Child = info });
        }
    }

    private Border BuildCard(Plugins.PluginManager.Loaded l)
    {
        var plugin = l.Plugin;
        var card = new Border { Style = Res<Style>("Card"), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 0, 10) };
        var stack = new StackPanel();
        card.Child = stack;

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconImg = new IconImage { Icon = Ctx.Plugins.IconOf(plugin.Id), Size = 18, Brush = Res<Brush>("AccentHoverBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Background = Res<Brush>("AccentSoftBrush"), Child = iconImg, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 14, 0) });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(info, 1);
        var title = new TextBlock { FontSize = 14 }; // YaHei UI has no semibold: SemiBold would render as heavy Bold
        title.Inlines.Add(new System.Windows.Documents.Run(plugin.Name));
        title.Inlines.Add(new System.Windows.Documents.Run($"  v{plugin.Version}" + (l.BuiltIn ? "  · 内置" : "  · 第三方，来源未验证")) { Foreground = Res<Brush>("MutedBrush"), FontWeight = FontWeights.Normal, FontSize = 12 });
        if (l.Source != null) title.ToolTip = l.Source;
        info.Children.Add(title);
        var desc = new TextBlock { Text = l.Error != null ? "加载失败：" + l.Error + (l.BuiltIn ? "" : "（修好后重启 PopBar 生效）") : plugin.Description, Style = Res<Style>("Muted"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        if (l.Error != null) desc.Foreground = Res<Brush>("DangerBrush");
        info.Children.Add(desc);
        head.Children.Add(info);
        var toggle = new CheckBox { Style = Res<Style>("Switch"), IsChecked = !_disabled.Contains(plugin.Id), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        toggle.Checked += (_, _) => { _disabled.Remove(plugin.Id); RaiseEdited(); };
        toggle.Unchecked += (_, _) => { _disabled.Add(plugin.Id); RaiseEdited(); };
        Grid.SetColumn(toggle, 3);
        head.Children.Add(toggle);
        stack.Children.Add(head);

        FrameworkElement? view = null;
        if (l.Error == null)
        {
            try { view = plugin.CreateSettingsView(); }
            catch (Exception ex) { Log.Error($"plugin {plugin.Id} settings view", ex); }
        }
        if (view == null) return card;

        // Gear button in the header (left of the switch) toggles an in-card settings area.
        var muted = Res<Brush>("MutedBrush");
        var accent = Res<Brush>("AccentHoverBrush");
        var gearIcon = new IconImage { Icon = "settings", Size = 16, Brush = muted };
        var gear = new Button { Style = Res<Style>("IconButton"), Width = 32, Height = 32, Content = gearIcon, ToolTip = "设置", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        Grid.SetColumn(gear, 2);
        head.Children.Add(gear);

        var body = new StackPanel { Visibility = Visibility.Collapsed };
        body.Children.Add(new System.Windows.Shapes.Rectangle { Height = 1, Fill = Res<Brush>("BorderBrush"), Margin = new Thickness(0, 12, 0, 12) });
        body.Children.Add(view);
        stack.Children.Add(body);

        void SetOpen(bool open)
        {
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            gearIcon.Brush = open ? accent : muted;
            gear.Background = open ? Res<Brush>("AccentSoftBrush") : Brushes.Transparent;
        }
        gear.Click += (_, _) => SetOpen(body.Visibility != Visibility.Visible);
        _openSettings[plugin.Id] = () => { SetOpen(true); card.BringIntoView(); };
        return card;
    }
}
