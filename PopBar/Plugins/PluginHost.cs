using System.IO;
using System.Net;
using System.Net.Http;
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
    public required Action<string, string, Action> AddTrayItem { get; init; }
    public required Action<SelectionContext> ShowPopup { get; init; }
    public required Action<string> OpenSettings { get; init; }
    public required Func<string, SelectionContext, Task> RunAction { get; init; }
    public required PanelRegistry Panels { get; init; }

    public static HttpClient CreateHttp()
    {
        var handler = new HttpClientHandler { Proxy = WebRequest.GetSystemWebProxy(), UseProxy = true, AutomaticDecompression = DecompressionMethods.All };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PopBar/0.2 (+https://github.com/iccyuan/PopBar)");
        return http;
    }
}

/// <summary>Tracks open panels so outside clicks can close the unpinned ones.</summary>
internal sealed class PanelRegistry : IPanelRegistry
{
    private readonly List<PanelWindow> _open = new();
    private readonly object _gate = new();

    /// <summary>While true the engine ignores mouse selections (a region-capture overlay is up).</summary>
    private volatile bool _suppress;
    public bool SuppressCapture { get => _suppress; set => _suppress = value; }

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
    }

    public IPluginLogger Log => _log;
    public IPluginStorage Storage => _storage;
    public HttpClient Http => _s.Http;
    public ILlmService Llm => _s.Llm;
    public Dispatcher Dispatcher => _s.Dispatcher;
    public IHostUi Ui { get; } = new HostUi();
    public string Theme => ThemeManager.Effective;
    public string DataDir { get; }

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
        _s.Panels.SuppressCapture = true;
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
            _ = Task.Delay(300).ContinueWith(_ => _s.Panels.SuppressCapture = false);
        }
    }

    public void OpenSettings() => _s.OpenSettings(_pluginId);

    public void CopyToClipboard(string text) => InputSender.CopyToClipboard(text);
    public void PasteText(string text, IntPtr targetWindow) => InputSender.PasteText(text, targetWindow);
    public void ActivateWindow(IntPtr window) => ForceForeground(window);

    public IntPtr FindWindowByProcess(string processName)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (string.Equals(GetWindowProcessName(h), processName, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public bool RegisterHotkey(string id, string gesture, Action callback) =>
        _s.Dispatcher.Invoke(() => _s.Hotkeys.Register($"{_pluginId}:{id}", gesture, callback));

    public void UnregisterHotkey(string id) => _s.Dispatcher.Invoke(() => _s.Hotkeys.Unregister($"{_pluginId}:{id}"));

    public void AddTrayMenuItem(string id, string label, Action callback) => _s.AddTrayItem($"{_pluginId}:{id}", label, callback);

    public Task RunActionAsync(string actionId, SelectionContext context) => _s.RunAction(actionId, context);

    private sealed class PluginLogger : IPluginLogger
    {
        private readonly string _id;
        public PluginLogger(string id) => _id = id;
        public void Info(string message) => PopBar.Log.Info($"[{_id}] {message}");
        public void Warn(string message) => PopBar.Log.Warn($"[{_id}] {message}");
        public void Error(string message, Exception? ex = null) => PopBar.Log.Error($"[{_id}] {message}", ex);
    }
}
