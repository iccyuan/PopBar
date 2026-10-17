using System.IO;
using System.Runtime.CompilerServices;

namespace PopBar.Tests;

/// <summary>Runs before any test: the app's logger writes to a temp file for this run, never to the user's popbar.log.</summary>
internal static class TestLog
{
    public static string FilePath { get; } = Path.Combine(Path.GetTempPath(), "popbar-tests", $"popbar-{Environment.ProcessId}.log");

    [ModuleInitializer]
    internal static void RedirectAppLog()
    {
        Log.FilePath = FilePath;
        // The folder only holds logs of earlier test runs: keep it from growing.
        try
        {
            foreach (var old in Directory.Exists(Path.GetDirectoryName(FilePath)) ? Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "popbar-*.log*") : [])
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old);
        }
        catch { /* best effort */ }
    }
}

public class TestLogTests
{
    [Fact]
    public void App_log_goes_to_the_temp_file()
    {
        var marker = "test-log-" + Guid.NewGuid().ToString("N");
        Log.Info(marker);
        Assert.Contains(marker, File.ReadAllText(TestLog.FilePath));
        Assert.DoesNotContain(Path.Combine("PopBar", "popbar.log"), Log.FilePath);
    }
}
