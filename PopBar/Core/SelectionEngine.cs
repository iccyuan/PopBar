using System.Collections.Concurrent;
using System.Windows.Threading;
using PopBar.Actions;
using PopBar.Config;
using PopBar.UI;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>
/// Decides when a selection happened (drag / double click / shift click), captures the text on a worker
/// thread and asks the popup to show. Also hides the popup on outside clicks, wheel and key presses.
/// </summary>
internal sealed class SelectionEngine : IDisposable
{
    private const int DragThresholdPx = 5;
    private const int DoubleClickSlopPx = 4;

    private readonly ConfigStore _store;
    private readonly PopupWindow _popup;
    private readonly Dispatcher _ui;
    private readonly InputHook _hook = new();
    private readonly TextCapture _capture = new();
    private readonly BlockingCollection<Action> _jobs = new();
    private readonly Thread _worker;

    // --- hook-thread state ---
    private int _downX, _downY;
    private long _downTicks;
    private bool _downInPopup;
    private int _lastUpX, _lastUpY;
    private long _lastUpTicks;
    private int _seq;                 // bumped on any event that invalidates a pending capture

    // --- popup state (written on UI thread, read on hook thread) ---
    private volatile bool _popupVisible;
    private RECT _popupRect;

    public SelectionEngine(ConfigStore store, PopupWindow popup, Dispatcher ui)
    {
        _store = store;
        _popup = popup;
        _ui = ui;
        _hook.Mouse += OnMouse;
        _hook.KeyDown += OnKey;
        _popup.ActionClicked += (a, text, src) => Enqueue(() => ActionRunner.Run(a, text, src));
        _popup.Shown += r => { _popupRect = r; _popupVisible = true; };
        _popup.Hidden += () => _popupVisible = false;

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "PopBar.Worker" };
        _worker.SetApartmentState(ApartmentState.STA); // UIA COM client lives here
    }

    public void Start()
    {
        _worker.Start();
        _hook.Start();
        Log.Info("engine started");
    }

    private void Enqueue(Action job) => _jobs.Add(job);

    private void WorkerLoop()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try { job(); }
            catch (Exception ex) { Log.Error("worker job failed", ex); }
        }
    }

    // ------------------------------------------------------------------ hook callbacks

    private void OnMouse(int msg, int x, int y)
    {
        long now = Environment.TickCount64;
        switch (msg)
        {
            case WM_LBUTTONDOWN:
                if (_popupVisible && _popupRect.Contains(x, y)) { _downInPopup = true; return; }
                _downInPopup = false;
                if (_popupVisible) RequestHide();
                _downX = x; _downY = y; _downTicks = now;
                Interlocked.Increment(ref _seq);
                break;

            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
                if (_popupVisible && !_popupRect.Contains(x, y)) RequestHide();
                Interlocked.Increment(ref _seq);
                break;

            case WM_LBUTTONUP:
            {
                if (_downInPopup) { _downInPopup = false; return; }
                var cfg = _store.Current;
                bool recentDown = now - _downTicks < 10_000;
                bool drag = recentDown && Dist(_downX, _downY, x, y) >= DragThresholdPx;
                bool dbl = now - _lastUpTicks <= GetDoubleClickTime() && Dist(_lastUpX, _lastUpY, x, y) <= DoubleClickSlopPx;
                bool shift = !drag && IsKeyDown(VK_SHIFT);
                _lastUpX = x; _lastUpY = y; _lastUpTicks = now;

                if (!cfg.Enabled) return;
                bool trigger = (drag && cfg.TriggerOnDrag) || (dbl && cfg.TriggerOnDoubleClick) || (shift && cfg.TriggerOnShiftClick);
                if (!trigger) return;

                int seq = Volatile.Read(ref _seq);
                Enqueue(() => CaptureJob(seq, x, y, dbl, now, cfg));
                break;
            }

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                if (_popupVisible) RequestHide();
                break;
        }
    }

    private void OnKey(uint vk)
    {
        if (_popupVisible) RequestHide();
        Interlocked.Increment(ref _seq);
    }

    private static int Dist(int x1, int y1, int x2, int y2) => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    private void RequestHide()
    {
        _popupVisible = false;
        _ui.InvokeAsync(_popup.HideNow, DispatcherPriority.Send);
    }

    // ------------------------------------------------------------------ worker

    private void CaptureJob(int seq, int x, int y, bool dbl, long upTicks, AppConfig cfg)
    {
        long wait = upTicks + cfg.ShowDelayMs - Environment.TickCount64;
        if (wait > 0) Thread.Sleep((int)wait);
        if (seq != Volatile.Read(ref _seq)) return;

        IntPtr source = GetForegroundWindow();
        if (source == IntPtr.Zero) return;
        if (cfg.ExcludedApps.Count > 0)
        {
            var proc = GetWindowProcessName(source);
            if (cfg.ExcludedApps.Any(p => string.Equals(p.Trim(), proc, StringComparison.OrdinalIgnoreCase))) return;
        }

        bool allowClipboard = !cfg.ClipboardFallbackOff && (cfg.ClipboardFallbackAlways || dbl || IsIBeamCursor());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var text = _capture.Capture(x, y, allowClipboard, cfg.NoClipboardApps, out var method);
        if (seq != Volatile.Read(ref _seq)) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > cfg.MaxTextLength) return;

        var actions = cfg.Actions.Where(a => a.Enabled && Matchers.Matches(a, text)).ToList();
        if (actions.Count == 0) return;
        Log.Info($"captured {text.Length} chars via {method} in {sw.ElapsedMilliseconds}ms");

        _ui.InvokeAsync(() =>
        {
            if (seq != Volatile.Read(ref _seq)) return;
            _popup.ShowFor(text, x, y, source, actions, cfg);
        }, DispatcherPriority.Send);
    }

    public void Dispose()
    {
        _hook.Dispose();
        _jobs.CompleteAdding();
    }
}
