using PopBar.Sdk;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>A captured selection: text, where it came from, and where the mouse was.</summary>
public sealed record Selection(string Text, IntPtr SourceWindow, int X, int Y)
{
    public SelectionContext ToContext() =>
        new(Text, SourceWindow, SourceWindow == IntPtr.Zero ? "" : GetWindowProcessName(SourceWindow), X, Y);

    public static Selection FromContext(SelectionContext c) => new(c.Text, c.SourceWindow, c.ScreenX, c.ScreenY);
}
