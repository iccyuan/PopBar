using System.Reflection;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;
using static PopBar.Native.NativeMethods;

namespace PopBar.Native;

/// <summary>
/// Where a tray icon is on screen, so its menu can hang off the taskbar next to it instead of off the cursor.
/// <c>Shell_NotifyIconGetRect</c> needs the icon's owner window and id, which WinForms' <see cref="WinForms.NotifyIcon"/>
/// keeps private (<c>_window</c>, <c>_id</c> in .NET 8-10); they are read by reflection, and a renamed field just
/// means no rect (the caller falls back to the cursor).
/// </summary>
internal static class TrayIconLocator
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo? WindowField = typeof(WinForms.NotifyIcon).GetField("_window", Private);
    private static readonly FieldInfo? IdField = typeof(WinForms.NotifyIcon).GetField("_id", Private);

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    /// <summary>The icon's rect in physical pixels; null when it cannot be told (hidden in the overflow, API change).</summary>
    public static RECT? GetRect(WinForms.NotifyIcon icon)
    {
        try
        {
            if (WindowField?.GetValue(icon) is not WinForms.NativeWindow window || IdField?.GetValue(icon) is not uint id) return null;
            var nid = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = window.Handle, uID = id };
            return Shell_NotifyIconGetRect(ref nid, out var rect) == 0 && rect.Width > 0 ? rect : null;
        }
        catch (Exception ex)
        {
            Log.Warn("tray icon rect unavailable: " + ex.Message);
            return null;
        }
    }
}
