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
    /// <summary>"dark" or "light" - current bar theme.</summary>
    string Theme { get; }
    /// <summary>Directory for plugin data files (%AppData%\PopBar\plugins\{id}\).</summary>
    string DataDir { get; }

    // ---- UI ----
    /// <summary>Open a floating result panel anchored near the selection. Returns on the calling thread; the panel is created on the UI thread.</summary>
    IPanel ShowPanel(PanelOptions options);
    /// <summary>Show the standard PopBar action bar for arbitrary text (e.g. OCR output) at a screen point.</summary>
    void ShowPopup(SelectionContext context);
    /// <summary>Let the user drag a rectangle on screen; returns PNG bytes + screen rect, or null if cancelled.</summary>
    Task<RegionCapture?> CaptureScreenRegionAsync();
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
    /// <summary>Add an entry to the tray context menu.</summary>
    void AddTrayMenuItem(string id, string label, Action callback);
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
    /// <summary>Screen point to anchor near (usually the selection point).</summary>
    public int AnchorX { get; init; }
    public int AnchorY { get; init; }
    /// <summary>Shown collapsed above the result, e.g. the source text.</summary>
    public string? Source { get; init; }
    public double Width { get; init; } = 440;
    /// <summary>Allow the user to edit the result text (e.g. OCR output).</summary>
    public bool Editable { get; init; }
}
