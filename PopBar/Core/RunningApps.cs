using System.Diagnostics;
using System.Text.RegularExpressions;
using PopBar.Sdk;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>
/// Programs the user would recognise as "running": one entry per exe that owns an application window. That is a
/// visible taskbar-style window, or a hidden main window of a program sitting in the tray (WeChat, password
/// managers...). Shell infrastructure, UWP hosts and the countless hidden helper windows are left out.
/// </summary>
internal static class RunningApps
{
    /// <summary>Hosts and shell parts whose windows never mean "this program".</summary>
    private static readonly HashSet<string> SystemExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe",
        "TextInputHost.exe", "LockApp.exe", "Widgets.exe", "msedgewebview2.exe", "RuntimeBroker.exe", "dllhost.exe", "svchost.exe",
        "taskhostw.exe", "sihost.exe", "ctfmon.exe", "conhost.exe", "dwm.exe", "TabTip.exe", "SecurityHealthSystray.exe",
        "ScreenClippingHost.exe", "SystemSettings.exe", "WeChatAppEx.exe",
    };

    /// <summary>Hidden windows named like plumbing: IME, notification sinks, broadcast / listener / helper windows and the like.</summary>
    private static readonly Regex Plumbing = new(
        @"Hidden|Listener|Monitor|Notif|Helper|Service|Agent|Broadcast|GDI\+|\bIME\b|MSCTF|DDE|WISPTIS|TrayIcon|^IPC_|Power|Task Host|EndSession|Hook|Hotkey|OSD|Console|Wallpaper|Dispatcher|Marshal",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<RunningApp> List(string? excludeExe = "PopBar.exe")
    {
        var seen = new Dictionary<string, (string Path, string Title, bool Visible)>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((h, _) =>
        {
            bool visible = IsWindowVisible(h);
            if (visible ? !IsVisibleAppWindow(h) : !IsTrayMainWindow(h)) return true;
            var path = GetWindowProcessPath(h);
            var exe = System.IO.Path.GetFileName(path);
            if (exe.Length == 0 || SystemExes.Contains(exe) || (excludeExe != null && exe.Equals(excludeExe, StringComparison.OrdinalIgnoreCase))) return true;
            // A visible window beats a hidden one for the same program (its title is what the user sees).
            if (!seen.TryGetValue(exe, out var cur) || (visible && !cur.Visible)) seen[exe] = (path, GetWindowTitle(h), visible);
            return true;
        }, IntPtr.Zero);
        return seen.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(k => new RunningApp(k.Key, Label(k.Value.Path, k.Value.Title, k.Key)))
            .ToList();
    }

    /// <summary>A window that is on screen and would appear on the taskbar.</summary>
    public static bool IsVisibleAppWindow(IntPtr h) => IsWindowVisible(h) && IsAppWindow(h) && !IsCloaked(h);

    /// <summary>
    /// A program's main window while it is hidden in the tray: a real frame (caption plus resize border or minimise box),
    /// not one of the helper windows every process keeps around.
    /// </summary>
    public static bool IsTrayMainWindow(IntPtr h)
    {
        if (IsWindowVisible(h) || !IsAppWindow(h)) return false;
        long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
        if ((style & WS_CAPTION) != WS_CAPTION || (style & (WS_THICKFRAME | WS_MINIMIZEBOX)) == 0) return false;
        var title = GetWindowTitle(h);
        var cls = GetWindowClass(h);
        if (string.Equals(title, cls, StringComparison.OrdinalIgnoreCase)) return false; // named after its class: infrastructure
        if (Plumbing.IsMatch(title) || Plumbing.IsMatch(cls)) return false;
        if (Guid.TryParse(title, out _) || title.Contains('\\')) return false;            // GUIDs and paths are not app titles
        return true;
    }

    /// <summary>The program's own description ("微信", "Visual Studio Code") when the exe carries one, else the window title.</summary>
    private static string Label(string path, string title, string exe)
    {
        string desc = "";
        try { if (path.Length > 0) desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; } catch { }
        var stem = System.IO.Path.GetFileNameWithoutExtension(exe);
        if (desc.Length == 0 || desc.Equals(stem, StringComparison.OrdinalIgnoreCase)) desc = title;
        return desc.Length > 40 ? desc[..40] + "…" : desc;
    }
}
