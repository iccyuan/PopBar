using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PopBar.UI;

/// <summary>A small "i" icon that shows its <see cref="Text"/> as a tooltip. Keeps settings rows short.</summary>
public sealed class InfoTip : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(InfoTip),
        new PropertyMetadata("", (d, e) => ((InfoTip)d).ToolTip = e.NewValue as string));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public InfoTip()
    {
        Width = 20; Height = 20;
        Margin = new Thickness(6, 0, 0, 0);
        Background = Brushes.Transparent;   // hit-testable so the tooltip opens
        Cursor = Cursors.Help;
        VerticalAlignment = VerticalAlignment.Center;
        var icon = new IconImage { Icon = "info", Size = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(IconImage.BrushProperty, "MutedBrush");
        Child = icon;
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, 20000);
    }
}
