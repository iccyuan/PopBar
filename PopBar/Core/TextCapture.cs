using System.Text;
using Interop.UIAutomationClient;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

internal enum CaptureMethod { None, Uia, Clipboard }

/// <summary>Reads the current text selection of the foreground app: UI Automation first, Ctrl+C fallback second.</summary>
internal sealed class TextCapture
{
    private IUIAutomation? _uia;

    private static readonly HashSet<string> TerminalClasses = new(StringComparer.Ordinal)
    {
        "ConsoleWindowClass",            // conhost
        "CASCADIA_HOSTING_WINDOW_CLASS", // Windows Terminal
        "mintty",
        "PuTTY",
    };

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

    public string? Capture(int x, int y, bool allowClipboard, IReadOnlyCollection<string> noClipboardApps, out CaptureMethod method)
    {
        method = CaptureMethod.None;
        var text = TryUia(x, y);
        if (!string.IsNullOrWhiteSpace(text)) { method = CaptureMethod.Uia; return text; }

        if (!allowClipboard) return null;
        IntPtr fg = GetForegroundWindow();
        if (IsTerminal(fg, noClipboardApps)) return null;

        text = InputSender.CopySelection();
        if (!string.IsNullOrWhiteSpace(text)) { method = CaptureMethod.Clipboard; return text; }
        return null;
    }

    public static bool IsTerminal(IntPtr hwnd, IReadOnlyCollection<string> noClipboardApps)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (TerminalClasses.Contains(GetWindowClass(hwnd))) return true;
        var proc = GetWindowProcessName(hwnd);
        return proc.Length > 0 && noClipboardApps.Any(p => string.Equals(p, proc, StringComparison.OrdinalIgnoreCase));
    }

    private string? TryUia(int x, int y)
    {
        try
        {
            var focused = Uia.GetFocusedElement();
            var t = SelectionOf(focused);
            if (!string.IsNullOrEmpty(t)) return t;

            var under = Uia.ElementFromPoint(new tagPOINT { x = x, y = y });
            return SelectionOf(under);
        }
        catch (Exception ex)
        {
            Log.Warn("UIA capture failed: " + ex.Message);
            // Reset so a broken COM proxy is recreated next time.
            _uia = null;
            return null;
        }
    }

    private static string? SelectionOf(IUIAutomationElement? el)
    {
        if (el == null) return null;
        try
        {
            if (el.CurrentIsPassword != 0) return null;
            if (el.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is not IUIAutomationTextPattern pat) return null;
            var ranges = pat.GetSelection();
            if (ranges == null) return null;
            int n = ranges.Length;
            if (n == 0) return null;
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                var r = ranges.GetElement(i);
                var s = r?.GetText(-1);
                if (!string.IsNullOrEmpty(s)) sb.Append(s);
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log.Warn("UIA selection failed: " + ex.Message);
            return null;
        }
    }
}
