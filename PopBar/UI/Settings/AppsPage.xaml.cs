using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PopBar.Config;
using PopBar.Core;

namespace PopBar.UI;

/// <summary>"应用": programs where the bar never shows, and programs where Ctrl+C is never used to read the selection.</summary>
internal partial class AppsPage : SettingsPage
{
    private readonly List<string> _excluded = new();
    private readonly List<string> _noClip = new();

    private sealed record AppPick(string Exe, string Label);

    public AppsPage() => InitializeComponent();

    protected override void OnLoad(AppConfig c)
    {
        _excluded.Clear(); _excluded.AddRange(c.ExcludedApps);
        _noClip.Clear(); _noClip.AddRange(c.NoClipboardApps);
        Render();
    }

    public override void Apply(AppConfig c)
    {
        c.ExcludedApps = _excluded.ToList();
        c.NoClipboardApps = _noClip.ToList();
    }

    private void Render()
    {
        RenderList(ExcludedList, _excluded);
        RenderList(NoClipList, _noClip);
    }

    private void RenderList(StackPanel host, List<string> items)
    {
        host.Children.Clear();
        foreach (var exe in items)
        {
            var name = exe;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new IconImage { Icon = "app-window", Size = 15, Brush = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) });
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var del = new Button { Style = Res<Style>("IconButton"), ToolTip = "移除", Content = new IconImage { Icon = "x", Size = 13, Brush = Res<Brush>("MutedBrush") } };
            del.Click += (_, _) => { items.Remove(name); Render(); RaiseEdited(); };
            Grid.SetColumn(del, 2);
            row.Children.Add(del);
            host.Children.Add(new Border { Style = Res<Style>("Card"), Padding = new Thickness(12, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(9), Child = row });
        }
    }

    /// <summary>Fill the picker with the processes that currently own a visible window.</summary>
    private void ProcessPick_Opened(object sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;
        combo.ItemsSource = RunningApps.List().Select(a => new AppPick(a.Exe, $"{a.Exe}  —  {a.Title}")).ToList();
    }

    private void Add(ComboBox picker, List<string> list)
    {
        var exe = (picker.SelectedItem as AppPick)?.Exe ?? (picker.Text ?? "").Trim();
        if (exe.Length == 0) return;
        if (!list.Contains(exe, StringComparer.OrdinalIgnoreCase)) list.Add(exe);
        picker.SelectedItem = null;
        Render();
        RaiseEdited();
    }

    private void Browse(List<string> list)
    {
        var picked = Ctx.Ui.PickFile("程序 (*.exe)|*.exe", "选择程序");
        if (picked == null) return;
        var exe = System.IO.Path.GetFileName(picked);
        if (!list.Contains(exe, StringComparer.OrdinalIgnoreCase)) list.Add(exe);
        Render();
        RaiseEdited();
    }

    private void AddExcluded_Click(object sender, RoutedEventArgs e) => Add(ExcludedPick, _excluded);
    private void AddNoClip_Click(object sender, RoutedEventArgs e) => Add(NoClipPick, _noClip);
    private void BrowseExcluded_Click(object sender, RoutedEventArgs e) => Browse(_excluded);
    private void BrowseNoClip_Click(object sender, RoutedEventArgs e) => Browse(_noClip);

    private void ResetNoClip_Click(object sender, RoutedEventArgs e)
    {
        _noClip.Clear();
        _noClip.AddRange(new AppConfig().NoClipboardApps);
        Render();
        RaiseEdited();
    }
}
