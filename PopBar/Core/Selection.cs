using PopBar.Sdk;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>A captured selection: text, where it came from, and where the mouse was (released).</summary>
public sealed record Selection(string Text, IntPtr SourceWindow, int X, int Y)
{
    /// <summary>Where a drag selection was started (mouse-down y); null for double / shift clicks and external text.</summary>
    public int? StartY { get; init; }
    /// <summary>The highest line the mouse touched: a drag down from line 1 ends on the last line, the bar belongs above line 1.</summary>
    public int Top => Math.Min(Y, StartY ?? Y);
    /// <summary>The lowest line the mouse touched, for placing the bar below the selection.</summary>
    public int Bottom => Math.Max(Y, StartY ?? Y);

    public SelectionContext ToContext() =>
        new(Text, SourceWindow, SourceWindow == IntPtr.Zero ? "" : GetWindowProcessName(SourceWindow), X, Y);

    public static Selection FromContext(SelectionContext c) => new(c.Text, c.SourceWindow, c.ScreenX, c.ScreenY);
}
