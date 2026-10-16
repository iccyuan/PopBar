using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PopBar.Sdk;

namespace PopBar.UI;

/// <summary>One entry of a model-provider picker: id, display text and the logo drawn in front of it.</summary>
internal sealed record ProviderItem(string Key, string Value, string Logo)
{
    /// <summary>Icon for providers that declare no <see cref="ILlmProvider.Logo"/>.</summary>
    public const string DefaultLogo = "bot";

    public static string LogoOf(ILlmProvider? p) => string.IsNullOrWhiteSpace(p?.Logo) ? DefaultLogo : p!.Logo!;

    /// <summary>Logo + text, the code-built twin of the combo template on the settings "服务" page.</summary>
    public static DataTemplate Template()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(IconImage));
        icon.SetBinding(IconImage.IconProperty, new Binding(nameof(Logo)));
        icon.SetValue(IconImage.SizeProperty, 16.0);
        icon.SetResourceReference(IconImage.BrushProperty, "TextBrush");
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Value)));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(icon);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }
}
