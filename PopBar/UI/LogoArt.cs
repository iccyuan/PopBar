using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PopBar.UI;

/// <summary>
/// The PopBar mark, defined once as vector art: a gradient tile with a popup bar hovering over a text selection.
/// The settings header, window icons, tray icon and the exe's .ico (via <c>--export-icon</c>) all come from here.
/// </summary>
public static class LogoArt
{
    private static readonly Color From = Color.FromRgb(0x4F, 0x8B, 0xFF);
    private static readonly Color To = Color.FromRgb(0x7C, 0x4D, 0xFF);
    private static readonly Color Dot = Color.FromRgb(0x5B, 0x6B, 0xFF);

    /// <summary>The mark drawn into a <paramref name="s"/>×<paramref name="s"/> box. Small sizes drop fine detail.</summary>
    public static Drawing Create(double s)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            // Tile with a soft top sheen.
            var tile = new Rect(0, 0, s, s);
            double r = s * 0.235;
            var bg = new LinearGradientBrush(From, To, new Point(0, 0), new Point(1, 1));
            dc.DrawRoundedRectangle(bg, null, tile, r, r);
            var sheen = new LinearGradientBrush(Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(0, 0.6));
            dc.DrawRoundedRectangle(sheen, null, tile, r, r);

            bool tiny = s < 24;

            // Popup bar: white pill with a small tail pointing at the selection.
            double pw = s * (tiny ? 0.74 : 0.68), ph = s * (tiny ? 0.34 : 0.28);
            double px = (s - pw) / 2, py = s * (tiny ? 0.2 : 0.22);
            if (!tiny)
            {
                var shadow = new SolidColorBrush(Color.FromArgb(48, 0x1E, 0x1B, 0x6B));
                dc.DrawRoundedRectangle(shadow, null, new Rect(px, py + s * 0.025, pw, ph), ph / 2, ph / 2);
            }
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(px, py, pw, ph), ph / 2, ph / 2);
            double tw = s * 0.15, th = s * 0.085, cx = s / 2, ty = py + ph - s * 0.01;
            var tail = new StreamGeometry();
            using (var t = tail.Open())
            {
                t.BeginFigure(new Point(cx - tw / 2, ty), true, true);
                t.LineTo(new Point(cx + tw / 2, ty), true, false);
                t.LineTo(new Point(cx, ty + th), true, false);
            }
            dc.DrawGeometry(Brushes.White, null, tail);

            if (!tiny)
            {
                double d = s * 0.08, dy = py + ph / 2;
                var dot = new SolidColorBrush(Dot);
                foreach (var k in new[] { -1, 0, 1 })
                    dc.DrawEllipse(dot, null, new Point(cx + k * pw * 0.25, dy), d / 2, d / 2);
            }

            // Text selection underneath.
            double sw = s * (tiny ? 0.62 : 0.56), sh = s * (tiny ? 0.14 : 0.11);
            var sel = new Rect((s - sw) / 2, s * (tiny ? 0.68 : 0.67), sw, sh);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), null, sel, sh / 2, sh / 2);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Vector image for XAML (scales crisply to any size).</summary>
    public static ImageSource Image { get; } = Freeze(new DrawingImage(Create(64)));

    /// <summary>Pixel-exact rendering at <paramref name="px"/> pixels, using the small-size variant when needed.</summary>
    public static BitmapSource Render(int px)
    {
        var v = new DrawingVisual();
        RenderOptions.SetEdgeMode(v, EdgeMode.Unspecified);
        using (var dc = v.RenderOpen()) dc.DrawDrawing(Create(px));
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Multi-size window icon (16…256) so the taskbar / alt-tab pick a sharp frame at any DPI.</summary>
    public static ImageSource WindowIcon { get; } = LoadIcon();

    private static ImageSource LoadIcon()
    {
        using var ms = new MemoryStream(BuildIco());
        var dec = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return dec.Frames[0];
    }

    /// <summary>ICO container with PNG-compressed frames.</summary>
    public static byte[] BuildIco()
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = sizes.Select(s =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(s)));
            using var m = new MemoryStream();
            enc.Save(m);
            return m.ToArray();
        }).ToArray();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        w.Flush();
        return ms.ToArray();
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
