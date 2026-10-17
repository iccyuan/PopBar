using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using PopBar.Sdk;

namespace PopBar.Plugins;

/// <summary>
/// Whether a plugin DLL fits this PopBar, judged from the PopBar.Sdk version it was built against (read from its
/// metadata, nothing is loaded). Without the check a plugin built on a newer SDK fails with a bare loader error, or
/// later with a MissingMethodException the user never sees.
/// </summary>
internal static class PluginCompat
{
    /// <summary>The SDK API version this PopBar provides (PopBar.Sdk's assembly version).</summary>
    public static Version Host { get; } = typeof(IPopBarPlugin).Assembly.GetName().Version ?? new Version(1, 0);

    /// <summary>The PopBar.Sdk version <paramref name="dll"/> references; null when it is no plugin (no SDK reference, not .NET, unreadable).</summary>
    public static Version? SdkOf(string dll)
    {
        try
        {
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            foreach (var h in md.AssemblyReferences)
            {
                var r = md.GetAssemblyReference(h);
                if (md.GetString(r.Name) == "PopBar.Sdk") return r.Version;
            }
        }
        catch { } // not a PE file / corrupt: not a plugin
        return null;
    }

    /// <summary>
    /// Why a plugin built against SDK <paramref name="built"/> cannot run on SDK <paramref name="host"/>; null when it can.
    /// Same major and a minor no newer than ours. Major 0 is from before the SDK had its own version (it followed the
    /// app's, 0.x): those plugins use a subset of 1.0 and are let through.
    /// </summary>
    public static string? Problem(Version built, Version host)
    {
        if (built.Major == 0 && host.Major == 1) return null;
        string b = built.ToString(2), h = host.ToString(2);
        if (built.Major < host.Major) return $"插件基于旧版 SDK {b}，与当前 PopBar（SDK {h}）不兼容，需要插件作者更新";
        if (built.Major > host.Major || built.Minor > host.Minor) return $"插件需要更新版本的 PopBar（插件基于 SDK {b}，当前 PopBar 提供 SDK {h}）";
        return null;
    }

    /// <summary><see cref="Problem(Version, Version)"/> for a DLL against this PopBar; null for a fitting plugin or a non-plugin DLL.</summary>
    public static string? Problem(string dll) => SdkOf(dll) is { } v ? Problem(v, Host) : null;
}
