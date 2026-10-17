using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PopBar.Sdk.Ui;

/// <summary>
/// Helpers for a plugin's own floating windows (notes, overlays, pinned images) placed in physical screen pixels,
/// which is what selections, captures and <see cref="SelectionContext"/> coordinates use.
/// </summary>
public static class ScreenWindows
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    /// <summary>DPI scale (1.0 = 96 DPI) of the monitor at a physical screen point: divide pixels by it to get DIPs.</summary>
    public static double ScaleAt(int x, int y)
    {
        var mon = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
        return GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1.0;
    }

    /// <summary>Keep the window out of Alt+Tab and the taskbar. Call once its handle exists (e.g. in SourceInitialized).</summary>
    public static void HideFromAltTab(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
    }

    /// <summary>The window's bounds in physical screen pixels; false before it has a handle.</summary>
    public static bool TryGetBounds(Window w, out Int32Rect bounds)
    {
        bounds = default;
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return false;
        bounds = new Int32Rect(r.L, r.T, r.R - r.L, r.B - r.T);
        return true;
    }

    /// <summary>Move the window's top-left corner to a physical screen point and keep it above others, without activating it.</summary>
    public static void PinTopmostAt(Window w, int x, int y)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, x, y, 0, 0, 0x1 /* NOSIZE */ | 0x10 /* NOACTIVATE */);
    }
}
