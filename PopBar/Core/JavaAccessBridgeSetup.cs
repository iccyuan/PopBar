using System.IO;

namespace PopBar.Core;

/// <summary>
/// Switches the Java Access Bridge on for the user's Java apps, as <c>jabswitch -enable</c> does: Java reads
/// ~/.accessibility.properties at start-up and loads the bridge when it lists it. PopBar's lines sit between markers,
/// so switching off removes exactly what it added and leaves any setting of the user's own (a screen reader's) alone.
/// </summary>
internal static class JavaAccessBridgeSetup
{
    private const string Begin = "# >>> PopBar: Java Access Bridge (text capture without the clipboard)";
    private const string End = "# <<< PopBar";
    private const string BridgeClass = "com.sun.java.accessibility.AccessBridge";

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".accessibility.properties");

    /// <summary>Make the file match <paramref name="enable"/>. Takes effect for Java apps started afterwards.</summary>
    public static void Apply(bool enable) => Apply(enable, FilePath);

    internal static void Apply(bool enable, string path)
    {
        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            var others = WithoutOurBlock(lines);
            List<string> next;
            if (!enable) next = others;
            else if (others.Any(DeclaresAssistiveTechnologies))
                next = others; // the user's own setting (it already names the bridge, or another tool owns the key): leave it
            else
                next = [.. others, Begin, $"assistive_technologies={BridgeClass}", "screen_magnifier_present=true", End];

            if (next.SequenceEqual(lines)) return;
            if (next.All(string.IsNullOrWhiteSpace)) { if (File.Exists(path)) File.Delete(path); }
            else File.WriteAllLines(path, next);
            Log.Info($"java access bridge {(enable ? "enabled" : "disabled")} in {path}");
        }
        catch (Exception ex) { Log.Warn("java access bridge setup: " + ex.Message); }
    }

    private static List<string> WithoutOurBlock(List<string> lines)
    {
        var kept = new List<string>();
        bool inside = false;
        foreach (var l in lines)
        {
            if (l.Trim() == Begin) { inside = true; continue; }
            if (inside) { if (l.Trim() == End) inside = false; continue; }
            kept.Add(l);
        }
        return kept;
    }

    private static bool DeclaresAssistiveTechnologies(string line)
    {
        var t = line.TrimStart();
        return !t.StartsWith('#') && !t.StartsWith('!') && t.StartsWith("assistive_technologies", StringComparison.Ordinal);
    }
}
