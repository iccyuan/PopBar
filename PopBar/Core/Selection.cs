using PopBar.Sdk;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>A captured selection: text, where it came from, and where the mouse was (released).</summary>
public sealed record Selection(string Text, IntPtr SourceWindow, int X, int Y)
{
    /// <summary>
    /// The selected text's on-screen rectangle (physical pixels, visible lines only), from UI Automation. Null when the
    /// app does not report it; the placement then estimates it from the mouse (see <see cref="StartY"/>).
    /// </summary>
    internal RECT? Bounds { get; init; }
    /// <summary>Where a drag selection was started (mouse-down x); null for double / shift clicks and external text.</summary>
    public int? StartX { get; init; }
    /// <summary>Where a drag selection was started (mouse-down y); null for double / shift clicks and external text.</summary>
    public int? StartY { get; init; }
    /// <summary>A text line's height (px) from the system caret, for the estimate without <see cref="Bounds"/>.</summary>
    public int? LineHeight { get; init; }
    /// <summary>The highest line the mouse touched: a drag down from line 1 ends on the last line, the bar belongs above line 1.</summary>
    public int Top => Math.Min(Y, StartY ?? Y);
    /// <summary>The lowest line the mouse touched, for placing the bar below the selection.</summary>
    public int Bottom => Math.Max(Y, StartY ?? Y);

    /// <summary>
    /// Where the selected text begins on screen (x, px), for the bar to start there: of a drag's two ends the one on the
    /// upper line, or on one line the left one (<paramref name="sameLine"/> = how far apart in y still counts as one
    /// line); else the reported text's left edge; null when only the mouse is known.
    /// </summary>
    public int? TextStartX(int sameLine)
    {
        if (StartX is { } sx && StartY is { } sy)
            return Math.Abs(sy - Y) <= sameLine ? Math.Min(sx, X) : sy < Y ? sx : X;
        return Bounds?.Left;
    }

    /// <summary>Half a text line (DIP), for the estimate when the app reports neither the text nor its line height.</summary>
    internal const double HalfLineDip = 12;

    /// <summary>
    /// The selected text on screen: <see cref="Bounds"/> when reported, else estimated - the lines the mouse touched,
    /// half a line around each, between the drag's two ends.
    /// </summary>
    internal RECT Area()
    {
        if (Bounds is { } b) return b;
        int half = LineHeight is { } lh ? (lh + 1) / 2 : (int)Math.Round(HalfLineDip * GetMonitorAt(X, Y).dpi / 96.0);
        int left = Math.Min(X, StartX ?? X), right = Math.Max(X, StartX ?? X);
        return new RECT { Left = left, Top = Top - half, Right = right + 1, Bottom = Bottom + half };
    }

    public SelectionContext ToContext()
    {
        var a = Area();
        return new(Text, SourceWindow, SourceWindow == IntPtr.Zero ? "" : GetWindowProcessName(SourceWindow), X, Y)
        {
            Area = new ScreenRect(a.Left, a.Top, a.Right, a.Bottom),
        };
    }

    /// <summary>Text handed in by a plugin (<see cref="IPluginHost.ShowPopup"/>): its <see cref="SelectionContext.Area"/>, if any, counts as reported.</summary>
    public static Selection FromContext(SelectionContext c) => new(c.Text, c.SourceWindow, c.ScreenX, c.ScreenY)
    {
        Bounds = c.Area is { } a ? new RECT { Left = a.Left, Top = a.Top, Right = a.Right, Bottom = a.Bottom } : null,
    };
}
