using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using PopBar.Core;

namespace PopBar.UI;

/// <summary>Generic modal editor (title, caller-supplied content, 保存 / 取消) in the settings window's style.</summary>
internal sealed class EditDialog : Window
{
    public EditDialog(string title, FrameworkElement content, double width, string okText = "保存")
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBgBrush");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Animated); // unhinted outlines: smooth CJK like DirectWrite apps
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 40, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = true });
        SourceInitialized += (_, _) => Acrylic.Apply(this, BackdropKind.Mica, ThemeManager.IsDark, roundCorners: true);

        var root = new StackPanel { Margin = new Thickness(24, 12, 24, 20) };
        var heading = new TextBlock { Text = title, Margin = new Thickness(-2, 0, 0, 10), IsHitTestVisible = false };
        heading.Style = (Style)FindResource("Muted");
        root.Children.Add(heading);

        // Text sits on Mica here, so give it an opaque card for ClearType.
        var card = new Border { Child = content, Padding = new Thickness(16, 6, 16, 10) };
        card.Style = (Style)FindResource("Card");
        root.Children.Add(card);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 80 };
        ok.Style = (Style)FindResource("AccentButton");
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
    }
}
