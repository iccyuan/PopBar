using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PopBar.Config;

namespace PopBar.UI;

/// <summary>What the settings pages share: the stores they edit, the services they show, and a way back to the window.</summary>
internal sealed class SettingsContext
{
    public required ConfigStore Store { get; init; }
    public required Plugins.PluginManager Plugins { get; init; }
    public required Sdk.ILlmService Llm { get; init; }
    /// <summary>The host's proxy-aware HttpClient (plugin downloads).</summary>
    public required System.Net.Http.HttpClient Http { get; init; }
    /// <summary>Plugin settings drafts waiting for Save (edited inside the plugin cards).</summary>
    public required Plugins.SettingsHub PluginDrafts { get; init; }
    /// <summary>Tray entries registered by plugins, offered for the tray left-click action and the tray menu.</summary>
    public required Func<List<(string Id, Sdk.TrayItem Item)>> TrayItems { get; init; }
    public required Window Owner { get; init; }
    /// <summary>One line in the window's status area.</summary>
    public required Action<string> Status { get; init; }
    /// <summary>Switch to the plugins page and open that plugin's settings.</summary>
    public required Action<string?> ShowPlugin { get; init; }
    /// <summary>Host-styled dialogs (file pickers, editor dialog) for the pages.</summary>
    public Plugins.HostUi Ui => _ui ??= new Plugins.HostUi(Llm);
    private Plugins.HostUi? _ui;
}

/// <summary>
/// One page of the settings window. A page owns the <see cref="AppConfig"/> fields it shows: it fills its controls from
/// a config (<see cref="Load"/>), writes its fields back into one (<see cref="Apply"/>), and knows on its own whether the
/// user changed anything since it was loaded or saved (<see cref="IsDirty"/>). Settings a page writes to the store
/// immediately (previews, connection tests) are simply not part of <see cref="Apply"/>, or are followed by
/// <see cref="MarkSaved"/>, so they never show up as unsaved edits.
/// </summary>
internal abstract class SettingsPage : UserControl
{
    private string _baseline = "";
    private int _quiet;

    protected SettingsContext Ctx { get; private set; } = null!;

    /// <summary>The user changed something on this page (it may or may not still differ from what was loaded).</summary>
    public event Action? Edited;

    /// <summary>Called once, before the first <see cref="Load"/>: one-time setup that needs the context.</summary>
    public void Attach(SettingsContext ctx)
    {
        Ctx = ctx;
        OnAttach();
    }

    protected virtual void OnAttach() { }

    /// <summary>Show <paramref name="c"/>; the page is clean afterwards.</summary>
    public void Load(AppConfig c)
    {
        Quietly(() => OnLoad(c));
        MarkSaved();
    }

    protected abstract void OnLoad(AppConfig c);

    /// <summary>Write this page's fields (and only those) into <paramref name="c"/>.</summary>
    public abstract void Apply(AppConfig c);

    public bool IsDirty => _baseline.Length > 0 && Snapshot() != _baseline;

    /// <summary>What the page shows now counts as saved.</summary>
    public void MarkSaved() => _baseline = Snapshot();

    private string Snapshot()
    {
        var c = new AppConfig();
        Apply(c);
        return JsonSerializer.Serialize(c);
    }

    /// <summary>True while the page is filling its own controls: change events then are not edits.</summary>
    protected bool Loading => _quiet > 0;

    /// <summary>Run <paramref name="a"/> without its control changes counting as edits.</summary>
    protected void Quietly(Action a)
    {
        _quiet++;
        try { a(); }
        finally { _quiet--; }
    }

    protected void RaiseEdited()
    {
        // Controls raise change events while InitializeComponent runs (e.g. a Slider coercing its value), before any load.
        if (Loading || _baseline.Length == 0) return;
        Edited?.Invoke();
    }

    /// <summary>XAML handler for plain inputs: any change is a (possible) edit.</summary>
    protected void Changed(object sender, RoutedEventArgs e) => RaiseEdited();

    protected T Res<T>(string key) => (T)FindResource(key);

    /// <summary>Margin of the ContextMenu template's root grid (Themes/Modern.xaml), room for the shadow.</summary>
    private const double MenuShadowRoom = 10;
    private const double MenuGap = 4;

    /// <summary>
    /// The drop-down under a "＋ 添加…" button: one <c>RichMenuItem</c>-styled entry per choice (icon tile, title,
    /// a line of detail), its right edge lined up with the button's, which sits at the right of the page.
    /// </summary>
    protected void ShowAddMenu(FrameworkElement anchor, IEnumerable<(string Icon, string Title, string? Detail, Action OnClick)> items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = 260 };
        var style = Res<Style>("RichMenuItem");
        foreach (var (icon, title, detail, onClick) in items)
        {
            var item = new MenuItem { Style = style, Header = title, Tag = string.IsNullOrWhiteSpace(detail) ? null : detail, Icon = new IconImage { Icon = icon, Size = 16, Brush = Res<Brush>("AccentHoverBrush") } };
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        // Opening the menu by hand raises no ContextMenuOpening, so Frost would not snapshot the screen on its own.
        Frost.PrepareAtCursor();
        void Align(double width) => (menu.HorizontalOffset, menu.VerticalOffset) = (anchor.ActualWidth - width + MenuShadowRoom, MenuGap - MenuShadowRoom);
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Align(menu.DesiredSize.Width);
        // the measure above can run before the template is in place; correct with the real width once it is on screen
        menu.Opened += (_, _) => { if (Math.Abs(menu.ActualWidth - menu.DesiredSize.Width) > 0.5) Align(menu.ActualWidth); };
        menu.IsOpen = true;
    }
}
