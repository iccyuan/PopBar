using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace PopBar.UI;

/// <summary>
/// "Frosted glass" without the OS: snapshot the screen under a (not yet visible) window rect, Gaussian-blur it
/// and hand it back as a brush. Works on every Windows version and for never-activated windows, where the
/// DWM / accent-policy acrylic falls back to a flat colour.
/// </summary>
internal static class FrostedGlass
{
    /// <summary>Blurred image of the physical screen rect (x, y, w, h). Null when capture is not possible.</summary>
    public static ImageSource? Capture(int x, int y, int w, int h, double radius = 36)
    {
        if (w <= 0 || h <= 0) return null;
        try
        {
            int pad = (int)Math.Ceiling(radius * 1.5);
            int cx = x - pad, cy = y - pad, cw = w + 2 * pad, ch = h + 2 * pad;

            BitmapSource src;
            using (var bmp = new Drawing.Bitmap(cw, ch, Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (var g = Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(cx, cy, 0, 0, bmp.Size);
                var data = bmp.LockBits(new Drawing.Rectangle(0, 0, cw, ch), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try { src = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Pbgra32, null, data.Scan0, data.Stride * ch, data.Stride); }
                finally { bmp.UnlockBits(data); }
            }
            src.Freeze();

            // Render through a Gaussian BlurEffect, then crop the padded rim (which would otherwise fade to transparent).
            var image = new Image { Source = src, Stretch = Stretch.None, Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance } };
            image.Measure(new Size(cw, ch));
            image.Arrange(new Rect(0, 0, cw, ch));
            var rtb = new RenderTargetBitmap(cw, ch, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(image);
            var cropped = new CroppedBitmap(rtb, new Int32Rect(pad, pad, w, h));
            cropped.Freeze();
            return cropped;
        }
        catch (Exception ex)
        {
            Log.Warn("frosted capture failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Blurred, down-scaled snapshot of a large screen rect (e.g. a whole monitor) so a movable window can slide a
    /// viewbox over it instead of re-capturing. Blurring at half resolution is 4x cheaper and looks identical.
    /// </summary>
    public static BitmapSource? CaptureScaled(int x, int y, int w, int h, double downscale = 0.5, double radius = 18)
    {
        if (w <= 0 || h <= 0) return null;
        try
        {
            int pad = (int)Math.Ceiling(radius * 1.5 / downscale);
            int cx = x - pad, cy = y - pad, cw = w + 2 * pad, ch = h + 2 * pad;
            BitmapSource src;
            using (var bmp = new Drawing.Bitmap(cw, ch, Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (var g = Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(cx, cy, 0, 0, bmp.Size);
                var data = bmp.LockBits(new Drawing.Rectangle(0, 0, cw, ch), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try { src = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Pbgra32, null, data.Scan0, data.Stride * ch, data.Stride); }
                finally { bmp.UnlockBits(data); }
            }
            src.Freeze();

            int sw = (int)Math.Ceiling(cw * downscale), sh = (int)Math.Ceiling(ch * downscale);
            var image = new Image { Source = src, Stretch = Stretch.Fill, Width = sw, Height = sh, Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance } };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
            image.Measure(new Size(sw, sh));
            image.Arrange(new Rect(0, 0, sw, sh));
            var rtb = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(image);
            int sp = (int)Math.Round(pad * downscale);
            var cropped = new CroppedBitmap(rtb, new Int32Rect(sp, sp, Math.Max(1, sw - 2 * sp), Math.Max(1, sh - 2 * sp)));
            cropped.Freeze();
            return cropped;
        }
        catch (Exception ex)
        {
            Log.Warn("frosted capture (scaled) failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Blurred, down-scaled copy of an image already in hand (e.g. a frozen screenshot an overlay is drawn over), same
    /// framing as the source. There is no screen beyond its edges to pad with, so the rim is filled with a mirror of the
    /// edge: the blur then does not fade to transparent at the borders.
    /// </summary>
    public static BitmapSource? Blur(BitmapSource source, double downscale = 0.5, double radius = 18)
    {
        try
        {
            int sw = Math.Max(1, (int)Math.Ceiling(source.PixelWidth * downscale)), sh = Math.Max(1, (int)Math.Ceiling(source.PixelHeight * downscale));
            int pad = (int)Math.Ceiling(radius * 1.5);
            var mirror = new ImageBrush(source)
            {
                TileMode = TileMode.FlipXY, Stretch = Stretch.Fill,
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(pad, pad, sw, sh),
            };
            var fill = new System.Windows.Shapes.Rectangle
            {
                Width = sw + 2 * pad, Height = sh + 2 * pad, Fill = mirror,
                Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
            };
            RenderOptions.SetBitmapScalingMode(fill, BitmapScalingMode.Linear);
            fill.Measure(new Size(fill.Width, fill.Height));
            fill.Arrange(new Rect(0, 0, fill.Width, fill.Height));
            var rtb = new RenderTargetBitmap(sw + 2 * pad, sh + 2 * pad, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(fill);
            var cropped = new CroppedBitmap(rtb, new Int32Rect(pad, pad, sw, sh));
            cropped.Freeze();
            return cropped;
        }
        catch (Exception ex)
        {
            Log.Warn("frosted blur failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Convenience: an ImageBrush ready for a Border background (null when capture failed).</summary>
    public static ImageBrush? Brush(int x, int y, int w, int h, double radius = 36)
    {
        var img = Capture(x, y, w, h, radius);
        if (img == null) return null;
        var b = new ImageBrush(img) { Stretch = Stretch.Fill };
        b.Freeze();
        return b;
    }
}
