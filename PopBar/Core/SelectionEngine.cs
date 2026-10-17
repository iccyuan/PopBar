using System.Collections.Concurrent;
using System.Windows.Threading;
using PopBar.Actions;
using PopBar.Config;
using PopBar.Plugins;
using PopBar.Sdk;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>
/// Decides when a selection happened (drag / double click / shift click), captures the text on a worker
/// thread and asks the popup to show. Also hides the popup on outside clicks, wheel and key presses.
/// </summary>
internal sealed class SelectionEngine : IDisposable
{
    // Physical pixels. 5 was ~3 DIP at 150 %: a hand tremor during a double-click counted as a drag-select.
    private const int DragThresholdPx = 10;
    private const int DoubleClickSlopPx = 4;

    private readonly ConfigStore _store;
    private readonly IPopupPresenter _popup;
    private readonly Dispatcher _ui;
    private readonly PluginManager _plugins;
    private readonly IPanelRegistry _panels;
    private readonly ActionRunner _runner;
    private readonly InputHook _hook = new();
    private readonly TextCapture _capture = new();
    private readonly EditableProbe _editable = new();
    /// <summary>Capture jobs only: the UIA client lives on this STA thread. Actions do not run here.</summary>
    private readonly BlockingCollection<Action> _jobs = new();
    private readonly Thread _worker;

    // --- hook-thread state ---
    private int _downX, _downY;
    private long _downTicks;
    private bool _downInPopup;
    /// <summary>
    /// This press is part of a multi-click (double, triple...). Judged like Windows does for the apps themselves:
    /// press-to-press within the double-click time and slop. Judging on release-to-release drifted from the app's
    /// view for slow clicks, so we would hide a bar the app had just extended to a line.
    /// </summary>
    private bool _multiDown;
    /// <summary>This press continues a click chain whose previous release started a capture: keep the bar, update in place.</summary>
    private bool _chainDown;
    /// <summary>The last left-button release started a capture (its bar may still be on its way to the screen).</summary>
    private bool _lastUpTriggered;
    private int _seq;                 // bumped on any event that invalidates a pending capture

    // --- popup state (written on UI thread, read on hook thread) ---
    private volatile bool _popupVisible;
    private RECT _popupRect;

