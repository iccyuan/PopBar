using PopBar.Sdk;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>Processes that currently own a visible top-level window (one entry per exe).</summary>
internal static class RunningApps
{
    public static IReadOnlyList<RunningApp> List(string? excludeExe = "PopBar.exe")
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = GetWindowTitle(h);
            if (string.IsNullOrWhiteSpace(title)) return true;
            var exe = GetWindowProcessName(h);
            if (exe.Length == 0 || (excludeExe != null && exe.Equals(excludeExe, StringComparison.OrdinalIgnoreCase))) return true;
            if (!seen.ContainsKey(exe)) seen[exe] = title.Length > 40 ? title[..40] + "…" : title;
            return true;
        }, IntPtr.Zero);
        return seen.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => new RunningApp(k.Key, k.Value)).ToList();
    }
}
