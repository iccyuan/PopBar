using System.Text;
using Interop.UIAutomationClient;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

internal enum CaptureMethod { None, Native, Uia, JavaAccessBridge, IAccessible2, Clipboard }

/// <summary>Everything a capture strategy may need to decide and act.</summary>
internal sealed record CaptureContext(int X, int Y, IntPtr ForegroundWindow, string ForegroundProcess, bool AllowClipboard, IReadOnlyCollection<string> NoClipboardApps)
{
    /// <summary>The control with keyboard focus inside the foreground window (the window itself when unknown).</summary>
    public IntPtr FocusWindow { get; init; } = ForegroundWindow;
}

/// <summary>A read selection; <paramref name="Bounds"/> is the selected text's on-screen rectangle (physical pixels) when known.</summary>
internal sealed record CapturedText(string Text, RECT? Bounds = null);

/// <summary>
/// What a strategy found. <see cref="Settled"/>: it can read the focused control and its answer is final, so an empty
/// one means "nothing selected" and the chain stops there instead of falling through to Ctrl+C.
/// </summary>
internal readonly record struct CaptureAttempt(CapturedText? Text, bool Settled)
{
    /// <summary>This strategy cannot read the control; try the next one.</summary>
    public static CaptureAttempt Unsupported => default;
    /// <summary>The control was read and has no selection (or must not be read, e.g. a password box).</summary>
    public static CaptureAttempt NoSelection => new(null, true);
    public static CaptureAttempt Got(CapturedText text) => new(text, true);
    /// <summary>Text when there is some; otherwise settled or not as given.</summary>
    public static CaptureAttempt From(string? text, RECT? bounds, bool settled) =>
        string.IsNullOrEmpty(text) ? new(null, settled) : Got(new CapturedText(text, bounds));
}

/// <summary>One way of reading the current selection. Strategies run in order until one returns text or settles.</summary>
internal interface ITextCaptureStrategy
{
    CaptureMethod Method { get; }
    /// <summary>Cheap pre-check (e.g. clipboard fallback is disabled for terminals).</summary>
    bool CanRun(CaptureContext ctx);
    CaptureAttempt Capture(CaptureContext ctx);
}

/// <summary>
/// Reads the selection of the foreground app by running the strategy chain: the control's own messages (Scintilla,
/// Edit, RichEdit), UI Automation, Java Access Bridge, IAccessible2, and only when none of them can read the control,
/// Ctrl+C through the clipboard.
/// </summary>
internal sealed class TextCapture
{
    private readonly List<ITextCaptureStrategy> _strategies;

    public TextCapture() : this(new ITextCaptureStrategy[]
    {
        new NativeControlCaptureStrategy(), new UiaCaptureStrategy(), new JabCaptureStrategy(), new Ia2CaptureStrategy(), new ClipboardCaptureStrategy(),
    }) { }
    public TextCapture(IEnumerable<ITextCaptureStrategy> strategies) => _strategies = strategies.ToList();

    public CapturedText? Capture(int x, int y, bool allowClipboard, IReadOnlyCollection<string> noClipboardApps, out CaptureMethod method)
    {
        method = CaptureMethod.None;
        IntPtr fg = GetForegroundWindow();
        var ctx = new CaptureContext(x, y, fg, fg == IntPtr.Zero ? "" : GetWindowProcessName(fg), allowClipboard, noClipboardApps) { FocusWindow = GetFocusedControl(fg) };
        foreach (var s in _strategies)
        {
            if (!s.CanRun(ctx)) continue;
            CaptureAttempt got = default;
            try { got = s.Capture(ctx); }
            catch (Exception ex) { Log.Warn($"{s.Method} capture failed: {ex.Message}"); }
            if (!string.IsNullOrWhiteSpace(got.Text?.Text)) { method = s.Method; return got.Text; }
            if (got.Settled) { method = s.Method; return null; }
        }
        return null;
    }
}

/// <summary>Rectangle helpers shared by the strategies that know where the selected text is.</summary>
internal static class TextBounds
{
    /// <summary>
    /// How far (px) the mouse-up point may be above or below the reported text: a drag may end past the last line, but a
    /// provider reporting client instead of screen coordinates lands far off and is ignored.
    /// </summary>
    private const int MaxMouseDistance = 240;

    public static RECT? Union(RECT? a, RECT? b) => a is not { } x ? b : b is not { } y ? x : new RECT
    {
        Left = Math.Min(x.Left, y.Left), Top = Math.Min(x.Top, y.Top),
        Right = Math.Max(x.Right, y.Right), Bottom = Math.Max(x.Bottom, y.Bottom),
    };

