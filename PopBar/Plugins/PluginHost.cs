using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows.Threading;
using PopBar.Config;
using PopBar.Core;
using PopBar.Sdk;
using PopBar.UI;
using static PopBar.Native.NativeMethods;

namespace PopBar.Plugins;

/// <summary>Everything the host must provide to plugins that lives outside PluginHost itself (App wires these).</summary>
internal sealed class HostServices
{
    public required ConfigStore Store { get; init; }
    public required Dispatcher Dispatcher { get; init; }
    public required ILlmService Llm { get; init; }
    /// <summary>The HttpClients by proxy route: PopBar's way, direct, through the proxy; and one per AI service.</summary>
    public required ProxyManager Proxies { get; init; }
    public required HotkeyManager Hotkeys { get; init; }
    /// <summary>Registers a plugin's tray entry under its full id ("plugin:item").</summary>
    public required Action<string, TrayItem> AddTrayItem { get; init; }
    /// <summary>Drop every tray entry of a plugin (by plugin id), when it is unloaded.</summary>
    public required Action<string> RemoveTrayItems { get; init; }
    public required Action<SelectionContext> ShowPopup { get; init; }
    public required Action<string> OpenSettings { get; init; }
    public required Func<string, SelectionContext, Task> RunAction { get; init; }
    public required Action<string> ResyncActions { get; init; }
    public required PanelRegistry Panels { get; init; }
    public ServiceRegistry Services { get; } = new();
    public SettingsHub SettingsHub { get; } = new();
}

/// <summary>Process-wide registry behind <see cref="IPluginHost.Services"/>: one implementation per contract type.</summary>
internal sealed class ServiceRegistry : IServiceRegistry
{
    private readonly Dictionary<Type, object> _map = new();

    public void Provide<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_map) _map[typeof(T)] = service;
    }

    public T? Get<T>() where T : class
    {
        lock (_map) return _map.TryGetValue(typeof(T), out var s) ? (T)s : null;
    }

    /// <summary>Withdraw the implementations that come from an unloaded plugin's assembly.</summary>
    public void RemoveFrom(System.Reflection.Assembly asm)
    {
        lock (_map) foreach (var k in _map.Where(kv => kv.Value.GetType().Assembly == asm).Select(kv => kv.Key).ToList()) _map.Remove(k);
    }
}

/// <summary>Pending plugin-settings drafts of the open settings window; Save applies them, Cancel drops them.</summary>
internal sealed class SettingsHub
{
    private readonly Dictionary<string, Action> _apply = new();
    /// <summary>For drafts registered with their object: how the draft reads now, and how it read when unchanged.</summary>
    private readonly Dictionary<string, (Func<string> Snapshot, string Baseline)> _tracked = new();
    private readonly HashSet<string> _pending = new();

    /// <summary>Some plugin draft changed (UI thread).</summary>
    public event Action? Changed;
    public bool AnyPending => _pending.Count > 0;

    public void Register(string plugin, Action apply, Func<string>? snapshot = null)
    {
        _apply[plugin] = apply;
        if (snapshot != null) _tracked[plugin] = (snapshot, snapshot());
        else _tracked.Remove(plugin);
    }

    /// <summary>A draft was edited; a tracked one that is back to where it started is no longer pending.</summary>
    public void MarkChanged(string plugin)
    {
        if (_tracked.TryGetValue(plugin, out var t) && Same(t.Snapshot, t.Baseline)) _pending.Remove(plugin);
        else _pending.Add(plugin);
        Changed?.Invoke();
    }

    private static bool Same(Func<string> snapshot, string baseline)
    {
        try { return snapshot() == baseline; }
        catch { return false; }
    }

    public void ApplyAll()
    {
        foreach (var id in _pending.ToList())
        {
            try { if (_apply.TryGetValue(id, out var a)) a(); }
            catch (Exception ex) { PopBar.Log.Error($"plugin {id} apply settings", ex); }
            // saved: what the draft holds now is the new unchanged state
            if (_tracked.TryGetValue(id, out var t))
                try { _tracked[id] = (t.Snapshot, t.Snapshot()); } catch { }
        }
        _pending.Clear();
    }

    /// <summary>Forget an unloaded plugin's draft.</summary>
    public void Remove(string plugin) { _pending.Remove(plugin); _apply.Remove(plugin); _tracked.Remove(plugin); Changed?.Invoke(); }

    /// <summary>Drop all drafts; the window rebuilds the views (which register again).</summary>
    public void DiscardAll() { _pending.Clear(); _apply.Clear(); _tracked.Clear(); }
}

internal sealed class SettingsSession(SettingsHub hub, string plugin) : ISettingsSession
{
    public void Changed() => hub.MarkChanged(plugin);
    public void OnApply(Action apply) => hub.Register(plugin, apply);
    public void OnApply(object draft, Action apply)
    {
        ArgumentNullException.ThrowIfNull(draft);
        hub.Register(plugin, apply, () => System.Text.Json.JsonSerializer.Serialize(draft, draft.GetType()));
    }
}

