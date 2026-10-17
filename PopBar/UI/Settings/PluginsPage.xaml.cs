using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PopBar.Config;

namespace PopBar.UI;

/// <summary>
/// "插件": one card per plugin (on/off switch, its own settings view behind the gear, uninstall for third-party ones)
/// and one per DLL that failed to load, plus installing from a GitHub repository or a local .zip / .dll. The switches
/// are this page's state; what plugins edit in their views goes through <see cref="SettingsContext.PluginDrafts"/>.
/// </summary>
internal partial class PluginsPage : SettingsPage
{
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Opens a plugin card's settings area, scrolled into view.</summary>
    private readonly Dictionary<string, Action> _openSettings = new(StringComparer.OrdinalIgnoreCase);

    public PluginsPage() => InitializeComponent();

    protected override void OnAttach() =>
        PluginDirTip.Text = "第三方插件装在 " + Plugins.PluginStorage.PluginsDir + "，和 PopBar 拥有相同权限（读剪贴板、按键、联网），只安装信得过的来源。";

    protected override void OnLoad(AppConfig c)
    {
        HideNotice(null, EventArgs.Empty); // a reload (cancel, reopen) starts without the last install's message
        _disabled.Clear();
        foreach (var id in c.DisabledPlugins) _disabled.Add(id);
        // Rebuilt on every load: the plugins' settings views start again from their live settings.
        BuildList();
    }

    public override void Apply(AppConfig c) => c.DisabledPlugins = DisabledList();

