using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using PopBar.Actions;
using PopBar.Config;
using PopBar.Core;
using PopBar.Plugins;
using PopBar.Plugins.Builtin;
using PopBar.Sdk;
using PopBar.UI;
using WinForms = System.Windows.Forms;

namespace PopBar;

public partial class App : Application
{
    private const string MutexName = @"Local\PopBar.SingleInstance";
    private const string ShowEventName = @"Local\PopBar.ShowSettings";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ContextMenuStrip? _trayMenu;
    private WinForms.ToolStripMenuItem? _enabledItem;
    private readonly ConfigStore _store = new();
    private PopupWindow? _popup;
    private SelectionEngine? _engine;
    private SettingsWindow? _settings;
    private PluginManager? _plugins;
    private HotkeyManager? _hotkeys;
    private HttpClient? _http;
    private LlmService? _llm;
    private readonly PanelRegistry _panels = new();
    private readonly List<(string Id, string Label, Action Callback)> _pluginTrayItems = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance is running: hand it the request (open settings, or "--run <trayItemId>"), then leave.
            try
            {
                int ri = Array.IndexOf(e.Args, "--run");
                if (ri >= 0 && ri + 1 < e.Args.Length)
                    File.WriteAllText(CommandFile, e.Args[ri + 1]);
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch { }
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("unhandled", ex.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ex) => { Log.Error("dispatcher", ex.Exception); ex.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("task", ex.Exception); ex.SetObserved(); };

        _store.Load();
        _store.Changed += OnConfigChanged;
        ApplyAutoStart(_store.Current.AutoStart);
        ThemeManager.BlurOpacity = _store.Current.BlurOpacity;
        ThemeManager.Apply(_store.Current.Theme);

        _popup = new PopupWindow();
        _popup.Preload();

        // Plugin infrastructure.
        _http = HostServices.CreateHttp();
        _llm = new LlmService(_store, _http);
        _hotkeys = new HotkeyManager();
        var services = new HostServices
        {
            Store = _store,
            Dispatcher = Dispatcher,
            Llm = _llm,
            Http = _http,
            Hotkeys = _hotkeys,
            Panels = _panels,
            AddTrayItem = (id, label, cb) => { _pluginTrayItems.Add((id, label, cb)); RebuildTrayMenu(); },
            ShowPopup = ctx => _engine?.ShowExternal(ctx),
            OpenSettings = pluginId => Dispatcher.InvokeAsync(() => ShowSettings(pluginId)),
            RunAction = (id, ctx) => _plugins!.ExecuteAsync(id, ctx),
        };
        _plugins = new PluginManager(_store, id => new PluginHost(id, services));
        ActionRunner.Plugins = _plugins;
        _plugins.Load(new IPopBarPlugin[] { new TranslatePlugin(), new AiPlugin(), new OcrPlugin(), new SendToPlugin() });

        _engine = new SelectionEngine(_store, _popup, Dispatcher, _plugins, _panels);
        _engine.Start();

        CreateTray();
        ListenForShowRequests();

        Log.Info($"PopBar started, config={ConfigStore.FilePath}");
        if (_store.IsFirstRun || e.Args.Contains("--settings")) ShowSettings();
        else Dispatcher.InvokeAsync(TrimWorkingSet, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static void TrimWorkingSet()
    {
        try { Native.NativeMethods.SetProcessWorkingSetSize(Native.NativeMethods.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); }
        catch { }
    }

    // ------------------------------------------------------------------ tray

    private void CreateTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "PopBar",
            Icon = LoadTrayIcon(),
            Visible = true,
        };
        RebuildTrayMenu();
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    private void RebuildTrayMenu()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(RebuildTrayMenu); return; }
        var menu = new WinForms.ContextMenuStrip();
        var enabledItem = new WinForms.ToolStripMenuItem("启用 PopBar") { Checked = _store.Current.Enabled, CheckOnClick = true };
        enabledItem.Click += (_, _) => _store.Update(c => c.Enabled = enabledItem.Checked);
        _enabledItem = enabledItem;
        menu.Items.Add(enabledItem);
        if (_pluginTrayItems.Count > 0)
        {
            menu.Items.Add(new WinForms.ToolStripSeparator());
            foreach (var (_, label, cb) in _pluginTrayItems)
                menu.Items.Add(new WinForms.ToolStripMenuItem(label, null, (_, _) => { try { cb(); } catch (Exception ex) { Log.Error("tray item", ex); } }));
        }
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("设置…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("退出", null, (_, _) => ExitApp()));
        _trayMenu?.Dispose();
        _trayMenu = menu;
        if (_tray != null) _tray.ContextMenuStrip = menu;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/popbar.ico"));
            if (res != null) return new System.Drawing.Icon(res.Stream);
        }
        catch (Exception ex) { Log.Warn("tray icon fallback: " + ex.Message); }
        return System.Drawing.SystemIcons.Application;
    }

    private void OnConfigChanged(AppConfig cfg)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_enabledItem != null) _enabledItem.Checked = cfg.Enabled;
            _settings?.RefreshFromStore();
            ApplyAutoStart(cfg.AutoStart);
            ThemeManager.BlurOpacity = cfg.BlurOpacity;
            ThemeManager.Apply(cfg.Theme);
        });
    }

    public void ShowSettings(string? pluginId = null)
    {
        try { ShowSettingsCore(pluginId); }
        catch (Exception ex) { Log.Error("open settings failed", ex); }
    }

    private void ShowSettingsCore(string? pluginId)
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(_store, _plugins!, _llm!);
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                _plugins?.RefreshActions();
                _plugins?.SyncConfig();
                // The settings UI is the only heavy part; give the memory back once it is gone.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                TrimWorkingSet();
            };
            _settings.Show();
        }
        else
        {
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            _settings.Show();
        }
        if (pluginId != null) _settings.ShowPluginsPage(pluginId);
        _settings.Activate();
    }

    private void ListenForShowRequests()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var t = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                string? cmd = null;
                try
                {
                    if (File.Exists(CommandFile)) { cmd = File.ReadAllText(CommandFile).Trim(); File.Delete(CommandFile); }
                }
                catch { }
                Dispatcher.InvokeAsync(() =>
                {
                    if (string.IsNullOrEmpty(cmd)) { ShowSettings(); return; }
                    var item = _pluginTrayItems.FirstOrDefault(i => string.Equals(i.Id, cmd, StringComparison.OrdinalIgnoreCase));
                    if (item.Callback != null) { Log.Info("--run " + cmd); item.Callback(); }
                    else Log.Warn("--run: unknown command " + cmd);
                });
            }
        }) { IsBackground = true, Name = "PopBar.ShowSignal" };
        t.Start();
    }

    /// <summary>Second instances drop a tray-item id here before signalling (PopBar.exe --run ocr:capture).</summary>
    private static string CommandFile => Path.Combine(ConfigStore.Dir, "command.txt");

    // ------------------------------------------------------------------ autostart

    private static void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            var exe = Environment.ProcessPath;
            if (enable && exe != null) key.SetValue("PopBar", $"\"{exe}\"");
            else if (!enable) key.DeleteValue("PopBar", throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Error("autostart", ex); }
    }

    // ------------------------------------------------------------------ shutdown

    private void ExitApp()
    {
        Log.Info("exiting");
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _engine?.Dispose();
        _plugins?.Dispose();
        _hotkeys?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