/// <summary>Tracks open panels so outside clicks can close the unpinned ones.</summary>
internal sealed class PanelRegistry : IPanelRegistry
{
    private readonly List<PanelWindow> _open = new();
    private readonly object _gate = new();

    /// <summary>While true the engine ignores mouse selections (a region-capture overlay is up).</summary>
    private int _suppress;
    /// <summary>True while at least one suppressor is active (reference counted).</summary>
    public bool SuppressCapture => Volatile.Read(ref _suppress) > 0;
    public void Suppress() => Interlocked.Increment(ref _suppress);
    public void Release() { if (Interlocked.Decrement(ref _suppress) < 0) Interlocked.Exchange(ref _suppress, 0); }

    public void Add(PanelWindow p)
    {
        lock (_gate) _open.Add(p);
        p.PanelClosed += () => { lock (_gate) _open.Remove(p); };
    }

    /// <summary>Called on the UI thread for a click at a screen point outside the bar.</summary>
    public void OnOutsideClick(int x, int y)
    {
        PanelWindow[] snapshot;
        lock (_gate) snapshot = _open.ToArray();
        foreach (var p in snapshot)
        {
            if (p.IsPinned) continue;
            if (p.ContainsScreenPoint(x, y)) continue;
            p.Close();
        }
    }

    public bool ContainsPoint(int x, int y)
    {
        PanelWindow[] snapshot;
        lock (_gate) snapshot = _open.ToArray();
        return snapshot.Any(p => p.ContainsScreenPoint(x, y));
    }
}

internal sealed class PluginHost : IPluginHost
{
    private readonly string _pluginId;
    private readonly HostServices _s;
    private readonly PluginLogger _log;
    private readonly PluginStorage _storage;

    public PluginHost(string pluginId, HostServices s)
    {
        _pluginId = pluginId;
        _s = s;
        _log = new PluginLogger(pluginId);
        _storage = new PluginStorage(pluginId);
        DataDir = PluginStorage.DataDirOf(pluginId);
        Settings = new SettingsSession(s.SettingsHub, pluginId);
        Ui = new HostUi(s.Llm);
    }

    public IPluginLogger Log => _log;
    public IPluginStorage Storage => _storage;
    public HttpClient Http => _s.Proxies.Main;
    public HttpClient HttpFor(ProxyRoute route) => _s.Proxies.For(route);
    public ILlmService Llm => _s.Llm;
    public Dispatcher Dispatcher => _s.Dispatcher;
    public IHostUi Ui { get; }
    public string Theme => ThemeManager.Effective;

    public bool RegisterIcon(string name, string svg)
    {
        var why = Icons.Register(name, svg ?? "", _pluginId);
        if (why != null) _log.Warn($"icon '{name}' not registered: {why}");
        return why == null;
    }

    private Action<string>? _themeChanged;
    private readonly object _themeGate = new();

    /// <summary>Hooked to <see cref="ThemeManager.Changed"/> only while the plugin listens, so a dropped host is not kept alive.</summary>
    public event Action<string>? ThemeChanged
    {
        add
        {
            lock (_themeGate)
            {
                if (_themeChanged == null) ThemeManager.Changed += OnThemeChanged;
                _themeChanged += value;
            }
        }
        remove
        {
            lock (_themeGate)
            {
                _themeChanged -= value;
                if (_themeChanged == null) ThemeManager.Changed -= OnThemeChanged;
            }
        }
    }

    private void OnThemeChanged(string theme)
    {
        try { _themeChanged?.Invoke(theme); }
        catch (Exception ex) { _log.Error("ThemeChanged handler", ex); }
    }
    public string DataDir { get; }
    public IServiceRegistry Services => _s.Services;
    public ISettingsSession Settings { get; }

    public IPanel ShowPanel(PanelOptions options) => _s.Dispatcher.Invoke(() => OpenPanel(options));

    public async Task<IPanel> ShowPanelAsync(PanelOptions options) => await _s.Dispatcher.InvokeAsync(() => OpenPanel(options));

    private IPanel OpenPanel(PanelOptions options)
    {
        var w = new PanelWindow(options, Theme);
        _s.Panels.Add(w);
        w.ShowAt(options.AnchorX, options.AnchorY);
        return w;
    }

    public void ShowPopup(SelectionContext context) => _s.ShowPopup(context);

    public async Task<RegionCapture?> CaptureScreenRegionAsync()
    {
        _s.Panels.Suppress();
        try
        {
            // Queued, not Invoke: a worker asking for a capture must not hold its thread until the UI gets to it.
            var task = await _s.Dispatcher.InvokeAsync(() =>
            {
                var w = new RegionCaptureWindow();
                w.Show();
                return w.Result;
            });
            return await task;
        }
        finally
        {
            // Small grace period so the mouse-up that ended the drag is not treated as a selection.
            _ = Task.Delay(300).ContinueWith(_ => _s.Panels.Release());
        }
    }

    public IDisposable SuppressSelection()
    {
        _s.Panels.Suppress();
        return new Releaser(() => _ = Task.Delay(300).ContinueWith(_ => _s.Panels.Release()));
    }

