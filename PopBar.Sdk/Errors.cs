namespace PopBar.Sdk;

/// <summary>Turns an exception into the one-line text a panel should show.</summary>
public static class Errors
{
    private static readonly string[] Generic =
    {
        "An error occurred while sending the request.",
        "One or more errors occurred.",
        "Exception of type",
    };

    /// <summary>
    /// The messages along the inner-exception chain, outermost first, skipping the generic wrappers .NET adds
    /// ("An error occurred while sending the request.") so the real cause - a TLS failure, a refused connection,
    /// a DNS miss - is what the user reads.
    /// </summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is AggregateException agg && agg.InnerExceptions.Count == 1) continue;
            var m = e.Message.Trim();
            int cut = m.IndexOf(", see inner exception", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) m = m[..cut];
            m = m.TrimEnd('.', ' ');
            if (m.Length == 0 || Generic.Any(g => m.StartsWith(g.TrimEnd('.'), StringComparison.Ordinal))) continue;
            if (!parts.Contains(m)) parts.Add(m);
        }
        return parts.Count == 0 ? ex.GetType().Name : string.Join("：", parts);
    }
}
