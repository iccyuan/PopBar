using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PopBar.Plugins.Builtin.Screenshot;

internal enum ShotTool { None, Rect, Ellipse, Arrow, Pen, Mosaic, Text }

/// <summary>One annotation in overlay DIP coordinates. Rendered the same way on screen and into the exported bitmap.</summary>
internal abstract class Annot
{
    public Color Color { get; init; }
    public double Width { get; init; }

    protected Pen MakePen()
    {
        var b = new SolidColorBrush(Color); b.Freeze();
        var p = new Pen(b, Width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    public abstract void Draw(DrawingContext dc);
}

internal sealed class RectAnnot : Annot
{
    public Rect R;
    public override void Draw(DrawingContext dc) => dc.DrawRoundedRectangle(null, MakePen(), R, 2, 2);
}

internal sealed class EllipseAnnot : Annot
{
    public Rect R;
    public override void Draw(DrawingContext dc) =>
        dc.DrawEllipse(null, MakePen(), new Point(R.X + R.Width / 2, R.Y + R.Height / 2), R.Width / 2, R.Height / 2);
}

internal sealed class ArrowAnnot : Annot
{
    public Point A, B;
    public override void Draw(DrawingContext dc)
    {
        var v = B - A;
        if (v.Length < 1) return;
        v.Normalize();
        double head = Math.Max(10, Width * 4.5);
        var basePt = B - v * head;
        var n = new Vector(-v.Y, v.X) * head * 0.45;
        var pen = MakePen();
        dc.DrawLine(pen, A, basePt + v * 1);
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(B, true, true);
            g.LineTo(basePt + n, true, true);
            g.LineTo(basePt - n, true, true);
        }
        tri.Freeze();
        dc.DrawGeometry(pen.Brush, null, tri);
    }
}

internal sealed class PenAnnot : Annot
{
    public List<Point> Points = new();
    public override void Draw(DrawingContext dc)
    {
        if (Points.Count < 2) { if (Points.Count == 1) dc.DrawEllipse(MakePen().Brush, null, Points[0], Width / 2, Width / 2); return; }
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Points[0], false, false);
            c.PolyLineTo(Points.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, MakePen(), g);
    }
}

internal sealed class MosaicAnnot : Annot
{
    public Rect R;
    public ImageSource? Pixels; // pre-pixelated bitmap of the region; null while dragging
    public override void Draw(DrawingContext dc)
    {
        if (Pixels != null) dc.DrawImage(Pixels, R);
        else
        {
            var b = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)); b.Freeze();
            dc.DrawRectangle(b, null, R);
        }
    }

    /// <summary>Block-average the frozen screen under <paramref name="pxRect"/> (physical pixels).</summary>
    public static BitmapSource Pixelate(BitmapSource screen, Int32Rect pxRect, int block)
    {
        int w = pxRect.Width, h = pxRect.Height, stride = w * 4;
        var src = new byte[stride * h];
        screen.CopyPixels(pxRect, src, stride, 0);
        var dst = new byte[src.Length];
        for (int by = 0; by < h; by += block)
        for (int bx = 0; bx < w; bx += block)
        {
            int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by);
            long sb = 0, sg = 0, sr = 0; int n = bw * bh;
            for (int y = by; y < by + bh; y++)
            for (int x = bx; x < bx + bw; x++)
            {
                int i = y * stride + x * 4;
                sb += src[i]; sg += src[i + 1]; sr += src[i + 2];
            }
            byte b = (byte)(sb / n), gg = (byte)(sg / n), r = (byte)(sr / n);
            for (int y = by; y < by + bh; y++)
            for (int x = bx; x < bx + bw; x++)
            {
                int i = y * stride + x * 4;
                dst[i] = b; dst[i + 1] = gg; dst[i + 2] = r; dst[i + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, dst, stride);
        bmp.Freeze();
        return bmp;
    }
}

internal sealed class TextAnnot : Annot
{
    public Point P;
    public string Text = "";
    public double FontSize = 18;
    public double PixelsPerDip = 1;

    public override void Draw(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text)) return;
        var b = new SolidColorBrush(Color); b.Freeze();
        var ft = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            FontSize, b, PixelsPerDip);
        dc.DrawText(ft, P);
    }
}

/// <summary>Draws all annotations (plus the one being drawn), clipped to the current selection.</summary>
internal sealed class AnnotationLayer : FrameworkElement
{
    public List<Annot> Items { get; } = new();
    public Annot? Current { get; set; }
    public Rect ClipRect { get; set; }

    public AnnotationLayer() { IsHitTestVisible = false; }

    protected override void OnRender(DrawingContext dc)
    {
        if (ClipRect.IsEmpty || ClipRect.Width <= 0) return;
        dc.PushClip(new RectangleGeometry(ClipRect));
        foreach (var a in Items) a.Draw(dc);
        Current?.Draw(dc);
        dc.Pop();
    }
}
