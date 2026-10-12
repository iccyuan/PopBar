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
    public required HttpClient Http { get; init; }
    public required HotkeyManager Hotkeys { get; init; }
    public required Action<string, string, string?, Action> AddTrayItem { get; init; }
    public required Action<SelectionContext> ShowPopup { get; init; }
    public required Action<string> OpenSettings { get; init; }
    public required Func<string, SelectionContext, Task> RunAction { get; init; }
    public required Action<string> ResyncActions { get; init; }
    public required PanelRegistry Panels { get; init; }
    public ServiceRegistry Services { get; } = new();
    public SettingsHub SettingsHub { get; } = new();

    public static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = WebRequest.GetSystemWebProxy(), UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = ConnectAsync,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PopBar/0.2 (+https://github.com/iccyuan/PopBar)");
        return http;
    }

    /// <summary>
    /// .NET connects through a dual-mode IPv6 socket by default, i.e. to "::ffff:a.b.c.d" for IPv4 hosts. Some
    /// machines (VPN / TUN adapters, filtering drivers) reject exactly that with WSAEACCES (10013) while plain IPv4
    /// sockets work - curl and browsers are fine, PopBar is not. Connect with a socket of the address's own family.
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
        Exception? last = null;
        foreach (var ip in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose(); // cancelled: don't leak, don't try the next address
                throw;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
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
}

/// <summary>Pending plugin-settings drafts of the open settings window; Save applies them, Cancel drops them.</summary>
internal sealed class SettingsHub
{
    private readonly Dictionary<string, Action> _apply = new();
    private readonly HashSet<string> _pending = new();

    /// <summary>Some plugin draft changed (UI thread).</summary>
    public event Action? Changed;
    public bool AnyPending => _pending.Count > 0;

    public void Register(string plugin, Action apply) => _apply[plugin] = apply;
    public void MarkChanged(string plugin) { _pending.Add(plugin); Changed?.Invoke(); }

    public void ApplyAll()
    {
        foreach (var id in _pending.ToList())
        {
            try { if (_apply.TryGetValue(id, out var a)) a(); }
            catch (Exception ex) { PopBar.Log.Error($"plugin {id} apply settings", ex); }
        }
        _pending.Clear();
    }

    /// <summary>Drop all drafts; the window rebuilds the views (which register again).</summary>
    public void DiscardAll() { _pending.Clear(); _apply.Clear(); }
}

internal sealed class SettingsSession(SettingsHub hub, string plugin) : ISettingsSession
{
    public void Changed() => hub.MarkChanged(plugin);
    public void OnApply(Action apply) => hub.Register(plugin, apply);
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
        DataDir = Path.Combine(PluginStorage.PluginsDir, pluginId);
        Settings = new SettingsSession(s.SettingsHub, pluginId);
        Ui = new HostUi(s.Llm);
    }

    public IPluginLogger Log => _log;
    public IPluginStorage Storage => _storage;
    public HttpClient Http => _s.Http;
    public ILlmService Llm => _s.Llm;
    public Dispatcher Dispatcher => _s.Dispatcher;
    public IHostUi Ui { get; }
    public string Theme => ThemeManager.Effective;
    public string DataDir { get; }
    public IServiceRegistry Services => _s.Services;
    public ISettingsSession Settings { get; }

    public IPanel ShowPanel(PanelOptions options)
    {
        return _s.Dispatcher.Invoke(() =>
        {
            var w = new PanelWindow(options, Theme);
            _s.Panels.Add(w);
            w.ShowAt(options.AnchorX, options.AnchorY);
            return (IPanel)w;
        });
    }

    public void ShowPopup(SelectionContext context) => _s.ShowPopup(context);

    public async Task<RegionCapture?> CaptureScreenRegionAsync()
    {
        _s.Panels.Suppress();
        try
        {
            var task = _s.Dispatcher.Invoke(() =>
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

    /// <summary>A visible window of the process if it has one, else its main window hidden in the tray (see <see cref="Core.RunningApps"/>).</summary>
    public IntPtr FindWindowByProcess(string processName)
    {
        IntPtr visible = IntPtr.Zero, hidden = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            bool isVisible = IsWindowVisible(h);
            if (isVisible ? visible != IntPtr.Zero : (hidden != IntPtr.Zero || !Core.RunningApps.IsTrayMainWindow(h))) return true;
            if (!string.Equals(GetWindowProcessName(h), processName, StringComparison.OrdinalIgnoreCase)) return true;
            if (isVisible) visible = h; else hidden = h;
            return visible == IntPtr.Zero;
        }, IntPtr.Zero);
        return visible != IntPtr.Zero ? visible : hidden;
    }

    public bool RegisterHotkey(string id, string gesture, Action callback) =>
        _s.Dispatcher.Invoke(() => _s.Hotkeys.Register($"{_pluginId}:{id}", gesture, callback));

    public void UnregisterHotkey(string id) => _s.Dispatcher.Invoke(() => _s.Hotkeys.Unregister($"{_pluginId}:{id}"));

    public void AddTrayMenuItem(string id, string label, Action callback, string? icon = null) => _s.AddTrayItem($"{_pluginId}:{id}", label, icon, callback);

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
