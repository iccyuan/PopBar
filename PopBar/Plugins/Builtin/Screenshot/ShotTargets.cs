using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Interop.UIAutomationClient;

namespace PopBar.Plugins.Builtin.Screenshot;

/// <summary>A top-level window the capture overlay can snap to, with the smaller parts inside it (physical pixels).</summary>
internal sealed class ShotTarget(IntPtr hwnd, Int32Rect rect)
{
    public IntPtr Hwnd { get; } = hwnd;
    public Int32Rect Rect { get; } = rect;
    /// <summary>Child controls and UI elements, any order; the overlay picks among those under the cursor. Set on the UI thread.</summary>
    public IReadOnlyList<Int32Rect> Parts { get; set; } = [];
}

/// <summary>
/// Collects the parts of each <see cref="ShotTarget"/>, off the UI thread while the overlay is already up (neither
/// source depends on what is drawn on top): visible child windows
/// (classic Win32 controls), plus UI Automation elements for the taskbar, whose buttons, tray icons and clock live in
/// one XAML surface with no child windows of their own.
/// </summary>
internal static class ShotTargets
{
    private const int MinSize = 8;
    /// <summary>Taskbars on the main and the other monitors.</summary>
    private static readonly string[] TaskbarClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    /// <summary>UIA is only walked for the taskbar; keep it bounded even so.</summary>
    private const int MaxUiaParts = 400;

    private delegate bool EnumChildProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder name, int max);

    [ThreadStatic] private static IUIAutomation? _uia; // COM object of the collecting thread

    /// <summary>Parts per target, in the order of <paramref name="targets"/>. Safe to call from any thread.</summary>
    public static List<Int32Rect>[] CollectParts(IReadOnlyList<ShotTarget> targets)
    {
        var parts = new List<Int32Rect>[targets.Count];
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            parts[i] = new List<Int32Rect>();
            AddChildWindows(t, parts[i]);
            if (IsTaskbar(t.Hwnd)) AddUiaElements(t, parts[i]);
        }
        return parts;
    }

    private static bool IsTaskbar(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return TaskbarClasses.Contains(sb.ToString());
    }

    private static void AddChildWindows(ShotTarget t, List<Int32Rect> parts)
    {
        EnumChildWindows(t.Hwnd, (h, _) =>
        {
            if (ShotNative.IsWindowVisible(h) && ShotNative.GetWindowRect(h, out var r)) Add(t, parts, r.L, r.T, r.R, r.B);
            return true;
        }, IntPtr.Zero);
    }

    private static void AddUiaElements(ShotTarget t, List<Int32Rect> parts)
    {
        try
        {
            _uia ??= CreateUia();
            var all = _uia.ElementFromHandle(t.Hwnd)?.FindAll(TreeScope.TreeScope_Descendants, _uia.CreateTrueCondition());
            if (all == null) return;
            for (int i = 0, n = Math.Min(all.Length, MaxUiaParts); i < n; i++)
            {
                var b = all.GetElement(i).CurrentBoundingRectangle;
                Add(t, parts, b.left, b.top, b.right, b.bottom);
            }
        }
        catch (Exception ex) { Log.Warn("taskbar elements unavailable: " + ex.Message); }
    }

    private static IUIAutomation CreateUia()
    {
        var uia = new CUIAutomation8();
        if (uia is IUIAutomation2 u2) { u2.ConnectionTimeout = 400; u2.TransactionTimeout = 800; } // the explorer may be busy
        return uia;
    }

    /// <summary>Clip to the window, drop slivers and the window's own rect (that is the target itself).</summary>
    private static void Add(ShotTarget t, List<Int32Rect> parts, int left, int top, int right, int bottom)
    {
        var w = t.Rect;
        int l = Math.Max(left, w.X), tp = Math.Max(top, w.Y), r = Math.Min(right, w.X + w.Width), b = Math.Min(bottom, w.Y + w.Height);
        if (r - l < MinSize || b - tp < MinSize) return;
        var part = new Int32Rect(l, tp, r - l, b - tp);
        if (part.Equals(w)) return;
        parts.Add(part);
    }
}
