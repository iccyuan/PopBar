using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using PopBar.Actions;
using PopBar.Config;
using PopBar.Core;
using PopBar.Plugins;
using PopBar.Plugins.Builtin;
using PopBar.Plugins.Builtin.Screenshot;
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
    /// <summary>Invisible, activatable window: a WPF ContextMenu opened from the tray only closes on outside clicks when our process is foreground.</summary>
    private Window? _trayAnchor;
    private readonly ConfigStore _store = new();
    private PopupWindow? _popup;
    private SelectionEngine? _engine;
    private SettingsWindow? _settings;
    private PluginManager? _plugins;
    private HotkeyManager? _hotkeys;
    private HttpClient? _http;
    private LlmService? _llm;
    private readonly PanelRegistry _panels = new();
    private readonly List<(string Id, string Label, string? Icon, Action Callback)> _pluginTrayItems = new();
    /// <summary>Delays a tray left click past the double-click window so a double click (settings) does not also fire the click action.</summary>
    private System.Windows.Threading.DispatcherTimer? _trayClickTimer;
    private System.Windows.Controls.ContextMenu? _trayMenu;
    private DateTime _trayMenuClosedAt;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Build-time helper: regenerate Assets/popbar.ico from the vector logo (tools/make-icon.ps1).
        int ei = Array.IndexOf(e.Args, "--export-icon");
        if (ei >= 0 && ei + 1 < e.Args.Length)
        {
            File.WriteAllBytes(e.Args[ei + 1], UI.LogoArt.BuildIco());
            Shutdown();
            return;
        }

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
        UI.Frost.Install();

        _popup = new PopupWindow();
        _popup.GroupOf = a => _plugins?.GroupOf(a);
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
            AddTrayItem = (id, label, icon, cb) => _pluginTrayItems.Add((id, label, icon, cb)),
            ShowPopup = ctx => _engine?.ShowExternal(ctx),
            OpenSettings = pluginId => Dispatcher.InvokeAsync(() => ShowSettings(pluginId)),
            RunAction = (id, ctx) => _plugins!.ExecuteAsync(id, ctx),
            ResyncActions = id => _plugins?.Resync(id),
        };
        _plugins = new PluginManager(_store, id => new PluginHost(id, services));
        SettingsWindow.PluginDrafts = services.SettingsHub;
        SettingsWindow.TrayItems = () => _pluginTrayItems.Select(i => (i.Id, i.Label, i.Icon)).ToList();
        ActionRunner.Plugins = _plugins;
        _plugins.Load(new IPopBarPlugin[] { new TranslatePlugin(), new AiPlugin(), new OcrPlugin(), new ScreenshotPlugin(), new SendToPlugin(), new QrPlugin(), new CodecPlugin(), new NotesPlugin(), new ScreenTranslatePlugin() });

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
        _tray.MouseUp += (_, e) => { if (e.Button == WinForms.MouseButtons.Right) Dispatcher.InvokeAsync(ShowTrayMenu); };
        // MouseClick (unlike MouseUp) is suppressed for the second click of a double click.
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) Dispatcher.InvokeAsync(OnTrayLeftClick); };
        _tray.DoubleClick += (_, _) => Dispatcher.InvokeAsync(() => { _trayClickTimer?.Stop(); ShowSettings(); });
    }

    /// <summary>
    /// Left click runs whatever the user picked (<see cref="AppConfig.TrayClick"/>). Opening settings happens at once since
    /// a double click lands on the same window anyway; any other action waits out the double-click time first.
    /// </summary>
    private void OnTrayLeftClick()
    {
        var cmd = _store.Current.TrayClick;
        if (string.IsNullOrWhiteSpace(cmd) || cmd == "settings") { ShowSettings(); return; }
        if (_trayClickTimer == null)
        {
            _trayClickTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WinForms.SystemInformation.DoubleClickTime) };
            _trayClickTimer.Tick += (_, _) =>
            {
                _trayClickTimer.Stop();
                if (!RunTrayCommand(_store.Current.TrayClick)) ShowSettings();
            };
        }
        _trayClickTimer.Stop();
        _trayClickTimer.Start();
    }

    /// <summary>"settings", "menu" or a plugin tray item id. False when nothing matches (e.g. the plugin was disabled).</summary>
    private bool RunTrayCommand(string cmd)
    {
        if (cmd == "settings") { ShowSettings(); return true; }
        if (cmd == "menu") { ShowTrayMenu(); return true; }
        var item = _pluginTrayItems.FirstOrDefault(i => string.Equals(i.Id, cmd, StringComparison.OrdinalIgnoreCase));
        if (item.Callback == null) { Log.Warn("tray command unknown: " + cmd); return false; }
        try { item.Callback(); } catch (Exception ex) { Log.Error("tray command " + cmd, ex); }
        return true;
    }

    /// <summary>The tray menu is PopBar's own styled ContextMenu (icons, rounded, theme-aware), rebuilt on every open.</summary>
    private void ShowTrayMenu()
    {
        // Clicking the icon while the menu is open closes it (outside click, on the mouse-down) a moment before this runs:
        // that click meant "close", so do not reopen. Same when the menu is somehow still open: toggle it off.
        if (_trayMenu is { IsOpen: true }) { _trayMenu.IsOpen = false; return; }
        if ((DateTime.UtcNow - _trayMenuClosedAt).TotalMilliseconds < 400) return;
        _trayAnchor ??= new Window
        {
            Width = 1, Height = 1, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, Opacity = 0,
        };
        var menu = BuildTrayMenu();
        UI.Frost.PrepareAtCursor(); // blur the monitor before the menu covers it
        _trayAnchor.Show();
        _trayAnchor.Activate();
        PlaceTrayMenu(menu, _trayAnchor);
        // IsOpen flips to false the instant the menu is dismissed; the Closed event only fires after the fade-out,
        // which is later than the tray's mouse-up, so the close time is taken from the property change.
        var isOpen = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.ContextMenu.IsOpenProperty, typeof(System.Windows.Controls.ContextMenu));
        EventHandler onIsOpen = (_, _) => { if (!menu.IsOpen) _trayMenuClosedAt = DateTime.UtcNow; };
        isOpen.AddValueChanged(menu, onIsOpen);
        menu.Closed += (_, _) => { isOpen.RemoveValueChanged(menu, onIsOpen); _trayAnchor.Hide(); if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; };
        _trayMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Hang the menu off the taskbar next to the icon, like the shell's own menus, rather than off the cursor (which
    /// moved it with every click position). The invisible anchor window is laid over the icon and the menu opens on
    /// its far side from the taskbar; without an icon rect it falls back to the cursor.
    /// </summary>
    private void PlaceTrayMenu(System.Windows.Controls.ContextMenu menu, Window anchor)
    {
        if (_tray == null || Native.TrayIconLocator.GetRect(_tray) is not { } icon)
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            return;
        }
        var hwnd = new System.Windows.Interop.WindowInteropHelper(anchor).Handle;
        Native.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, icon.Left, icon.Top, icon.Width, icon.Height,
            Native.NativeMethods.SWP_NOZORDER | Native.NativeMethods.SWP_NOACTIVATE);
        var (work, _) = Native.NativeMethods.GetMonitorAt(icon.Left + icon.Width / 2, icon.Top + icon.Height / 2);
        menu.PlacementTarget = anchor;
        // The menu template keeps MenuShadowRoom around the card for its shadow: pull that back so the card itself
        // lines up with the icon's edge and sits MenuGap away from the taskbar.
        (menu.Placement, menu.HorizontalOffset, menu.VerticalOffset) =
            icon.Bottom <= work.Top ? (System.Windows.Controls.Primitives.PlacementMode.Bottom, -MenuShadowRoom, MenuGap - MenuShadowRoom) : // taskbar on top
            icon.Right <= work.Left ? (System.Windows.Controls.Primitives.PlacementMode.Right, MenuGap - MenuShadowRoom, -MenuShadowRoom) :
            icon.Left >= work.Right ? (System.Windows.Controls.Primitives.PlacementMode.Left, MenuShadowRoom - MenuGap, -MenuShadowRoom) :
            (System.Windows.Controls.Primitives.PlacementMode.Top, -MenuShadowRoom, MenuShadowRoom - MenuGap);                              // bottom, and auto-hide
    }

    /// <summary>Margin of the ContextMenu template's root grid (Themes/Modern.xaml), room for the shadow.</summary>
    private const double MenuShadowRoom = 10;
    private const double MenuGap = 4;

    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");

        System.Windows.Controls.MenuItem Item(string label, string? icon, Action onClick, System.Windows.Media.Brush? brush = null)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = label };
            if (icon != null) mi.Icon = new UI.IconImage { Icon = icon, Size = 16, Brush = brush ?? muted };
            mi.Click += (_, _) => { try { onClick(); } catch (Exception ex) { Log.Error("tray item", ex); } };
            return mi;
        }

        bool enabled = _store.Current.Enabled;
        menu.Items.Add(Item(enabled ? "已启用 · 点击暂停" : "已暂停 · 点击启用", enabled ? "toggle-right" : "toggle-left",
            () => _store.Update(c => c.Enabled = !c.Enabled), enabled ? accent : muted));

        if (_pluginTrayItems.Count > 0)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
            foreach (var (_, label, icon, cb) in _pluginTrayItems) menu.Items.Add(Item(label, icon ?? "circle", cb));
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Item("设置…", "settings", () => ShowSettings()));
        menu.Items.Add(Item("退出", "power", ExitApp));
        return menu;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            using var ms = new MemoryStream(UI.LogoArt.BuildIco());
            return new System.Drawing.Icon(ms, WinForms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex) { Log.Warn("tray icon fallback: " + ex.Message); }
        return System.Drawing.SystemIcons.Application;
    }

    private void OnConfigChanged(AppConfig cfg)
    {
        Dispatcher.InvokeAsync(() =>
        {
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
                    Log.Info("--run " + cmd);
                    RunTrayCommand(cmd);
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