    private sealed class Releaser : IDisposable
    {
        private Action? _a;
        public Releaser(Action a) => _a = a;
        public void Dispose() => Interlocked.Exchange(ref _a, null)?.Invoke();
    }

    public void OpenSettings() => _s.OpenSettings(_pluginId);

    public void CopyToClipboard(string text) => InputSender.CopyToClipboard(text);
    public void PasteText(string text, IntPtr targetWindow) => InputSender.PasteText(text, targetWindow);
    public void ActivateWindow(IntPtr window) => ShowAndForeground(window);

    /// <summary>
    /// The window of the process to paste into, best first: a visible window of <paramref name="windowClass"/>, a visible
    /// titled application window, any other visible top-level window, then its main window hidden in the tray (see
    /// <see cref="Core.RunningApps"/>; an untitled one counts here, as WeChat's is until first shown). Tooltips, owned
    /// popups and tool windows never count, so a stray popup of the program cannot be mistaken for it.
    /// </summary>
    public IntPtr FindWindowByProcess(string processName, string? windowClass = null)
    {
        var exe = System.IO.Path.GetFileName(processName);
        if (exe.Length == 0) return IntPtr.Zero;
        IntPtr byClass = IntPtr.Zero, app = IntPtr.Zero, plain = IntPtr.Zero, hidden = IntPtr.Zero, hiddenByClass = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            bool visible = IsWindowVisible(h);
            if (!visible && (hidden != IntPtr.Zero && windowClass == null || !Core.RunningApps.IsTrayMainWindow(h, allowUntitled: true))) return true;
            if (!string.Equals(GetWindowProcessName(h), exe, StringComparison.OrdinalIgnoreCase)) return true;
            bool classMatch = windowClass != null && GetWindowClass(h) == windowClass;
            if (!visible)
            {
                if (hidden == IntPtr.Zero) hidden = h;
                if (classMatch && hiddenByClass == IntPtr.Zero) hiddenByClass = h;
                return true;
            }
            if (IsCloaked(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || (GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
            if (classMatch && byClass == IntPtr.Zero) byClass = h;
            if (app == IntPtr.Zero && GetWindowTitle(h).Length > 0) app = h;
            else if (plain == IntPtr.Zero) plain = h;
            return byClass == IntPtr.Zero && (windowClass != null || app == IntPtr.Zero);
        }, IntPtr.Zero);
        if (byClass != IntPtr.Zero) return byClass;
        if (app != IntPtr.Zero) return app;
        if (plain != IntPtr.Zero) return plain;
        return hiddenByClass != IntPtr.Zero ? hiddenByClass : hidden;
    }

    public bool RegisterHotkey(string id, string gesture, Action callback)
    {
        lock (_hotkeys) _hotkeys.Add(id);
        return _s.Dispatcher.Invoke(() => _s.Hotkeys.Register($"{_pluginId}:{id}", gesture, callback));
    }

    /// <summary>Hotkey ids this plugin registered, released with it.</summary>
    private readonly HashSet<string> _hotkeys = new();

    /// <summary>
    /// Undo what the plugin registered through this host, after it was disposed (unload, or switched off): hotkeys,
    /// tray entries, settings drafts, theme listeners, icons, and services / model providers implemented in its assembly. UI thread.
    /// </summary>
    public void Release(System.Reflection.Assembly pluginAssembly)
    {
        string[] keys;
        lock (_hotkeys) { keys = _hotkeys.ToArray(); _hotkeys.Clear(); }
        foreach (var k in keys) _s.Hotkeys.Unregister($"{_pluginId}:{k}");
        _s.RemoveTrayItems(_pluginId);
        _s.SettingsHub.Remove(_pluginId);
        _s.Services.RemoveFrom(pluginAssembly);
        Icons.Unregister(_pluginId);
        lock (_themeGate)
        {
            if (_themeChanged != null) ThemeManager.Changed -= OnThemeChanged;
            _themeChanged = null;
        }
        if (_s.Llm is Core.LlmService llm) llm.RemoveProvidersFrom(pluginAssembly);
    }

    public void UnregisterHotkey(string id)
    {
        lock (_hotkeys) _hotkeys.Remove(id);
        if (_s.Dispatcher.CheckAccess()) _s.Hotkeys.Unregister($"{_pluginId}:{id}");
        else _s.Dispatcher.InvokeAsync(() => _s.Hotkeys.Unregister($"{_pluginId}:{id}"));
    }

    public void AddTrayItem(TrayItem item) => _s.AddTrayItem($"{_pluginId}:{item.Id}", item);

    public Task RunActionAsync(string actionId, SelectionContext context) => _s.RunAction(actionId, context);

    public void RefreshActions() => _s.ResyncActions(_pluginId);

    private sealed class PluginLogger : IPluginLogger
    {
        private readonly string _id;
        public PluginLogger(string id) => _id = id;
        public void Info(string message) => PopBar.Log.Info($"[{_id}] {message}");
        public void Warn(string message) => PopBar.Log.Warn($"[{_id}] {message}");
        public void Error(string message, Exception? ex = null) => PopBar.Log.Error($"[{_id}] {message}", ex);
    }
}
