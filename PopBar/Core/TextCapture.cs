using System.Text;
using Interop.UIAutomationClient;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

internal enum CaptureMethod { None, Uia, Clipboard }

/// <summary>Everything a capture strategy may need to decide and act.</summary>
internal sealed record CaptureContext(int X, int Y, IntPtr ForegroundWindow, string ForegroundProcess, bool AllowClipboard, IReadOnlyCollection<string> NoClipboardApps);

/// <summary>One way of reading the current selection. Strategies run in order until one returns text.</summary>
internal interface ITextCaptureStrategy
{
    CaptureMethod Method { get; }
    /// <summary>Cheap pre-check (e.g. clipboard fallback is disabled for terminals).</summary>
    bool CanRun(CaptureContext ctx);
    string? Capture(CaptureContext ctx);
}

/// <summary>Reads the selection of the foreground app by running the strategy chain (UIA first, Ctrl+C fallback second).</summary>
internal sealed class TextCapture
{
    private readonly List<ITextCaptureStrategy> _strategies;

    public TextCapture() : this(new ITextCaptureStrategy[] { new UiaCaptureStrategy(), new ClipboardCaptureStrategy() }) { }
    public TextCapture(IEnumerable<ITextCaptureStrategy> strategies) => _strategies = strategies.ToList();

    public string? Capture(int x, int y, bool allowClipboard, IReadOnlyCollection<string> noClipboardApps, out CaptureMethod method)
    {
        method = CaptureMethod.None;
        IntPtr fg = GetForegroundWindow();
        var ctx = new CaptureContext(x, y, fg, fg == IntPtr.Zero ? "" : GetWindowProcessName(fg), allowClipboard, noClipboardApps);
        foreach (var s in _strategies)
        {
            if (!s.CanRun(ctx)) continue;
            string? text = null;
            try { text = s.Capture(ctx); }
            catch (Exception ex) { Log.Warn($"{s.Method} capture failed: {ex.Message}"); }
            if (!string.IsNullOrWhiteSpace(text)) { method = s.Method; return text; }
        }
        return null;
    }
}

/// <summary>UI Automation TextPattern.GetSelection() - no side effects, works in Edge/Chrome/Word/Notepad/Terminal.</summary>
internal sealed class UiaCaptureStrategy : ITextCaptureStrategy
{
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

    public string? Capture(CaptureContext ctx)
    {
        try
        {
            var focused = Uia.GetFocusedElement();
            var t = SelectionOf(focused);
            if (!string.IsNullOrEmpty(t)) return t;
            var under = Uia.ElementFromPoint(new tagPOINT { x = ctx.X, y = ctx.Y });
            return SelectionOf(under);
        }
        catch
        {
            _uia = null; // recreate a broken COM proxy next time
            throw;
        }
    }

    private static string? SelectionOf(IUIAutomationElement? el)
    {
        if (el == null) return null;
        if (el.CurrentIsPassword != 0) return null;
        if (el.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is not IUIAutomationTextPattern pat) return null;
        var ranges = pat.GetSelection();
        if (ranges == null) return null;
        int n = ranges.Length;
        if (n == 0) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            var s = ranges.GetElement(i)?.GetText(-1);
            if (!string.IsNullOrEmpty(s)) sb.Append(s);
        }
        return sb.ToString();
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

    public CaptureMethod Method => CaptureMethod.Clipboard;

    public bool CanRun(CaptureContext ctx) => ctx.AllowClipboard && !IsTerminal(ctx.ForegroundWindow, ctx.ForegroundProcess, ctx.NoClipboardApps);

    public string? Capture(CaptureContext ctx) => InputSender.CopySelection();

    public static bool IsTerminal(IntPtr hwnd, string proc, IReadOnlyCollection<string> noClipboardApps)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (TerminalClasses.Contains(GetWindowClass(hwnd))) return true;
        return proc.Length > 0 && noClipboardApps.Any(p => string.Equals(p, proc, StringComparison.OrdinalIgnoreCase));
    }
}
