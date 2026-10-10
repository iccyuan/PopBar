using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace PopBar.Plugins.Builtin.Screenshot;

/// <summary>Win32 bits the screenshot plugin needs. Kept local so the plugin stays self-contained.</summary>
internal static class ShotNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    /// <summary>DIP→physical scale of the monitor containing a physical point.</summary>
    public static double ScaleAt(int x, int y)
    {
        var mon = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
        return GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1.0;
    }

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_SHOWWINDOW = 0x40, SWP_NOACTIVATE = 0x10, SWP_NOSIZE = 0x1;
    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;

    /// <summary>Virtual screen in physical pixels (all monitors).</summary>
    public static Int32Rect VirtualScreen() =>
        new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    public static void MakeToolWindow(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
    }

    /// <summary>Freeze the screen: physical-pixel snapshot of the whole virtual screen.</summary>
    public static BitmapSource CaptureScreen(Int32Rect vs)
    {
        using var bmp = new Drawing.Bitmap(vs.Width, vs.Height, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(vs.X, vs.Y, 0, 0, bmp.Size);
        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, vs.Width, vs.Height), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var src = BitmapSource.Create(vs.Width, vs.Height, 96, 96, PixelFormats.Pbgra32, null, data.Scan0, data.Stride * vs.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>
    /// Visible top-level window rects in z-order (topmost first), physical pixels, excluding our own process
    /// and cloaked (virtual-desktop / UWP-suspended) windows. Used for hover auto-detection.
    /// </summary>
    public static List<Int32Rect> WindowRects(Int32Rect vs)
    {
        var list = new List<Int32Rect>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == self) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(h, out r);
            int l = Math.Max(r.L, vs.X), t = Math.Max(r.T, vs.Y), rr = Math.Min(r.R, vs.X + vs.Width), b = Math.Min(r.B, vs.Y + vs.Height);
            if (rr - l < 8 || b - t < 8) return true;
            list.Add(new Int32Rect(l, t, rr - l, b - t));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
