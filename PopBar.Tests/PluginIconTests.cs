using System.Windows.Media;
using System.Windows.Media.Imaging;
using PopBar.UI;

namespace PopBar.Tests;

/// <summary>Plugins bring their own icons: inline SVG anywhere an icon is taken, or registered by name.</summary>
public sealed class PluginIconTests
{
    private const string Lucide =
        """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 8v4"/><path d="M12 16h.01"/></svg>""";

    [Fact]
    public void A_line_icon_keeps_its_strokes_and_drops_its_colours()
    {
        var parts = SvgIcon.Parse(Lucide);
        Assert.Equal(3, parts.Length);
        Assert.All(parts, p =>
        {
            Assert.False(p.Fill);
            Assert.Equal(2, p.Stroke, 3);
            Assert.Equal(PenLineCap.Round, p.Cap);
        });
    }

    [Fact]
    public void Any_view_box_is_fitted_into_the_icon_square()
    {
        var parts = SvgIcon.Parse("""<svg viewBox="0 0 48 96"><g transform="translate(0,48)"><rect width="48" height="48" fill="#f00"/></g></svg>""");
        var b = Assert.Single(parts).Geometry.Bounds;   // 48x96 scaled by 1/4 and centred: x 6..18, y 12..24
        Assert.Equal(6, b.Left, 3);
        Assert.Equal(18, b.Right, 3);
        Assert.Equal(12, b.Top, 3);
        Assert.Equal(24, b.Bottom, 3);
        Assert.True(parts[0].Fill);
    }

    [Theory]
    [InlineData("<svg viewBox='0 0 24 24'></svg>")]   // nothing to draw
    [InlineData("<svg><path d='M 1 2 Q'/></svg>")]     // broken path
    [InlineData("<div/>")]
    [InlineData("<svg")]
    public void Svg_that_cannot_be_drawn_is_refused(string svg) => Assert.Throws<FormatException>(() => SvgIcon.Parse(svg));

    [Fact]
    public void Registered_names_follow_the_rules_and_leave_with_their_plugin()
    {
        string name = "test-" + Guid.NewGuid().ToString("N")[..8];
        Assert.NotNull(Icons.Register("copy", Lucide, "p1"));          // built-in
        Assert.NotNull(Icons.Register(name, "<svg/>", "p1"));          // nothing to draw
        Assert.NotNull(Icons.Register(name, "not an icon", "p1"));
        Assert.Null(Icons.Register(name, Lucide, "p1"));
        Assert.Null(Icons.Register(name, Lucide, "P1"));               // the same plugin may register again
        Assert.NotNull(Icons.Register(name, Lucide, "p2"));            // another plugin's name
        Assert.Contains(name, Icons.Names);

        Icons.Unregister("p1");
        Assert.DoesNotContain(name, Icons.Names);
        Assert.Null(Icons.Register(name, Lucide, "p2"));               // free again
        Icons.Unregister("p2");
    }

    private static void OnSta(Action body)
    {
        Exception? err = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { err = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw new Exception("on STA: " + err.Message, err);
    }

    private static byte[] Pixels(BitmapSource b)
    {
        var px = new byte[b.PixelWidth * b.PixelHeight * 4];
        b.CopyPixels(px, b.PixelWidth * 4, 0);
        return px;
    }

    [Fact]
    public void Inline_and_registered_svg_draw_and_an_unknown_name_falls_back_to_a_circle() => OnSta(() =>
    {
        string name = "test-" + Guid.NewGuid().ToString("N")[..8];
        var square = """<svg viewBox="0 0 24 24"><rect x="2" y="2" width="20" height="20"/></svg>""";
        var inline = Pixels(Icons.Render(square, 24, Colors.Black));
        Assert.True(inline.Where((_, i) => i % 4 == 3).Count(a => a > 0) > 300);   // a filled 20x20 square

        Assert.Null(Icons.Register(name, square, "p"));
        Assert.Equal(inline, Pixels(Icons.Render(name, 24, Colors.Black)));

        Icons.Unregister("p");
        Assert.Equal(Pixels(Icons.Render("circle", 24, Colors.Black)), Pixels(Icons.Render(name, 24, Colors.Black)));
    });
}
