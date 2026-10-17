using System.Net.Http;
using System.Windows.Threading;

namespace PopBar.Sdk;

/// <summary>
/// Services PopBar provides to plugins. Every member may be called from any thread. Members marked "waits for the UI
/// thread" run their work there and block the calling worker until it is done; don't call them from a worker while
/// holding a lock, or while the UI thread is itself waiting for that worker (it would deadlock). From the UI thread
/// they run inline. The *Async variants never block.
/// </summary>
public interface IPluginHost
{
    IPluginLogger Log { get; }
    /// <summary>Per-plugin JSON settings (%AppData%\PopBar\plugins\{id}.json).</summary>
    IPluginStorage Storage { get; }
    /// <summary>Shared HttpClient (proxy-aware, 60 s default timeout).</summary>
    HttpClient Http { get; }
    /// <summary>Access to the LLM configured in PopBar settings (may be unconfigured: check <see cref="ILlmService.IsConfigured"/>).</summary>
    ILlmService Llm { get; }
    /// <summary>UI thread dispatcher for building WPF elements.</summary>
    Dispatcher Dispatcher { get; }
    /// <summary>Host-styled dialogs, inputs, icons and model pickers (UI thread only). See <see cref="IHostUi"/>.</summary>
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
    /// <summary>
    /// Open a floating result panel anchored near the selection. Waits for the UI thread (the panel is created there);
    /// prefer <see cref="ShowPanelAsync"/> from async code. The returned panel's methods can be called from any thread.
    /// </summary>
    IPanel ShowPanel(PanelOptions options);
    /// <summary><see cref="ShowPanel"/> without blocking the calling thread.</summary>
    Task<IPanel> ShowPanelAsync(PanelOptions options);
    /// <summary>Show the standard PopBar action bar for arbitrary text (e.g. OCR output) at a screen point.</summary>
    void ShowPopup(SelectionContext context);
    /// <summary>Let the user drag a rectangle on screen; returns PNG bytes + screen rect, or null if cancelled. Never blocks the caller.</summary>
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
    /// <summary>Bring a window to the foreground reliably, unhiding (tray) or restoring (minimised) it first if needed.</summary>
    void ActivateWindow(IntPtr window);
    /// <summary>
    /// A visible application window of a process (exe name or path, e.g. "notepad.exe"); failing that, its main window
    /// hidden in the tray; else IntPtr.Zero. With <paramref name="windowClass"/> (as reported by <see cref="IHostInputs.WindowPicker"/>)
    /// a window of that class is preferred when the program has several.
    /// </summary>
    IntPtr FindWindowByProcess(string processName, string? windowClass = null);

    // ---- integration ----
    /// <summary>
    /// Register a global hotkey ("Ctrl+Alt+O"). Returns false if the combination is taken. Waits for the UI thread
    /// (hotkeys belong to its message window); <paramref name="callback"/> runs on the UI thread.
    /// </summary>
    bool RegisterHotkey(string id, string gesture, Action callback);
    /// <summary>Remove a hotkey. Never blocks: from a worker it is queued to the UI thread, in call order.</summary>
    void UnregisterHotkey(string id);
    /// <summary>
    /// Register a tray entry: it can be listed in the tray's right-click menu, picked as the tray icon's left-click action,
    /// and run as <c>PopBar.exe --run &lt;plugin&gt;:&lt;id&gt;</c>. <see cref="TrayItem"/> says which of those it supports.
    /// </summary>
    void AddTrayItem(TrayItem item);
    /// <summary>Shorthand for <see cref="AddTrayItem"/> with the defaults (menu, shown by default, left click allowed).</summary>
    void AddTrayMenuItem(string id, string label, Action callback, string? icon = null) =>
        AddTrayItem(new TrayItem { Id = id, Label = label, Callback = callback, Icon = icon });
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