    public SelectionEngine(ConfigStore store, IPopupPresenter popup, Dispatcher ui, PluginManager plugins, IPanelRegistry panels)
    {
        _store = store;
        _popup = popup;
        _ui = ui;
        _plugins = plugins;
        _panels = panels;
        _runner = new ActionRunner(plugins);
        _hook.Mouse += OnMouse;
        _hook.KeyDown += OnKey;
        // Actions run off the capture thread (see ActionRunner), so the next selection is never queued behind one.
        _popup.ActionClicked += (a, sel) => _runner.Start(a, sel);
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
                Volatile.Write(ref _pending, null);
                _multiDown = now - _downTicks <= GetDoubleClickTime() && Dist(_downX, _downY, x, y) <= DoubleClickSlopPx;
                // Chain is judged by the release that triggered, not by visibility: a fast third click often lands
                // before the bar from the double click is on screen, and must still count as the same gesture.
                _chainDown = _multiDown && _lastUpTriggered;
                if (_popupVisible && !_chainDown) RequestHide();
                NotifyOutsideClick(x, y);
                _downX = x; _downY = y; _downTicks = now;
                Interlocked.Increment(ref _seq);
                break;

            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
                if (_popupVisible && !_popupRect.Contains(x, y)) RequestHide();
                NotifyOutsideClick(x, y);
                Interlocked.Increment(ref _seq);
                break;

            case WM_LBUTTONUP:
            {
                if (_downInPopup) { _downInPopup = false; return; }
                var cfg = _store.Current;
                bool recentDown = now - _downTicks < 10_000;
                bool drag = recentDown && Dist(_downX, _downY, x, y) >= DragThresholdPx;
                bool dbl = _multiDown && !drag;
                bool shift = !drag && IsKeyDown(VK_SHIFT);
                bool chain = _chainDown;
                _chainDown = false;
                _lastUpTriggered = false;
                bool trigger = cfg.Enabled && !_panels.SuppressCapture
                    && ((drag && cfg.TriggerOnDrag) || (dbl && cfg.TriggerOnDoubleClick) || (shift && cfg.TriggerOnShiftClick))
                    && !_panels.ContainsPoint(x, y); // selecting inside one of our own panels
                if (!trigger)
                {
                    if (chain && _popupVisible) RequestHide(); // the kept bar has nothing to update to
                    return;
                }

                _lastUpTriggered = true;
                int seq = Volatile.Read(ref _seq);
                int? startY = drag ? _downY : null;
                Enqueue(() => CaptureJob(seq, x, y, startY, dbl, now, cfg, chain));
                break;
            }

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                if (_popupVisible) RequestHide();
                break;
        }
    }

    // --- "press a key to show" mode: the last captured selection waits here for the reveal key ---
    // Written by the capture thread, taken by the hook thread: one immutable record swapped atomically, so the hook can
    // never pair one selection's text with another's actions.
    private sealed record PendingShow(Selection Selection, List<ActionDefinition> Actions, long Ticks);
    private PendingShow? _pending;
    private const int PendingWindowMs = 4000;

    /// <summary>Virtual key for a configured reveal key: "ctrl"/"alt"/"shift"/"win" or any WPF Key name (e.g. "F8", "CapsLock").</summary>
    public static int RevealKeyVk(string? key)
    {
        switch ((key ?? "ctrl").ToLowerInvariant())
        {
            case "ctrl": return VK_CONTROL;
            case "alt": return VK_MENU;
            case "shift": return VK_SHIFT;
            case "win": return VK_LWIN;
        }
        return Enum.TryParse<System.Windows.Input.Key>(key, true, out var k) ? System.Windows.Input.KeyInterop.VirtualKeyFromKey(k) : VK_CONTROL;
    }

    private static bool IsRevealKey(uint vk, int configured) => configured switch
    {
        VK_CONTROL => vk is VK_CONTROL or 0xA2 or 0xA3,   // L/R Ctrl
        VK_MENU => vk is VK_MENU or 0xA4 or 0xA5,         // L/R Alt
        VK_SHIFT => vk is VK_SHIFT or 0xA0 or 0xA1,       // L/R Shift
        VK_LWIN => vk is VK_LWIN or VK_RWIN,
        _ => vk == (uint)configured,
    };

    private static bool IsModifier(uint vk) =>
        vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN or (>= 0xA0 and <= 0xA5);

    private void OnKey(uint vk)
    {
        var cfg = _store.Current;
        if (cfg.ShowMode == "key" && Volatile.Read(ref _pending) != null && IsRevealKey(vk, RevealKeyVk(cfg.ShowKey))
            && Interlocked.Exchange(ref _pending, null) is { } p)
        {
            if (Environment.TickCount64 - p.Ticks <= PendingWindowMs)
            {
                _ui.InvokeAsync(() =>
                {
                    try { _popup.ShowFor(p.Selection, p.Actions, cfg); }
                    catch (Exception ex) { Log.Error("popup show failed", ex); }
                }, DispatcherPriority.Send);
                return; // the reveal key must not hide what it just revealed
            }
        }
        // Bare modifiers never dismiss: holding Ctrl auto-repeats key-downs, which would hide the bar
        // right after a Ctrl+double-click shows it.
        if (IsModifier(vk)) return;
        if (_popupVisible) RequestHide();
        Interlocked.Increment(ref _seq);
    }

    private static int Dist(int x1, int y1, int x2, int y2) => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    private void RequestHide()
    {
        _popupVisible = false;
        _ui.InvokeAsync(_popup.HideNow, DispatcherPriority.Send);
    }

    private void NotifyOutsideClick(int x, int y) => _ui.InvokeAsync(() => _panels.OnOutsideClick(x, y));

    // ------------------------------------------------------------------ worker

    private void CaptureJob(int seq, int x, int y, int? startY, bool dbl, long upTicks, AppConfig cfg, bool update = false)
    {
        if (CaptureAndShow(seq, x, y, startY, dbl, upTicks, cfg, update) || !update) return;
        // A chain click that selected nothing (many apps restart the count after a triple click, collapsing the
        // selection) must not leave the bar up with stale text. But hiding at once makes the bar flicker when the
        // next click of the chain selects again, so wait out the double-click window first; any further click
        // bumps _seq and either updates the bar or hides it itself.
        _ = Task.Run(async () =>
        {
            await Task.Delay((int)GetDoubleClickTime());
            if (seq == Volatile.Read(ref _seq) && _popupVisible) RequestHide();
        });
    }

    /// <summary>False when nothing was shown (no text, no actions, superseded...).</summary>
    private bool CaptureAndShow(int seq, int x, int y, int? startY, bool dbl, long upTicks, AppConfig cfg, bool update)
    {
        long wait = upTicks + cfg.ShowDelayMs - Environment.TickCount64;
        if (wait > 0) Thread.Sleep((int)wait);
        if (seq != Volatile.Read(ref _seq)) return false;

        IntPtr source = GetForegroundWindow();
        if (source == IntPtr.Zero) return false;
        // Never treat our own windows (settings, panels, plugin overlays) as a selection source.
        GetWindowThreadProcessId(source, out uint srcPid);
        if (srcPid == (uint)Environment.ProcessId) return false;
        if (cfg.ExcludedApps.Count > 0)
        {
            var proc = GetWindowProcessName(source);
            if (cfg.ExcludedApps.Any(p => string.Equals(p.Trim(), proc, StringComparison.OrdinalIgnoreCase))) return false;
        }

        bool allowClipboard = !cfg.ClipboardFallbackOff && (cfg.ClipboardFallbackAlways || dbl || IsIBeamCursor());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var captured = _capture.Capture(x, y, allowClipboard, cfg.NoClipboardApps, out var method);
        if (seq != Volatile.Read(ref _seq)) return false;
        var text = captured?.Text;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > cfg.MaxTextLength) return false;

        var sel = new Selection(text, source, x, y)
        {
            Bounds = captured!.Bounds,
            StartY = startY,
            LineHeight = captured.Bounds == null ? CaretProbe.LineHeight(source) : null,
        };
        var actions = ResolveActions(sel, cfg);
        if (actions.Count == 0) return false;
        Log.Info($"captured {text.Length} chars via {method} from {GetWindowProcessName(source)} in {sw.ElapsedMilliseconds}ms"
            + (sel.Bounds is { } b ? $", text at {b.Left},{b.Top} {b.Width}x{b.Height}" : sel.LineHeight is { } lh ? $", line {lh}px" : ""));

        if (cfg.ShowMode == "key" && !IsKeyDown(RevealKeyVk(cfg.ShowKey)))
        {
            // Wait for the reveal key (see OnKey) instead of popping up right away.
            Volatile.Write(ref _pending, new PendingShow(sel, actions, Environment.TickCount64));
            if (update && _popupVisible) RequestHide(); // the revealed bar shows the old text; wait for the key again
            return true;
        }

        _ui.InvokeAsync(() =>
        {
            if (seq != Volatile.Read(ref _seq)) return;
            // A triple click right after a double click refreshes the visible bar instead of hiding and re-showing it.
            try { if (update) _popup.UpdateFor(sel, actions, cfg); else _popup.ShowFor(sel, actions, cfg); }
            catch (Exception ex) { Log.Error("popup show failed", ex); } // InvokeAsync would swallow it otherwise
        }, DispatcherPriority.Send);
        return true;
    }

    /// <summary>Config actions that apply to this selection (built-in matchers + plugin predicates).</summary>
    private List<ActionDefinition> ResolveActions(Selection sel, AppConfig cfg)
    {
        SelectionContext? ctx = null;
        bool? canPaste = null;
        var result = new List<ActionDefinition>();
        foreach (var a in cfg.Actions)
        {
            if (!a.Enabled) continue;
            // Paste only makes sense with text on the clipboard and an editable field under the selection.
            if (a.Type == ActionTypes.Paste
                && !(canPaste ??= IsClipboardFormatAvailable(CF_UNICODETEXT) && _editable.IsEditable(sel.SourceWindow)))
                continue;
            if (a.IsPlugin)
            {
                ctx ??= sel.ToContext();
                if (_plugins.IsApplicable(a, ctx, cfg)) result.Add(a);
            }
            else if (Matchers.Matches(a, sel.Text)) result.Add(a);
        }
        return result;
    }

    /// <summary>Show the bar for text that did not come from a mouse selection (e.g. OCR). Any thread.</summary>
    public void ShowExternal(SelectionContext ctx)
    {
        var cfg = _store.Current;
        var sel = Selection.FromContext(ctx);
        var actions = ResolveActions(sel, cfg);
        if (actions.Count == 0) return;
        Interlocked.Increment(ref _seq);
        _ui.InvokeAsync(() => _popup.ShowFor(sel, actions, cfg), DispatcherPriority.Send);
    }

    public void Dispose()
    {
        _hook.Dispose();
        _jobs.CompleteAdding();
    }
}