    /// <summary>The bounds clipped to the source window, or null when they cannot be where the user just selected.</summary>
    public static RECT? Plausible(RECT? bounds, CaptureContext ctx)
    {
        if (bounds is not { } b) return null;
        if (ctx.ForegroundWindow != IntPtr.Zero && GetWindowRect(ctx.ForegroundWindow, out var w))
            b = new RECT { Left = Math.Max(b.Left, w.Left), Top = Math.Max(b.Top, w.Top), Right = Math.Min(b.Right, w.Right), Bottom = Math.Min(b.Bottom, w.Bottom) };
        if (b.Width <= 0 || b.Height <= 0) return null;
        if (ctx.Y < b.Top - MaxMouseDistance || ctx.Y > b.Bottom + MaxMouseDistance) return null;
        return b;
    }
}

/// <summary>
/// UI Automation TextPattern.GetSelection() - no side effects, works in Edge/Chrome/Word/Notepad/Terminal. The ranges'
/// bounding rectangles also say exactly where the selected text is, so the bar can sit clear of it.
/// </summary>
internal sealed class UiaCaptureStrategy : ITextCaptureStrategy
{
    /// <summary>
    /// UIA frameworks whose TextPattern reports the selection faithfully, so "no selection" there is final. Chromium
    /// counts only for web documents: an Edit inside it may be a stand-in (Monaco's hidden textarea) that lags the editor.
    /// </summary>
    private static readonly HashSet<string> TrustedFrameworks = new(StringComparer.OrdinalIgnoreCase) { "Win32", "WPF", "WinForm", "XAML", "DirectUI" };
    /// <summary>How many parents to climb looking for a TextPattern (a click lands on a text run, the pattern sits on its document).</summary>
    private const int MaxAncestors = 6;
    /// <summary>
    /// Chromium answers the first query after it switches its accessibility on with text but no geometry yet; one retry
    /// after this many ms gets the rectangles.
    /// </summary>
    private const int GeometryRetryMs = 60;

    private IUIAutomation? _uia;

    public CaptureMethod Method => CaptureMethod.Uia;
    public bool CanRun(CaptureContext ctx) => true;

    private IUIAutomation Uia
    {
        get
        {
            if (_uia == null)
            {
                _uia = new CUIAutomation8();
                try
                {
                    if (_uia is IUIAutomation2 u2)
                    {
                        u2.ConnectionTimeout = 600;   // ms; do not hang on frozen apps
                        u2.TransactionTimeout = 1500;
                    }
                }
                catch { /* older OS */ }
            }
            return _uia;
        }
    }

    public CaptureAttempt Capture(CaptureContext ctx)
    {
        try
        {
            var focused = SelectionOf(Uia.GetFocusedElement(), ctx);
            if (!string.IsNullOrEmpty(focused.Text?.Text)) return focused;
            var under = SelectionOf(Uia.ElementFromPoint(new tagPOINT { x = ctx.X, y = ctx.Y }), ctx);
            if (!string.IsNullOrEmpty(under.Text?.Text)) return under;
            return focused.Settled || under.Settled ? CaptureAttempt.NoSelection : CaptureAttempt.Unsupported;
        }
        catch
        {
            _uia = null; // recreate a broken COM proxy next time
            throw;
        }
    }

    private CaptureAttempt SelectionOf(IUIAutomationElement? el, CaptureContext ctx)
    {
        if (el == null) return CaptureAttempt.Unsupported;
        if (el.CurrentIsPassword != 0) return CaptureAttempt.NoSelection;
        var pat = TextPatternOf(ref el);
        if (pat == null || el == null) return CaptureAttempt.Unsupported;
        bool settled = IsTrusted(el);
        var ranges = pat.GetSelection();
        if (ranges == null) return new(null, settled);
        int n = ranges.Length;
        if (n == 0) return new(null, settled);
        var sb = new StringBuilder();
        var list = new List<IUIAutomationTextRange>(n);
        for (int i = 0; i < n; i++)
        {
            var range = ranges.GetElement(i);
            if (range == null) continue;
            list.Add(range);
            var s = range.GetText(-1);
            if (!string.IsNullOrEmpty(s)) sb.Append(s);
        }
        if (sb.Length == 0) return new(null, settled);
        var bounds = BoundsOf(list);
        if (bounds == null) { Thread.Sleep(GeometryRetryMs); bounds = BoundsOf(list); }
        return CaptureAttempt.Got(new CapturedText(sb.ToString(), TextBounds.Plausible(bounds, ctx)));
    }

