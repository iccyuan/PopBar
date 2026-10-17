using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
}
