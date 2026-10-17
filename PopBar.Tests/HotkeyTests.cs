using PopBar.Plugins;

namespace PopBar.Tests;

public class HotkeyParseTests
{
    private const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

    [Theory]
    [InlineData("Ctrl+Alt+O", Ctrl | Alt, 0x4F)]
    [InlineData("ctrl + shift + f1", Ctrl | Shift, 0x70)]
    [InlineData("Win+Space", Win, 0x20)]
    [InlineData("Control+a", Ctrl, 0x41)]
    [InlineData("Alt+1", Alt, 0x31)]
    public void Parses(string gesture, uint mods, uint vk)
    {
        Assert.True(HotkeyManager.TryParse(gesture, out var m, out var v));
        Assert.Equal(mods, m);
        Assert.Equal(vk, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+NoSuchKey")]
    public void Rejects(string gesture) => Assert.False(HotkeyManager.TryParse(gesture, out _, out _));
}