    private List<string> DisabledList() => _disabled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    private void SaveDisabled()
    {
        var list = DisabledList();
        SaveAtOnce(c => c.DisabledPlugins = list.ToList());
    }

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
            info.Children.Add(new TextBlock { Text = "无法加载：" + f.Error + "（修好后重新安装即可）", Style = Res<Style>("Muted"), Foreground = Res<Brush>("DangerBrush"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
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
        // Under the name only what needs attention: a load error.
        if (l.Error != null)
            info.Children.Add(new TextBlock { Text = "加载失败：" + l.Error + (l.BuiltIn ? "" : "（修好后重新安装即可）"), Style = Res<Style>("Muted"), Foreground = Res<Brush>("DangerBrush"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(info);
        if (!l.BuiltIn && l.Source != null)
        {
            var uninstall = new Button { Style = Res<Style>("IconButton"), Width = 32, Height = 32, ToolTip = "卸载", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Content = new IconImage { Icon = "trash", Size = 15, Brush = Res<Brush>("DangerBrush") } };
            uninstall.Click += (_, _) => Uninstall(l);
            Grid.SetColumn(uninstall, 2);
            head.Children.Add(uninstall);
        }
        var toggle = new CheckBox { Style = Res<Style>("Switch"), IsChecked = !_disabled.Contains(plugin.Id), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        // a standalone switch: saved at once, the plugin's actions come and go right away
        toggle.Checked += (_, _) => { _disabled.Remove(plugin.Id); SaveDisabled(); };
        toggle.Unchecked += (_, _) => { _disabled.Add(plugin.Id); SaveDisabled(); };
        Grid.SetColumn(toggle, 4);
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
        Grid.SetColumn(gear, 3);
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

    // ---- install / uninstall ----

    private const string TrustNote = "第三方插件和 PopBar 拥有相同权限：能读取选中内容和剪贴板、模拟按键、联网。只安装信得过的来源。";

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        ShowAddMenu(InstallButton, new (string?, string, Action)[]
        {
            (null, "从GitHub安装", InstallFromGitHub),
            (null, "从本地文件安装", InstallFromFile),
        });
    }

    private void InstallFromGitHub()
    {
        var box = new TextBox { Width = 400 };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "仓库地址", Margin = new Thickness(0, 6, 0, 6) });
        content.Children.Add(box);
        content.Children.Add(new TextBlock { Text = "如 https://github.com/作者/仓库 或 作者/仓库。安装仓库最新发布（Release）里的 .zip 或 .dll。", Style = Res<Style>("Muted"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = TrustNote, Style = Res<Style>("Muted"), Foreground = Res<Brush>("DangerBrush"), Margin = new Thickness(0, 8, 0, 6), TextWrapping = TextWrapping.Wrap });
        box.Loaded += (_, _) => box.Focus();
        if (!Ctx.Ui.EditDialog("从 GitHub 安装插件", content, 480, "安装")) return;
        var repo = box.Text.Trim();
        if (repo.Length == 0) return;
        if (Plugins.PluginInstaller.ParseRepo(repo) == null) { Notice("不是 GitHub 仓库地址：" + repo, NoticeKind.Error); return; }
        _ = RunInstall(progress => Plugins.PluginInstaller.FromGitHubAsync(Ctx.Http, repo, progress));
    }

    private void InstallFromFile()
    {
        var path = Ctx.Ui.PickFile("PopBar 插件 (*.zip;*.dll)|*.zip;*.dll", "选择插件");
        if (path == null) return;
        if (!Ctx.Ui.Confirm(TrustNote + "\n\n安装 " + System.IO.Path.GetFileName(path) + "？", "安装插件")) return;
        _ = RunInstall(_ => Task.Run(() => Plugins.PluginInstaller.FromFile(path)));
    }

    /// <summary>Install from outside the page (<c>PopBar.exe --install</c>): a .zip / .dll path or a GitHub repository.</summary>
    public void InstallFrom(string source)
    {
        source = source.Trim().Trim('"');
        if (System.IO.File.Exists(source))
        {
            if (!Ctx.Ui.Confirm(TrustNote + "\n\n安装 " + System.IO.Path.GetFileName(source) + "？", "安装插件")) return;
            _ = RunInstall(_ => Task.Run(() => Plugins.PluginInstaller.FromFile(source)));
        }
        else if (Plugins.PluginInstaller.ParseRepo(source) is { } repo)
        {
            if (!Ctx.Ui.Confirm(TrustNote + $"\n\n安装 GitHub 仓库 {repo.Owner}/{repo.Repo} 的最新发布？", "安装插件")) return;
            _ = RunInstall(progress => Plugins.PluginInstaller.FromGitHubAsync(Ctx.Http, source, progress));
        }
        else Notice("找不到要安装的插件：" + source, NoticeKind.Error);
    }

    /// <summary>
    /// Fetch and check the plugin off the UI thread, then swap it in: an older version loaded from the same folder is
    /// unloaded first (its buttons stay, the new version takes them over), and the new one starts right away.
    /// </summary>
    private async Task RunInstall(Func<IProgress<string>, Task<Plugins.PluginInstaller.Prepared>> fetch)
    {
        InstallButton.IsEnabled = false;
        try
        {
            using var prepared = await fetch(new Progress<string>(s => Notice(s, NoticeKind.Progress)));
            var target = Plugins.PluginInstaller.TargetOf(prepared);
            var replaced = Ctx.Plugins.Unload(target, dropActions: false);
            Plugins.PluginInstaller.Commit(prepared);
            var loaded = Ctx.Plugins.LoadInstalled(target);
            BuildList();
            var names = string.Join("、", loaded.Select(l => l.Plugin.Name));
            if (loaded.Count == 0) Notice($"已复制到 {target}，但没有加载出插件，原因见下方或日志", NoticeKind.Error);
            else if (loaded.Any(l => l.Error != null)) Notice($"已安装「{names}」，但初始化失败，原因见下方", NoticeKind.Error);
            else Notice(replaced.Count > 0 ? $"已更新「{names}」到 v{loaded[0].Plugin.Version}，立即生效" : $"已安装并启用「{names}」，立即生效", NoticeKind.Done);
        }
        catch (Exception ex)
        {
            Log.Error("plugin install", ex);
            Notice("安装失败：" + ex.Message, NoticeKind.Error);
        }
        finally { InstallButton.IsEnabled = true; }
    }

    private void Uninstall(Plugins.PluginManager.Loaded l)
    {
        if (!Ctx.Ui.Confirm($"卸载「{l.Plugin.Name}」？它的按钮会从操作条上移除，文件一并删除。", "卸载插件")) return;
        // Every plugin that came from the same folder goes with it.
        var folder = System.IO.Path.GetDirectoryName(l.Source!)!;
        Ctx.Plugins.Unload(folder, dropActions: true);
        try { Plugins.PluginInstaller.Remove(folder); }
        catch (Exception ex) { Log.Error("plugin uninstall", ex); Notice($"已停用「{l.Plugin.Name}」，但删除文件失败：{ex.Message}", NoticeKind.Error); BuildList(); return; }
        BuildList();
        Notice($"已卸载「{l.Plugin.Name}」", NoticeKind.Done);
    }

    /// <summary>Progress stays until the result replaces it; a success fades on its own; an error waits to be closed.</summary>
    private enum NoticeKind { Progress, Done, Error }

    private DispatcherTimer? _noticeTimer;

    private void Notice(string text, NoticeKind kind)
    {
        _noticeTimer?.Stop();
        InstallNotice.Visibility = Visibility.Visible;
        InstallText.Text = text;
        InstallText.SetResourceReference(TextBlock.ForegroundProperty, kind == NoticeKind.Error ? "DangerBrush" : "TextBrush");
        NoticeClose.Visibility = kind == NoticeKind.Progress ? Visibility.Collapsed : Visibility.Visible;
        if (kind != NoticeKind.Done) return;
        _noticeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _noticeTimer.Tick -= HideNotice;
        _noticeTimer.Tick += HideNotice;
        _noticeTimer.Start();
    }

    private void HideNotice(object? sender, EventArgs e)
    {
        _noticeTimer?.Stop();
        InstallNotice.Visibility = Visibility.Collapsed;
    }

    private void NoticeClose_Click(object sender, RoutedEventArgs e) => HideNotice(null, EventArgs.Empty);
}
