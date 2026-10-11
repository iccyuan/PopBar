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

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Tool window that never takes focus; with <paramref name="clickThrough"/> the mouse passes to what is below.</summary>
    public static void MakeOverlay(Window w, bool clickThrough)
    {
        var h = new WindowInteropHelper(w).Handle;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (clickThrough) ex |= WS_EX_TRANSPARENT;
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Live BGRA pixels of a physical screen rect (top-down, stride = width*4), optionally with the mouse cursor drawn in.</summary>
    public static byte[] CaptureBgra(Int32Rect r, bool cursor)
    {
        using var bmp = new Drawing.Bitmap(r.Width, r.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.X, r.Y, 0, 0, bmp.Size);
            if (cursor) DrawCursor(g, r);
        }
        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, r.Width, r.Height), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[r.Width * r.Height * 4];
            for (int y = 0; y < r.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * r.Width * 4, r.Width * 4);
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return px;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static void DrawCursor(Drawing.Graphics g, Int32Rect r)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        var hdc = g.GetHdc();
        try { DrawIconEx(hdc, ci.pt.X - r.X - hx, ci.pt.Y - r.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3 /* DI_NORMAL */); }
        finally { g.ReleaseHdc(hdc); }
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
