using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PopBar.Core;

internal enum BackdropKind
{
    /// <summary>DWMSBT_TRANSIENTWINDOW: blurred acrylic, for popups / flyouts.</summary>
    Acrylic,
    /// <summary>DWMSBT_MAINWINDOW: Mica, for top-level windows (fades to solid when inactive).</summary>
    Mica,
}

/// <summary>
/// Gaussian-blur window backdrops. Windows 11 22H2+ uses the documented DWM system backdrop API;
/// Windows 10 1803+ falls back to the (undocumented but ubiquitous) SetWindowCompositionAttribute acrylic.
/// The window must have a transparent client area (WindowChrome GlassFrameThickness=-1 + Background=Transparent).
/// </summary>
internal static class Acrylic
{
    private static readonly int Build = Environment.OSVersion.Version.Build;

    /// <summary>DWM system backdrops (Mica / Acrylic) are available.</summary>
    public static bool SystemBackdropSupported => Build >= 22621;
    /// <summary>Some blur is available (DWM backdrop, or the Win10 composition-attribute acrylic).</summary>
    public static bool AnyBlurSupported => Build >= 17134;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;
    private const int DWMSBT_MAINWINDOW = 2, DWMSBT_TRANSIENTWINDOW = 3;

    // ---- Windows 10 fallback ----
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    /// <summary>Apply a blurred backdrop. <paramref name="tintArgb"/> is only used by the Win10 fallback (0xAABBGGRR).</summary>
    public static bool Apply(Window window, BackdropKind kind, bool dark, bool roundCorners = true, uint tintArgb = 0x40000000)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            int dm = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dm, sizeof(int));

            if (SystemBackdropSupported)
            {
                var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                int corner = roundCorners ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
                int type = kind == BackdropKind.Mica ? DWMSBT_MAINWINDOW : DWMSBT_TRANSIENTWINDOW;
                return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
            }

            if (Build >= 17134)
            {
                var policy = new AccentPolicy { AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND, AccentFlags = 2, GradientColor = tintArgb };
                int size = Marshal.SizeOf(policy);
                IntPtr p = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(policy, p, false);
                    var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = p, SizeOfData = size };
                    return SetWindowCompositionAttribute(hwnd, ref data) != 0;
                }
                finally { Marshal.FreeHGlobal(p); }
            }
        }
        catch (Exception ex) { Log.Warn("backdrop failed: " + ex.Message); }
        return false;
    }

    /// <summary>Re-apply only the dark/light hint (cheap; used on theme change).</summary>
    public static void SetDark(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dm = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dm, sizeof(int)); } catch { }
    }
}
