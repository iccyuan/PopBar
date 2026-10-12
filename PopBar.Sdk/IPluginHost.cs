using System.Net.Http;
using System.Windows.Threading;

namespace PopBar.Sdk;

/// <summary>Services PopBar provides to plugins. All members are thread-safe unless noted.</summary>
public interface IPluginHost
{
    IPluginLogger Log { get; }
    /// <summary>Per-plugin JSON settings (%AppData%\PopBar\plugins\{id}.json).</summary>
    IPluginStorage Storage { get; }
    /// <summary>Shared HttpClient (proxy-aware, 30s default timeout).</summary>
    HttpClient Http { get; }
    /// <summary>Access to the LLM configured in PopBar settings (may be unconfigured: check <see cref="ILlmService.IsConfigured"/>).</summary>
    ILlmService Llm { get; }
    /// <summary>UI thread dispatcher for building WPF elements.</summary>
    Dispatcher Dispatcher { get; }
    /// <summary>Host-styled controls for settings views (key / hotkey capture, app picker, icons...).</summary>
    IHostUi Ui { get; }
    /// <summary>"dark" or "light" - current bar theme.</summary>
    string Theme { get; }
    /// <summary>Directory for plugin data files (%AppData%\PopBar\plugins\{id}\).</summary>
    string DataDir { get; }
    /// <summary>Capabilities shared between plugins (translator, OCR...). See <see cref="IServiceRegistry"/>.</summary>
    IServiceRegistry Services { get; }
    /// <summary>Settings-window session: views edit a draft and apply it on Save. See <see cref="ISettingsSession"/>.</summary>
    ISettingsSession Settings { get; }

    // ---- UI ----
    /// <summary>Open a floating result panel anchored near the selection. Returns on the calling thread; the panel is created on the UI thread.</summary>
    IPanel ShowPanel(PanelOptions options);
    /// <summary>Show the standard PopBar action bar for arbitrary text (e.g. OCR output) at a screen point.</summary>
    void ShowPopup(SelectionContext context);
    /// <summary>Let the user drag a rectangle on screen; returns PNG bytes + screen rect, or null if cancelled.</summary>
    Task<RegionCapture?> CaptureScreenRegionAsync();
    /// <summary>
    /// Pause PopBar's selection detection (e.g. while your own full-screen overlay handles mouse drags).
    /// Dispose the returned token to resume. Nested calls are counted.
    /// </summary>
    IDisposable SuppressSelection();
    /// <summary>Open the settings window on this plugin's section.</summary>
    void OpenSettings();

    // ---- clipboard / input ----
    void CopyToClipboard(string text);
    /// <summary>Replace the current selection of <paramref name="targetWindow"/> with text (clipboard is restored afterwards).</summary>
    void PasteText(string text, IntPtr targetWindow);
    /// <summary>Bring a window to the foreground reliably.</summary>
    void ActivateWindow(IntPtr window);
    /// <summary>First visible top-level window of a process (by exe name, e.g. "notepad.exe"), or IntPtr.Zero.</summary>
    IntPtr FindWindowByProcess(string processName);

    // ---- integration ----
    /// <summary>Register a global hotkey ("Ctrl+Alt+O"). Returns false if the combination is taken.</summary>
    bool RegisterHotkey(string id, string gesture, Action callback);
    void UnregisterHotkey(string id);
    /// <summary>Add an entry to the tray context menu. <paramref name="icon"/> is a name from <see cref="IHostUi.IconNames"/>.</summary>
    void AddTrayMenuItem(string id, string label, Action callback, string? icon = null);
    /// <summary>
    /// Call after your <see cref="IPopBarPlugin.GetActions"/> result changed (items added, removed, renamed): new actions
    /// join the bar right away, removed ones leave it, and an open settings window refreshes its action list.
    /// </summary>
    void RefreshActions();
    /// <summary>Run any registered action (own or another plugin's, e.g. "translate:panel") for a context.</summary>
    Task RunActionAsync(string actionId, SelectionContext context);
}

public interface IPluginLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

public interface IPluginStorage
{
    /// <summary>Deserialize the plugin's settings object, or a new T when nothing is stored yet.</summary>
    T Get<T>() where T : class, new();
    void Set<T>(T value) where T : class;
}

public sealed record RegionCapture(byte[] Png, int X, int Y, int Width, int Height);

public sealed class PanelOptions
{
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "circle";
    /// <summary>Tag next to the title (provider / model). See <see cref="IPanel.SetBadge"/>.</summary>
    public string? Badge { get; init; }
    /// <summary>Screen point to anchor near (usually the selection point).</summary>
    public int AnchorX { get; init; }
    public int AnchorY { get; init; }
    /// <summary>Shown collapsed above the result, e.g. the source text.</summary>
    public string? Source { get; init; }
    /// <summary>Panel width in DIP. Plugins may change it later with <see cref="IPanel.SetSize"/>.</summary>
    public double Width { get; init; } = 480;
    /// <summary>Allow the user to edit the result text (e.g. OCR output).</summary>
    public bool Editable { get; init; }
    /// <summary>Close when the user clicks outside the panel (unless pinned). Default true.</summary>
    public bool CloseOnOutsideClick { get; init; } = true;
    /// <summary>Start pinned (outside clicks never close it).</summary>
    public bool Pinned { get; init; }
    /// <summary>Show the built-in text result area. Set false when you supply your own content via <see cref="IPanel.SetContent"/>.</summary>
    public bool ShowTextArea { get; init; } = true;
    /// <summary>Maximum height of the body before it scrolls (DIP).</summary>
    public double MaxBodyHeight { get; init; } = 420;
    /// <summary>
    /// Just the container: no title bar, footer or padding. The panel keeps its rounded frame, shadow, placement,
    /// Esc / outside-click dismissal and the busy line; everything inside is the element you pass to
    /// <see cref="IPanel.SetContent"/>, unwrapped (no scroll viewer, no height cap) and clipped to the corners.
    /// Empty areas of your content drag the panel. Text / status / buttons / error have nowhere to show here.
    /// </summary>
    public bool Bare { get; init; }
}
