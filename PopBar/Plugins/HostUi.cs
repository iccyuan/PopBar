using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PopBar.Core;
using PopBar.Sdk;
using PopBar.UI;

namespace PopBar.Plugins;

/// <summary>Host-styled controls for plugin settings views (see <see cref="IHostUi"/>).</summary>
internal sealed class HostUi : IHostUi
{
    private sealed record AppPick(string Exe, string Label);
    private sealed record IconItem(string Name);

    public FrameworkElement KeyBox(string? current, Action<string> onChanged)
    {
        var box = new KeyCaptureBox { Mode = KeyCaptureBox.CaptureMode.Single, Gesture = current ?? "" };
        box.GestureChanged += onChanged;
        return box;
    }

    public FrameworkElement HotkeyBox(string? current, Action<string> onChanged)
    {
        var box = new KeyCaptureBox { Mode = KeyCaptureBox.CaptureMode.Chord, Gesture = current ?? "", MinWidth = 140 };
        box.GestureChanged += onChanged;
        return box;
    }

    public FrameworkElement AppPicker(Action<string> onPicked, double width = 300)
    {
        var combo = new ComboBox { Width = width, DisplayMemberPath = "Label", SelectedValuePath = "Exe" };
        combo.DropDownOpened += (_, _) => combo.ItemsSource = RunningApps().Select(a => new AppPick(a.Exe, $"{a.Exe}  —  {a.Title}")).ToList();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is AppPick p)
            {
                onPicked(p.Exe);
                combo.SelectedItem = null;
            }
        };
        return combo;
    }

    public FrameworkElement FileBrowse(string label, string filter, Action<string> onPicked)
    {
        var btn = new Button { Content = label };
        btn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            if (dlg.ShowDialog() == true) onPicked(dlg.FileName);
        };
        return btn;
    }

    public FrameworkElement Icon(string name, double size = 16, Brush? brush = null)
    {
        var img = new IconImage { Icon = name, Size = size };
        if (brush != null) img.Brush = brush; else img.SetResourceReference(IconImage.BrushProperty, "TextBrush");
        return img;
    }

    public FrameworkElement IconPicker(string? current, Action<string> onChanged, double width = 200)
    {
        var combo = new ComboBox { Width = width };
        combo.ItemsSource = Icons.Names.Select(n => new IconItem(n)).ToList();
        combo.ItemTemplate = BuildIconTemplate();
        combo.SelectedItem = ((List<IconItem>)combo.ItemsSource).FirstOrDefault(i => string.Equals(i.Name, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is IconItem i) onChanged(i.Name); };
        return combo;
    }

    private static DataTemplate BuildIconTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(IconImage));
        icon.SetBinding(IconImage.IconProperty, new System.Windows.Data.Binding("Name"));
        icon.SetValue(IconImage.SizeProperty, 16.0);
        icon.SetResourceReference(IconImage.BrushProperty, "TextBrush");
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(icon);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    public IReadOnlyList<string> IconNames => Icons.Names.ToList();

    public bool EditDialog(string title, FrameworkElement content, double width = 540)
    {
        var dlg = new EditDialog(title, content, width) { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
        return dlg.ShowDialog() == true;
    }

    public IReadOnlyList<RunningApp> RunningApps() => Core.RunningApps.List();
}
