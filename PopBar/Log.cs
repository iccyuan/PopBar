using System.IO;

namespace PopBar;

/// <summary>Tiny append-only file logger (%AppData%\PopBar\popbar.log). Never throws.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    /// <summary>The live log file. Tests point it at a temp file so they never write into the user's log.</summary>
    internal static string FilePath { get; set; } = System.IO.Path.Combine(Config.ConfigStore.Dir, "popbar.log");
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
                var path = FilePath;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    for (int i = Keep - 1; i >= 1; i--)
                        if (File.Exists($"{path}.{i}")) File.Move($"{path}.{i}", $"{path}.{i + 1}", overwrite: true);
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
