using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PopBar.UI;

/// <summary>
/// Crisp line icon. Instead of scaling a 24-unit vector by a fractional factor (which lands strokes on half
/// pixels and looks blurry), the icon is rasterised once per (name, pixel size, colour) at the exact physical
/// pixel size for the current DPI and drawn 1:1.
/// </summary>
public sealed class IconImage : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(IconImage),
        new FrameworkPropertyMetadata("circle", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(IconImage),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(IconImage),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Stroke width in icon units (Lucide default 2).</summary>
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(IconImage),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    public IconImage()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int px = Math.Max(1, (int)Math.Round(Size * scale));
        var color = (Brush as SolidColorBrush)?.Color ?? Colors.Black;
        var bmp = Icons.Render(Icon, px, color, Thickness);
        // Draw at exactly px physical pixels; layout rounding keeps the origin on the pixel grid.
        double dip = px / scale;
        dc.DrawImage(bmp, new Rect((Size - dip) / 2, (Size - dip) / 2, dip, dip));
    }
}
