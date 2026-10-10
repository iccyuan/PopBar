using System.IO;
using System.Windows;
using Microsoft.Win32;
using PopBar.Config;
using PopBar.Core;
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
    private WinForms.ToolStripMenuItem? _enabledItem;
    private readonly ConfigStore _store = new();
    private PopupWindow? _popup;
    private SelectionEngine? _engine;
    private SettingsWindow? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance is running: ask it to open settings, then leave.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("unhandled", ex.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ex) => { Log.Error("dispatcher", ex.Exception); ex.Handled = true; };

        _store.Load();
        _store.Changed += OnConfigChanged;
        ApplyAutoStart(_store.Current.AutoStart);

        _popup = new PopupWindow();
        _popup.Preload();

        _engine = new SelectionEngine(_store, _popup, Dispatcher);
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
        var menu = new WinForms.ContextMenuStrip();
        var enabledItem = new WinForms.ToolStripMenuItem("启用 PopBar") { Checked = _store.Current.Enabled, CheckOnClick = true };
        enabledItem.Click += (_, _) => _store.Update(c => c.Enabled = enabledItem.Checked);
        _enabledItem = enabledItem;
        menu.Items.Add(enabledItem);
        menu.Items.Add(new WinForms.ToolStripMenuItem("设置…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _tray = new WinForms.NotifyIcon
        {
            Text = "PopBar",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();
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
        });
    }

    public void ShowSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(_store);
            _settings.Closed += (_, _) =>
            {
                _settings = null;
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
        _settings.Activate();
    }

    private void ListenForShowRequests()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var t = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.InvokeAsync(ShowSettings);
        }) { IsBackground = true, Name = "PopBar.ShowSignal" };
        t.Start();
    }

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
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