    /// <summary>The TextPattern of <paramref name="el"/> or of its nearest ancestor that has one (which <paramref name="el"/> then becomes).</summary>
    private IUIAutomationTextPattern? TextPatternOf(ref IUIAutomationElement? el)
    {
        var walker = Uia.ControlViewWalker;
        for (int i = 0; el != null && i <= MaxAncestors; i++)
        {
            if (el.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is IUIAutomationTextPattern pat) return pat;
            el = walker.GetParentElement(el);
        }
        return null;
    }

    private static bool IsTrusted(IUIAutomationElement el)
    {
        string fw = el.CurrentFrameworkId ?? "";
        return TrustedFrameworks.Contains(fw)
            || (string.Equals(fw, "Chrome", StringComparison.OrdinalIgnoreCase) && el.CurrentControlType == UIA_ControlTypeIds.UIA_DocumentControlTypeId);
    }

    private static RECT? BoundsOf(List<IUIAutomationTextRange> ranges)
    {
        RECT? u = null;
        foreach (var r in ranges) u = TextBounds.Union(u, BoundsOf(r));
        return u;
    }

    /// <summary>The union of a range's line rectangles (left, top, width, height, ...); only the visible lines are reported.</summary>
    private static RECT? BoundsOf(IUIAutomationTextRange range)
    {
        double[]? r;
        try { r = range.GetBoundingRectangles() as double[]; }
        catch { return null; } // a provider without geometry: placement falls back to the mouse
        if (r == null) return null;
        RECT? u = null;
        for (int i = 0; i + 3 < r.Length; i += 4)
        {
            if (r[i + 2] <= 0 || r[i + 3] <= 0) continue;
            u = TextBounds.Union(u, new RECT
            {
                Left = (int)Math.Floor(r[i]), Top = (int)Math.Floor(r[i + 1]),
                Right = (int)Math.Ceiling(r[i] + r[i + 2]), Bottom = (int)Math.Ceiling(r[i + 1] + r[i + 3]),
            });
        }
        return u;
    }
}

/// <summary>The system caret of the foreground thread: where the selection ended and, by its height, how tall a line is.</summary>
internal static class CaretProbe
{
    /// <summary>Caret heights outside this range (px) are not a text line (hidden or placeholder carets).</summary>
    private const int MinLineHeight = 6, MaxLineHeight = 400;

    /// <summary>The caret height in pixels, or null when the window shows no system caret (many modern apps).</summary>
    public static int? LineHeight(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) || info.hwndCaret == IntPtr.Zero) return null;
        // Map both corners: the caret is in the caret window's client coordinates, which DPI virtualisation may scale.
        var a = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        var b = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
        if (!ClientToScreen(info.hwndCaret, ref a) || !ClientToScreen(info.hwndCaret, ref b)) return null;
        int h = b.Y - a.Y;
        return h is >= MinLineHeight and <= MaxLineHeight ? h : null;
    }
}

/// <summary>Ctrl+C with full clipboard backup/restore. Never used for terminals (Ctrl+C would interrupt).</summary>
internal sealed class ClipboardCaptureStrategy : ITextCaptureStrategy
{
    private static readonly HashSet<string> TerminalClasses = new(StringComparer.Ordinal)
    {
        "ConsoleWindowClass",            // conhost
        "CASCADIA_HOSTING_WINDOW_CLASS", // Windows Terminal
        "mintty",
        "PuTTY",
    };

    /// <summary>Processes where Ctrl+C interrupts or is sent on to another machine, always skipped (the settings list adds to these).</summary>
    public static readonly HashSet<string> BuiltinNoClipboardApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal.exe", "conhost.exe", "OpenConsole.exe", "mintty.exe", "putty.exe",
        "mstsc.exe", "vmware-vmx.exe", "VirtualBoxVM.exe", "alacritty.exe", "wezterm-gui.exe",
    };

    public CaptureMethod Method => CaptureMethod.Clipboard;

    public bool CanRun(CaptureContext ctx) => ctx.AllowClipboard && !IsTerminal(ctx.ForegroundWindow, ctx.ForegroundProcess, ctx.NoClipboardApps);

    public CaptureAttempt Capture(CaptureContext ctx) => CaptureAttempt.From(InputSender.CopySelection(), null, settled: false);

    public static bool IsTerminal(IntPtr hwnd, string proc, IReadOnlyCollection<string> noClipboardApps)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (TerminalClasses.Contains(GetWindowClass(hwnd))) return true;
        if (proc.Length == 0) return false;
        return BuiltinNoClipboardApps.Contains(proc) || noClipboardApps.Any(p => string.Equals(p, proc, StringComparison.OrdinalIgnoreCase));
    }
}
