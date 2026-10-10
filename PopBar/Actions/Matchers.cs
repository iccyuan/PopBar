using System.Text.RegularExpressions;

namespace PopBar.Actions;

public static partial class Matchers
{
    [GeneratedRegex(@"^(?:https?://|ftp://|www\.)[^\s/$.?#].[^\s]*$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    private static readonly Dictionary<string, Regex?> CustomCache = new();

    public static bool IsUrl(string text) => UrlRegex().IsMatch(text.Trim());
    public static bool IsEmail(string text) => EmailRegex().IsMatch(text.Trim());

    public static string NormalizeUrl(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;
        return t;
    }

    /// <summary>Does this action apply to the given selection?</summary>
    public static bool Matches(ActionDefinition a, string text)
    {
        switch ((a.Match ?? MatchKinds.Any).ToLowerInvariant())
        {
            case MatchKinds.Url: return IsUrl(text);
            case MatchKinds.Email: return IsEmail(text);
            case MatchKinds.Regex:
                if (string.IsNullOrWhiteSpace(a.Regex)) return true;
                var rx = GetCustom(a.Regex);
                return rx != null && rx.IsMatch(text);
            default: return true;
        }
    }

    private static Regex? GetCustom(string pattern)
    {
        lock (CustomCache)
        {
            if (CustomCache.TryGetValue(pattern, out var cached)) return cached;
            Regex? rx = null;
            try { rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromMilliseconds(200)); }
            catch (Exception ex) { Log.Warn($"bad regex '{pattern}': {ex.Message}"); }
            CustomCache[pattern] = rx;
            return rx;
        }
    }
}
