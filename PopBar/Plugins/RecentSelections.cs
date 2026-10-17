using PopBar.Sdk;

namespace PopBar.Plugins;

/// <summary>
/// The selections actions ran on lately. Plugins open their panels at the selection's point
/// (<see cref="SelectionContext.ScreenX"/> / <see cref="SelectionContext.ScreenY"/>) without saying where the text is;
/// a panel anchored at such a point looks the selection up here and keeps clear of its text.
/// </summary>
internal static class RecentSelections
{
    private const int Keep = 8;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private static readonly List<(int X, int Y, ScreenRect Area, DateTime At)> Items = new();

    public static void Remember(SelectionContext c)
    {
        if (c.Area is not { } area) return;
        lock (Items)
        {
            Items.RemoveAll(i => i.X == c.ScreenX && i.Y == c.ScreenY);
            Items.Add((c.ScreenX, c.ScreenY, area, DateTime.UtcNow));
            if (Items.Count > Keep) Items.RemoveAt(0);
        }
    }

    /// <summary>The text of the latest selection whose point this is (within a pixel), if recent.</summary>
    public static ScreenRect? AreaAt(int x, int y)
    {
        lock (Items)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var it = Items[i];
                if (Math.Abs(it.X - x) <= 1 && Math.Abs(it.Y - y) <= 1 && DateTime.UtcNow - it.At < MaxAge) return it.Area;
            }
        }
        return null;
    }
}
