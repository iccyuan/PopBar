using PopBar.Core;

namespace PopBar.Tests;

public class CaptureChainTests
{
    private sealed class Fake(CaptureMethod method, Func<CaptureAttempt> result) : ITextCaptureStrategy
    {
        public int Calls;
        public CaptureMethod Method => method;
        public bool CanRun(CaptureContext ctx) => true;
        public CaptureAttempt Capture(CaptureContext ctx) { Calls++; return result(); }
    }

    private static CapturedText? Run(out CaptureMethod method, params ITextCaptureStrategy[] chain) =>
        new TextCapture(chain).Capture(0, 0, allowClipboard: true, Array.Empty<string>(), out method);

    [Fact]
    public void Settled_no_selection_stops_before_the_clipboard()
    {
        var clipboard = new Fake(CaptureMethod.Clipboard, () => CaptureAttempt.From("copied", null, settled: false));
        var got = Run(out var method, new Fake(CaptureMethod.Native, () => CaptureAttempt.NoSelection), clipboard);
        Assert.Null(got);
        Assert.Equal(CaptureMethod.Native, method);
        Assert.Equal(0, clipboard.Calls);
    }

    [Fact]
    public void Unsupported_and_failing_strategies_fall_through()
    {
        var got = Run(out var method,
            new Fake(CaptureMethod.Native, () => CaptureAttempt.Unsupported),
            new Fake(CaptureMethod.Uia, () => throw new InvalidOperationException("broken provider")),
            new Fake(CaptureMethod.IAccessible2, () => CaptureAttempt.Got(new CapturedText("hello"))));
        Assert.Equal("hello", got?.Text);
        Assert.Equal(CaptureMethod.IAccessible2, method);
    }

    [Fact]
    public void Unsettled_empty_answer_moves_on()
    {
        var got = Run(out var method,
            new Fake(CaptureMethod.Uia, () => new CaptureAttempt(null, false)),
            new Fake(CaptureMethod.Clipboard, () => CaptureAttempt.From("copied", null, settled: false)));
        Assert.Equal("copied", got?.Text);
        Assert.Equal(CaptureMethod.Clipboard, method);
    }

    [Theory]
    [InlineData("a\rb", "a\r\nb")]
    [InlineData("a\r\nb\r", "a\r\nb\r\n")]
    [InlineData("plain", "plain")]
    public void RichEdit_paragraph_marks_become_crlf(string raw, string expected) =>
        Assert.Equal(expected, NativeControlCaptureStrategy.NormalizeBreaks(raw));
}
