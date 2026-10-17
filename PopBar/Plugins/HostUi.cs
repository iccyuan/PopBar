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
    private readonly ILlmService _llm;

    public HostUi(ILlmService llm) => _llm = llm;

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

    public FrameworkElement WindowPicker(Action<PickedWindow> onPicked, string label = "拾取窗口…")
    {
        var btn = new Button { Content = label, ToolTip = "点击后在屏幕上点选目标窗口" };
        btn.Click += async (_, _) =>
        {
            var w = new WindowPickWindow();
            w.Show();
            var r = await w.Result;
            if (r != null) onPicked(r);
        };
        return btn;
    }

    public FrameworkElement FileBrowse(string label, string filter, Action<string> onPicked)
    {
        var btn = new Button { Content = label };
        btn.Click += (_, _) => { if (PickFile(filter) is { } f) onPicked(f); };
        return btn;
    }

    public FrameworkElement FolderBrowse(string label, Func<string?> initial, Action<string> onPicked)
    {
        var btn = new Button { Content = label };
        btn.Click += (_, _) => { if (PickFolder(initial()) is { } f) onPicked(f); };
        return btn;
    }

    // Dialogs are owned by the active PopBar window (settings, a panel...) so they centre on it and stay in front.
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    public string? PickFile(string filter, string? title = null, string? initialDir = null)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, Title = title ?? "选择文件" };
        if (!string.IsNullOrWhiteSpace(initialDir)) dlg.InitialDirectory = initialDir;
        return Show(dlg, Owner) ? dlg.FileName : null;
    }

    public string? PickFolder(string? initialDir = null, string? title = null)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title ?? "选择文件夹" };
        if (!string.IsNullOrWhiteSpace(initialDir) && System.IO.Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir;
        return Show(dlg, Owner) ? dlg.FolderName : null;
    }

    public string? SaveFile(string filter, string defaultName, string? initialDir = null, Window? owner = null)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = defaultName, AddExtension = true };
        if (!string.IsNullOrWhiteSpace(initialDir))
        {
            try { System.IO.Directory.CreateDirectory(initialDir); } catch { }
            dlg.InitialDirectory = initialDir;
        }
        return Show(dlg, owner ?? Owner) ? dlg.FileName : null;
    }

    private static bool Show(Microsoft.Win32.CommonDialog dlg, Window? owner) => (owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == true;

    public bool Confirm(string message, string? title = null)
    {
        var owner = Owner;
        var r = owner != null
            ? MessageBox.Show(owner, message, title ?? "PopBar", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(message, title ?? "PopBar", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        return r == MessageBoxResult.OK;
    }

    public void Alert(string message, string? title = null)
    {
        var owner = Owner;
        if (owner != null) MessageBox.Show(owner, message, title ?? "PopBar", MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(message, title ?? "PopBar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public System.Windows.Media.Imaging.BitmapSource? BlurImage(System.Windows.Media.Imaging.BitmapSource source) => FrostedGlass.Blur(source);

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

    public FrameworkElement ProviderPicker(string? current, Action<string> onChanged, double width = 240)
    {
        // The configured services only, no separate "default" entry: an untouched picker shows the service-page default selected.
        ProviderItem Item(string id, string suffix = "") => new(id, _llm.ProviderNameFor(id) + suffix, ProviderItem.LogoOf(_llm.Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))));
        var items = _llm.ConfiguredProviderIds.Select(id => Item(id)).ToList();
        var cur = (current ?? "").Trim();
        if (cur.Length > 0 && !items.Any(i => string.Equals(i.Key, cur, StringComparison.OrdinalIgnoreCase)))
            items.Add(Item(cur, "（未配置）")); // keep a stale choice visible rather than silently dropping it
        if (items.Count == 0) items.Add(new ProviderItem("", "「服务」页尚未配置", ProviderItem.DefaultLogo));
        var combo = new ComboBox { Width = width, SelectedValuePath = nameof(ProviderItem.Key), ItemTemplate = ProviderItem.Template(), ItemsSource = items, ToolTip = "「服务」页里配置的服务商" };
        combo.SelectedValue = cur.Length > 0 ? cur : _llm.ProviderId;
        if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) => { if (combo.SelectedValue is string v && v != cur) onChanged(v); };
        return combo;
    }

    public FrameworkElement ModelPicker(string? providerId, string? current, Action<string> onChanged, double width = 240)
    {
        // No separate "default" entry: an untouched picker shows the service's own model; clearing the box goes back to it.
        var defaultModel = _llm.ModelNameFor(providerId, null);
        var combo = new ComboBox { Width = width, IsEditable = true, ToolTip = "输入模型名，或打开列表从服务商拉取；清空 = 该服务商在「服务」页的模型" };
        // Starts with the list fetched last time (plus the service's own model); opening the list asks the server again.
        var known = _llm.KnownModels(providerId).ToList();
        if (!string.IsNullOrWhiteSpace(defaultModel) && !known.Contains(defaultModel)) known.Insert(0, defaultModel);
        combo.ItemsSource = known;
        combo.Text = string.IsNullOrWhiteSpace(current) ? defaultModel : current;
        bool loaded = false;
        // Models come from the configured service the first time the list is opened; failures just leave the typed value.
        combo.DropDownOpened += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            try
            {
                var models = await _llm.ListModelsAsync(providerId);
                var typed = combo.Text;
                combo.ItemsSource = models.ToList();
                combo.Text = typed;
            }
            catch (Exception ex) { Log.Warn("model list: " + ex.Message); loaded = false; }
        };
        string Value() => combo.Text?.Trim() ?? "";
        // Showing the default at creation, or re-setting the same text, must not count as an edit.
        var last = string.IsNullOrWhiteSpace(current) ? defaultModel : current.Trim();
        void Emit() { var v = Value(); if (v == last) return; last = v; onChanged(v); }
        combo.SelectionChanged += (_, _) => combo.Dispatcher.InvokeAsync(Emit);
        combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (!combo.IsDropDownOpen) Emit(); }));
        // Cleared by hand = the service's model again, both on screen and in the stored value.
        combo.LostKeyboardFocus += (_, _) => { if (Value().Length == 0) { combo.Text = defaultModel; last = defaultModel; onChanged(""); } };
        return combo;
    }

    public bool EditDialog(string title, FrameworkElement content, double width = 540) => EditDialog(title, content, width, "保存");

    /// <summary><see cref="EditDialog(string, FrameworkElement, double)"/> with another label on the confirm button.</summary>
    internal bool EditDialog(string title, FrameworkElement content, double width, string okText)
    {
        var dlg = new EditDialog(title, content, width, okText) { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
        return dlg.ShowDialog() == true;
    }

    public IReadOnlyList<RunningApp> RunningApps() => Core.RunningApps.List();
}
