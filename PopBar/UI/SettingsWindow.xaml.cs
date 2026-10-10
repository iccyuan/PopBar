using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PopBar.Actions;
using PopBar.Config;

namespace PopBar.UI;

/// <summary>Row view-model for the actions list.</summary>
public sealed class ActionRow : INotifyPropertyChanged
{
    public ActionDefinition Def { get; }
    public ActionRow(ActionDefinition def) => Def = def;

    public string Label => string.IsNullOrWhiteSpace(Def.Label) ? "(未命名)" : Def.Label;
    public Geometry IconGeometry => Icons.Get(Def.Icon);
    public bool Enabled
    {
        get => Def.Enabled;
        set { Def.Enabled = value; OnChanged(nameof(Enabled)); }
    }
    public string Summary
    {
        get
        {
            var desc = ActionTypes.Describe(Def.Type);
            var detail = Def.Type switch
            {
                ActionTypes.Url => Def.Url,
                ActionTypes.Transform => Def.Op + (string.IsNullOrEmpty(Def.Template) ? "" : "  " + Def.Template),
                ActionTypes.Command => Def.Command + " " + Def.Args,
                _ => null,
            };
            var cond = Def.Match switch
            {
                MatchKinds.Url => "  · 仅链接",
                MatchKinds.Email => "  · 仅邮箱",
                MatchKinds.Regex => "  · 正则 " + Def.Regex,
                _ => "",
            };
            return desc + (string.IsNullOrWhiteSpace(detail) ? "" : "  —  " + detail) + cond;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        OnChanged(nameof(Label)); OnChanged(nameof(IconGeometry)); OnChanged(nameof(Summary)); OnChanged(nameof(Enabled));
    }
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly ObservableCollection<ActionRow> _rows = new();
    private readonly DispatcherTimer _saveTimer;
    private bool _loading;

    public SettingsWindow(ConfigStore store)
    {
        _store = store;
        InitializeComponent();

        FallbackCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("ibeam", "文本光标 / 双击时（推荐）"),
            new("always", "总是"),
            new("off", "关闭"),
        };
        ThemeCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("dark", "深色"),
            new("light", "浅色"),
        };
        ActionsList.ItemsSource = _rows;

        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        var vs = "v" + (ver == null ? "0.1.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}");
        VersionText.Text = vs;
        AboutVersion.Text = vs + "  ·  .NET " + Environment.Version;
        ConfigPathRun.Text = ConfigStore.FilePath;
        LogPathRun.Text = System.IO.Path.Combine(ConfigStore.Dir, "popbar.log");

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        LoadFromConfig(_store.Current);
    }

    // ------------------------------------------------------------------ load / save

    private void LoadFromConfig(AppConfig c)
    {
        _loading = true;
        try
        {
            EnabledSw.IsChecked = c.Enabled;
            AutoStartSw.IsChecked = c.AutoStart;
            DragSw.IsChecked = c.TriggerOnDrag;
            DblSw.IsChecked = c.TriggerOnDoubleClick;
            ShiftSw.IsChecked = c.TriggerOnShiftClick;
            FallbackCombo.SelectedValue = c.ClipboardFallback;
            if (FallbackCombo.SelectedIndex < 0) FallbackCombo.SelectedIndex = 0;
            DelayBox.Text = c.ShowDelayMs.ToString();
            MaxLenBox.Text = c.MaxTextLength.ToString();
            ThemeCombo.SelectedValue = c.Theme;
            if (ThemeCombo.SelectedIndex < 0) ThemeCombo.SelectedIndex = 0;
            LabelsSw.IsChecked = c.ShowLabels;
            ExcludedBox.Text = string.Join(Environment.NewLine, c.ExcludedApps);
            NoClipBox.Text = string.Join(Environment.NewLine, c.NoClipboardApps);

            _rows.Clear();
            foreach (var a in c.Actions) _rows.Add(new ActionRow(a.Clone()));
        }
        finally { _loading = false; }
    }

    private AppConfig BuildConfig()
    {
        var c = new AppConfig
        {
            Enabled = EnabledSw.IsChecked == true,
            AutoStart = AutoStartSw.IsChecked == true,
            TriggerOnDrag = DragSw.IsChecked == true,
            TriggerOnDoubleClick = DblSw.IsChecked == true,
            TriggerOnShiftClick = ShiftSw.IsChecked == true,
            ClipboardFallback = FallbackCombo.SelectedValue as string ?? "ibeam",
            ShowDelayMs = ParseInt(DelayBox.Text, 60, 0, 2000),
            MaxTextLength = ParseInt(MaxLenBox.Text, 10000, 1, 1_000_000),
            Theme = ThemeCombo.SelectedValue as string ?? "dark",
            ShowLabels = LabelsSw.IsChecked == true,
            ExcludedApps = Lines(ExcludedBox.Text),
            NoClipboardApps = Lines(NoClipBox.Text),
            Actions = _rows.Select(r => r.Def.Clone()).ToList(),
        };
        return c;
    }

    private static int ParseInt(string s, int def, int min, int max) =>
        int.TryParse(s.Trim(), out var v) ? Math.Clamp(v, min, max) : def;

    private static List<string> Lines(string s) =>
        s.Split('\n').Select(l => l.Trim().TrimEnd('\r')).Where(l => l.Length > 0).ToList();

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
        StatusText.Text = "正在保存…";
    }

    private void SaveNow()
    {
        if (_loading) return;
        _store.Replace(BuildConfig());
        StatusText.Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>Called by the tray when the enabled flag flips elsewhere.</summary>
    public void RefreshFromStore()
    {
        var c = _store.Current;
        _loading = true;
        EnabledSw.IsChecked = c.Enabled;
        _loading = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ nav

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelGeneral == null) return;
        PanelGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelActions.Visibility = NavActions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ actions

    private ActionRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ActionRow;

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender); if (r == null) return;
        int i = _rows.IndexOf(r);
        if (i > 0) { _rows.Move(i, i - 1); Changed(sender, e); }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender); if (r == null) return;
        int i = _rows.IndexOf(r);
        if (i >= 0 && i < _rows.Count - 1) { _rows.Move(i, i + 1); Changed(sender, e); }
    }

    private void EditAction_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender); if (r == null) return;
        var dlg = new ActionEditWindow(r.Def.Clone()) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            int i = _rows.IndexOf(r);
            _rows[i] = new ActionRow(dlg.Result);
            Changed(sender, e);
        }
    }

    private void DeleteAction_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender); if (r == null) return;
        if (MessageBox.Show(this, $"删除动作“{r.Label}”？", "PopBar", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _rows.Remove(r);
        Changed(sender, e);
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ActionEditWindow(new ActionDefinition { Label = "新动作", Icon = "star" }) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            _rows.Add(new ActionRow(dlg.Result));
            Changed(sender, e);
        }
    }

    private void ResetActions_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "恢复默认动作列表？当前自定义动作会被移除。", "PopBar", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _rows.Clear();
        foreach (var a in DefaultActions.Create()) _rows.Add(new ActionRow(a));
        Changed(sender, e);
    }

    // ------------------------------------------------------------------ about

    private void OpenConfigDir_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ConfigStore.FilePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open config dir", ex); }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(System.IO.Path.Combine(ConfigStore.Dir, "popbar.log")) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open log", ex); }
    }
}
