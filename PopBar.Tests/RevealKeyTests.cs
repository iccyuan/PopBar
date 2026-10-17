using System.Windows.Input;
using PopBar.Core;

namespace PopBar.Tests;

public class RevealKeyTests
{
    private const int VK_SHIFT = 0x10, VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_F8 = 0x77;

    [Theory]
    [InlineData("alt", VK_MENU)]
    [InlineData("shift", VK_SHIFT)]
    [InlineData("F8", VK_F8)]
    [InlineData("CapsLock", VK_CAPITAL)]
    [InlineData("A", VK_MENU)]      // would eat typing: falls back to Alt
    [InlineData("Space", VK_MENU)]
    [InlineData("", VK_MENU)]
    [InlineData(null, VK_MENU)]
    public void Reveal_key_resolves_and_unsafe_keys_fall_back_to_alt(string? key, int vk) =>
        Assert.Equal(vk, SelectionEngine.RevealKeyVk(key));

    [Theory]
    [InlineData(Key.LeftAlt, true)]
    [InlineData(Key.RWin, true)]
    [InlineData(Key.F12, true)]
    [InlineData(Key.Insert, true)]
    [InlineData(Key.Enter, false)]
    [InlineData(Key.Tab, false)]
    [InlineData(Key.Left, false)]
    [InlineData(Key.D1, false)]
    public void Only_keys_that_do_nothing_while_typing_are_allowed(Key key, bool allowed) =>
        Assert.Equal(allowed, SelectionEngine.IsAllowedRevealKey(key));
}

public class SelectionStartTests
{
    private static Selection Drag(int sx, int sy, int x, int y) => new("t", IntPtr.Zero, x, y) { StartX = sx, StartY = sy };

    [Fact]
    public void One_line_drag_starts_at_its_left_end_either_way()
    {
        Assert.Equal(100, Drag(100, 50, 400, 52).TextStartX(12));
        Assert.Equal(100, Drag(400, 50, 100, 52).TextStartX(12));
    }

    [Fact]
    public void Multi_line_drag_starts_at_the_end_on_the_upper_line()
    {
        Assert.Equal(300, Drag(300, 50, 80, 200).TextStartX(12));  // dragged down
        Assert.Equal(300, Drag(80, 200, 300, 50).TextStartX(12));  // dragged up
    }

    [Fact]
    public void Without_a_drag_the_reported_text_or_nothing()
    {
        Assert.Null(new Selection("t", IntPtr.Zero, 10, 10).TextStartX(12));
    }
}
