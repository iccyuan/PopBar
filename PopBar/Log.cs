using System.IO;

namespace PopBar;

/// <summary>Tiny append-only file logger (%AppData%\PopBar\popbar.log). Never throws.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(Config.ConfigStore.Dir, "popbar.log");
    private const long MaxBytes = 2 * 1024 * 1024;
    /// <summary>Rotated files kept besides the live one (popbar.log.1 is the newest): ~10 MB of history in all.</summary>
    private const int Keep = 4;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR ", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Config.ConfigStore.Dir);
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    for (int i = Keep - 1; i >= 1; i--)
                        if (File.Exists($"{Path}.{i}")) File.Move($"{Path}.{i}", $"{Path}.{i + 1}", overwrite: true);
                    File.Move(Path, Path + ".1", overwrite: true);
                }
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
