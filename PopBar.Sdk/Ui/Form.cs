using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PopBar.Sdk.Ui;

/// <summary>
/// Tiny helpers for building a settings view that matches PopBar's look without writing XAML.
/// Styles are resolved from the host application's resources when present.
/// </summary>
public static class Form
{
    private static object? Res(string key)
    {
        try { return Application.Current?.TryFindResource(key); } catch { return null; }
    }

    public static StackPanel Stack(params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>A labelled row: label + hint on the left, control on the right.</summary>
    public static Grid Row(string label, FrameworkElement control, string? hint = null)
    {
        var g = new Grid { MinHeight = 44 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 12, 6) };
        left.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(hint))
        {
            // Long explanations live in a tooltip behind a small "i" so rows stay compact.
            var muted = (Brush?)Res("MutedBrush") ?? Brushes.Gray;
            var i = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.2), BorderBrush = muted,
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
                ToolTip = hint, Cursor = System.Windows.Input.Cursors.Help,
                Child = new TextBlock { Text = "i", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) },
            };
            ToolTipService.SetInitialShowDelay(i, 150);
            ToolTipService.SetShowDuration(i, 20000);
            left.Children.Add(i);
        }
        g.Children.Add(left);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    /// <summary>A card containing rows separated by hairlines.</summary>
    public static Border Card(params UIElement[] rows)
    {
        var panel = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) panel.Children.Add(new System.Windows.Shapes.Rectangle { Height = 1, Fill = (Brush?)Res("BorderBrush") ?? Brushes.LightGray });
            panel.Children.Add(rows[i]);
        }
        var b = new Border { Child = panel, Margin = new Thickness(0, 0, 0, 8) };
        if (Res("Card") is Style s) b.Style = s;
        else { b.Background = Brushes.White; b.BorderBrush = Brushes.LightGray; b.BorderThickness = new Thickness(1); b.CornerRadius = new CornerRadius(10); b.Padding = new Thickness(16, 4, 16, 4); }
        return b;
    }

    public static TextBlock Section(string title)
    {
        var t = new TextBlock { Text = title };
        if (Res("H2") is Style s) t.Style = s; else { t.Margin = new Thickness(4, 18, 0, 8); }
        return t;
    }

    public static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 8) };
        if (Res("Muted") is Style s) t.Style = s; else t.Foreground = Brushes.Gray;
        return t;
    }

    public static TextBox TextBox(string value, double width = 260, Action<string>? onChanged = null, bool multiline = false)
    {
        var tb = new TextBox { Text = value, Width = width, MinHeight = multiline ? 70 : 34 };
        if (multiline) { tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap; tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; tb.VerticalContentAlignment = VerticalAlignment.Top; }
        if (onChanged != null) tb.TextChanged += (_, _) => onChanged(tb.Text);
        return tb;
    }

    public static PasswordBox Password(string value, double width = 260, Action<string>? onChanged = null)
    {
        var pb = new PasswordBox { Password = value, Width = width, MinHeight = 34 };
        if (onChanged != null) pb.PasswordChanged += (_, _) => onChanged(pb.Password);
        return pb;
    }

    public static CheckBox Toggle(bool value, Action<bool>? onChanged = null)
    {
        var cb = new CheckBox { IsChecked = value };
        if (Res("Switch") is Style s) cb.Style = s;
        if (onChanged != null) { cb.Checked += (_, _) => onChanged(true); cb.Unchecked += (_, _) => onChanged(false); }
        return cb;
    }

    public static ComboBox Combo(IEnumerable<(string Key, string Label)> items, string selectedKey, Action<string>? onChanged = null, double width = 200)
    {
        var cb = new ComboBox { Width = width, DisplayMemberPath = "Value", SelectedValuePath = "Key" };
        cb.ItemsSource = items.Select(i => new KeyValuePair<string, string>(i.Key, i.Label)).ToList();
        cb.SelectedValue = selectedKey;
        if (cb.SelectedIndex < 0) cb.SelectedIndex = 0;
        if (onChanged != null) cb.SelectionChanged += (_, _) => { if (cb.SelectedValue is string k) onChanged(k); };
        return cb;
    }

    public static Button Button(string label, Action onClick, bool accent = false)
    {
        var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
        if (accent && Res("AccentButton") is Style s) b.Style = s;
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// One compact list row (title + subtitle, enable switch, edit / delete) in the same look as PopBar's action list.
    /// Pass <paramref name="ui"/> to get icon buttons; without it the buttons are text.
    /// </summary>
    public static Border ListRow(string title, string? subtitle, bool enabled, Action<bool>? onToggle, Action? onEdit, Action? onDelete, IHostUi? ui = null, string? icon = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (ui != null && icon != null)
        {
            var box = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            box.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            var ic = ui.Icon(icon, 16);
            ic.HorizontalAlignment = HorizontalAlignment.Center; ic.VerticalAlignment = VerticalAlignment.Center;
            box.Child = ic;
            g.Children.Add(box);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15 });
        if (!string.IsNullOrEmpty(subtitle))
        {
            var sub = new TextBlock { Text = subtitle, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            if (Res("Muted") is Style ms) sub.Style = ms; else sub.Foreground = Brushes.Gray;
            text.Children.Add(sub);
        }
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        if (onEdit != null) buttons.Children.Add(IconOrTextButton(ui, "pencil", "编辑", onEdit));
        if (onDelete != null) buttons.Children.Add(IconOrTextButton(ui, "trash", "删除", onDelete, danger: true));
        Grid.SetColumn(buttons, 2);
        g.Children.Add(buttons);

        if (onToggle != null)
        {
            var sw = Toggle(enabled, onToggle);
            sw.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(sw, 3);
            g.Children.Add(sw);
        }

        var row = new Border { Child = g, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8) };
        if (Res("Card") is Style s) row.Style = s;
        return row;
    }

    private static Button IconOrTextButton(IHostUi? ui, string icon, string text, Action onClick, bool danger = false)
    {
        var b = new Button();
        if (ui != null)
        {
            if (Res("IconButton") is Style s) b.Style = s;
            var brush = (Brush?)Res(danger ? "DangerBrush" : "MutedBrush") ?? Brushes.Gray;
            b.Content = ui.Icon(icon, 14, brush);
            b.ToolTip = text;
        }
        else
        {
            b.Content = text;
            b.Margin = new Thickness(0, 0, 6, 0);
        }
        b.Click += (_, _) => onClick();
        return b;
    }

    public static StackPanel Buttons(params UIElement[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }
}
