using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PopBar.Config;
using PopBar.Core;

namespace PopBar.UI;

/// <summary>
/// The settings window: navigation, the unsaved-changes bar and saving. Each page (UI/Settings) owns its fields and
/// tracks its own edits; the window only asks them. Saving merges the edited fields onto what is stored now, so
/// writes made meanwhile elsewhere (tray toggle, plugin action sync, model list cache) survive.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly Plugins.SettingsHub _pluginDrafts;
    private readonly SettingsPage[] _pages;
    /// <summary>The config the pages last loaded or saved: the "before" of the three-way merge.</summary>
    private AppConfig _loaded = new();

    internal SettingsWindow(ConfigStore store, Plugins.PluginManager plugins, Sdk.ILlmService llm, Plugins.SettingsHub pluginDrafts,
        Func<List<(string Id, Sdk.TrayItem Item)>> trayItems, System.Net.Http.HttpClient http)
    {
        _store = store;
        _pluginDrafts = pluginDrafts;
        InitializeComponent();
        _pages = new SettingsPage[] { PanelGeneral, PanelActions, PanelApps, PanelPlugins, PanelServices, PanelProxy };

        var ctx = new SettingsContext
        {
            Store = store, Plugins = plugins, Llm = llm, PluginDrafts = pluginDrafts, TrayItems = trayItems, Owner = this, Http = http,
            Status = text => StatusText.Text = text,
            ShowPlugin = ShowPluginsPage,
        };
        foreach (var p in _pages)
        {
            p.Attach(ctx);
            p.Edited += UpdateSaveBar;
        }
        LoadPages();

        VersionText.Text = "v" + typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3);
        SourceInitialized += (_, _) => Acrylic.Apply(this, BackdropKind.Acrylic, ThemeManager.IsDark, roundCorners: true);
        ContentScroll.SizeChanged += (_, _) => UpdateSaveBarGlass();   // the bar's place inside the page moves with the window height...
        ContentScroll.ScrollChanged += (_, _) => UpdateSaveBarGlass(); // ...and with scrolling
        ThemeManager.Changed += OnThemeChanged;
        _pluginDrafts.Changed += UpdateSaveBar;
        Closed += (_, _) =>
        {
            ThemeManager.Changed -= OnThemeChanged;
            _pluginDrafts.Changed -= UpdateSaveBar;
            _pluginDrafts.DiscardAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                if (IsDirty) SaveNow();
                e.Handled = true;
            }
        };
    }

    private void LoadPages()
    {
        _loaded = ConfigStore.Clone(_store.Current);
        foreach (var p in _pages) p.Load(_loaded);
        UpdateSaveBar();
    }

    /// <summary>Edits are held in the pages until saved; changing something back clears the flag again.</summary>
    private bool IsDirty => _pages.Any(p => p.IsDirty) || _pluginDrafts.AnyPending;

    private void SaveNow()
    {
        // Three-way merge onto what is stored now: only the fields the user changed on a page are written.
        var edited = ConfigStore.Clone(_loaded);
        foreach (var p in _pages.Where(p => p.IsDirty)) p.Apply(edited);
        var loaded = _loaded;
        _store.Update(c => MergeEdits(c, edited, loaded));
        _loaded = ConfigStore.Clone(_store.Current);
        foreach (var p in _pages) p.MarkSaved();
        _pluginDrafts.ApplyAll(); // plugin settings edited in their cards
        UpdateSaveBar();
        StatusText.Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>Copy into <paramref name="target"/> every persisted field whose value in <paramref name="edited"/> differs from <paramref name="loaded"/>.</summary>
    internal static void MergeEdits(AppConfig target, AppConfig edited, AppConfig loaded)
    {
        foreach (var p in typeof(AppConfig).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true)) continue;
            var mine = p.GetValue(edited);
            if (System.Text.Json.JsonSerializer.Serialize(mine) == System.Text.Json.JsonSerializer.Serialize(p.GetValue(loaded))) continue;
            p.SetValue(target, mine);
        }
    }

    private void SaveChanges_Click(object sender, RoutedEventArgs e) => SaveNow();

    private void CancelChanges_Click(object sender, RoutedEventArgs e)
    {
        _pluginDrafts.DiscardAll();
        LoadPages(); // plugin switches and settings views start again from the live settings
        StatusText.Text = "已撤销未保存的更改";
    }

    /// <summary>Called by the tray when the enabled flag flips elsewhere.</summary>
    public void RefreshFromStore()
    {
        PanelGeneral.SyncEnabled(_store.Current.Enabled);
        UpdateSaveBar();
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

    // ------------------------------------------------------------------ save bar

    private void UpdateSaveBar()
    {
        bool dirty = IsDirty;
        SaveBar.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        // Room under the last card only while the bar floats over it.
        ContentRoot.Margin = new Thickness(32, 12, 32, dirty ? 96 : 32);
        if (dirty) Dispatcher.InvokeAsync(UpdateSaveBarGlass, DispatcherPriority.Loaded);
    }

    private void SaveBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSaveBarGlass();

    /// <summary>
    /// The bar's frosted glass is a live, blurred copy of the page under it: point the VisualBrush at the bar's own
    /// rect inside the scroll viewer (padded, so the blur has material to sample at the rim) and clip the rounded corners.
    /// </summary>
    private void UpdateSaveBarGlass()
    {
        if (SaveBar.Visibility != Visibility.Visible || SaveBarGlass.ActualWidth <= 0 || SaveBarGlass.ActualHeight <= 0) return;
        const double pad = 40; // = the glass rectangle's negative margin
        // The page panel itself, not the ScrollViewer: TransformToVisual then already includes the scroll offset.
        SaveBarGlassBrush.Visual ??= ContentRoot;
        var rect = SaveBarGlass.TransformToVisual(ContentRoot).TransformBounds(new Rect(0, 0, SaveBarGlass.ActualWidth, SaveBarGlass.ActualHeight));
        SaveBarGlassBrush.Viewbox = new Rect(rect.X - pad, rect.Y - pad, rect.Width + 2 * pad, rect.Height + 2 * pad);
        SaveBarGlass.Clip = new RectangleGeometry(new Rect(0, 0, SaveBarGlass.ActualWidth, SaveBarGlass.ActualHeight), 13, 13);
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
        PanelProxy.Visibility = NavProxy.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Install a plugin from a .zip / .dll path or a GitHub repository (asks for confirmation first).</summary>
    public void InstallPlugin(string source)
    {
        NavPlugins.IsChecked = true;
        PanelPlugins.InstallFrom(source);
    }

    public void ShowPluginsPage(string? pluginId = null)
    {
        NavPlugins.IsChecked = true;
        if (pluginId != null) PanelPlugins.Open(pluginId);
    }

    // ------------------------------------------------------------------ theme

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
}
