using PopBar.Actions;

namespace PopBar.Tests;

public class SplitArgsTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("\"{text}\"", new[] { "{text}" })]
    [InlineData("-q \"hello world\" --x", new[] { "-q", "hello world", "--x" })]
    [InlineData("\"\"", new[] { "" })]
    [InlineData("a\"b c\"d", new[] { "ab cd" })]
    [InlineData(@"C:\dir\file.txt", new[] { @"C:\dir\file.txt" })]
    [InlineData(@"""C:\dir\\"" next", new[] { @"C:\dir\", "next" })]
    [InlineData(@"a\""b", new[] { "a\"b" })]
    [InlineData(@"a\\\""b", new[] { "a\\\"b" })]
    [InlineData("\"say \"\"hi\"\"\"", new[] { "say \"hi\"" })]
    public void Splits_like_the_C_runtime(string input, string[] expected) =>
        Assert.Equal(expected, ActionRunner.SplitArgs(input));

    [Fact]
    public void Selection_never_changes_the_argument_count()
    {
        // Substitution happens after splitting, so quotes and spaces in the selection stay inside one argument.
        var args = ActionRunner.SplitArgs("--query {text} --end").Select(a => a.Replace("{text}", "x\" --evil \"y z")).ToList();
        Assert.Equal(3, args.Count);
        Assert.Equal("x\" --evil \"y z", args[1]);
    }
}

public class TransformOpsTests
{
    [Theory]
    [InlineData("upper", "Hello", "HELLO")]
    [InlineData("lower", "HeLLo", "hello")]
    [InlineData("title", "hello WORLD", "Hello World")]
    [InlineData("sentence", "  hELLO world", "  Hello world")]
    [InlineData("sentence", "   ", "   ")]
    [InlineData("trim", "  x  ", "x")]
    [InlineData("reverse", "abc", "cba")]
    [InlineData("none", "abc", "abc")]
    [InlineData(null, "abc", "abc")]
    [InlineData("UPPER", "a", "A")]
    public void Applies(string? op, string input, string expected) => Assert.Equal(expected, TransformOps.Apply(op, input));
}

public class MatchersTests
{
    [Theory]
    [InlineData("https://example.com/a?b=c", true)]
    [InlineData("  www.example.com  ", true)]
    [InlineData("ftp://host/file", true)]
    [InlineData("example.com", false)]
    [InlineData("https://exa mple.com", false)]
    [InlineData("not a url", false)]
    public void Url(string text, bool expected) => Assert.Equal(expected, Matchers.IsUrl(text));

    [Theory]
    [InlineData("a.b+c@example.co", true)]
    [InlineData("a@b", false)]
    [InlineData("hello world", false)]
    public void Email(string text, bool expected) => Assert.Equal(expected, Matchers.IsEmail(text));

    [Fact]
    public void Www_gets_a_scheme() => Assert.Equal("https://www.example.com", Matchers.NormalizeUrl(" www.example.com "));

    [Fact]
    public void Regex_match_kind()
    {
        var a = new ActionDefinition { Match = MatchKinds.Regex, Regex = @"^\d+$" };
        Assert.True(Matchers.Matches(a, "12345"));
        Assert.False(Matchers.Matches(a, "12a45"));
        Assert.True(Matchers.Matches(new ActionDefinition { Match = MatchKinds.Regex, Regex = "" }, "anything"));
        Assert.True(Matchers.Matches(new ActionDefinition(), "anything"));
    }
}
