using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PopBar.Plugins.Builtin.Screenshot;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace PopBar.Plugins.Builtin;

/// <summary>Sticky notes on the desktop: pin the selection (or a blank note) as an editable card that survives restarts.</summary>
public sealed class NotesPlugin : IPopBarPlugin
{
    public sealed class Note
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Text { get; set; } = "";
        /// <summary>Top-left in physical screen pixels (stable across DPI changes).</summary>
        public int X { get; set; }
        public int Y { get; set; }
        /// <summary>Size in DIP.</summary>
        public double Width { get; set; } = 260;
        public double Height { get; set; } = 180;
        public int Color { get; set; }
        public bool Topmost { get; set; } = true;
        public double Opacity { get; set; } = 1;
    }

    public sealed class Settings
    {
        public List<Note> Notes { get; set; } = new();
        public string NewHotkey { get; set; } = "";
        public string ShowAllHotkey { get; set; } = "";
    }

    /// <summary>Paper colours (background, header) - fixed pastels that read the same in light and dark themes.</summary>
    internal static readonly (Color Paper, Color Bar)[] Palette =
    {
        (Color.FromRgb(0xFF, 0xF4, 0xB8), Color.FromRgb(0xFB, 0xE8, 0x8C)), // yellow
        (Color.FromRgb(0xDC, 0xF5, 0xDF), Color.FromRgb(0xC2, 0xEB, 0xC8)), // green
        (Color.FromRgb(0xDB, 0xEC, 0xFF), Color.FromRgb(0xBF, 0xDC, 0xFF)), // blue
        (Color.FromRgb(0xFF, 0xE0, 0xEC), Color.FromRgb(0xFF, 0xC9, 0xDD)), // pink
        (Color.FromRgb(0xEC, 0xE4, 0xFF), Color.FromRgb(0xDA, 0xCD, 0xFF)), // purple
        (Color.FromRgb(0xF1, 0xF2, 0xF4), Color.FromRgb(0xE2, 0xE4, 0xE8)), // grey
    };

    private IPluginHost _host = null!;
    private Settings _cfg = new();
    private readonly Dictionary<string, NoteWindow> _open = new();

    public string Id => "notes";
    public string Name => "便签";
    public string Description => "把选中的文字贴成桌面便签，可编辑、换颜色、调透明度，重启后自动恢复。";
    public Version Version => new(0, 1, 0);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
        host.AddTrayMenuItem("new", "新建便签", NewNote, "sticky-note");
        host.AddTrayMenuItem("show", "显示全部便签", () => host.Dispatcher.InvokeAsync(ShowAll), "eye");
        RegisterHotkeys();
        host.Dispatcher.InvokeAsync(() => { foreach (var n in _cfg.Notes.ToList()) Open(n, focus: false); });
    }

    public IReadOnlyList<PluginAction> GetActions() => new[]
    {
        new PluginAction
        {
            Id = "pin", Label = "便签", Icon = "sticky-note", Description = "把选中内容贴成桌面便签",
            EnabledByDefault = false,
            IsApplicable = ctx => ctx.Text.Trim().Length is > 0 and <= 20000,
            Execute = (ctx, _) =>
            {
                _host.Dispatcher.InvokeAsync(() => Open(Create(ctx.Text.Trim(), ctx.ScreenX + 12, ctx.ScreenY + 16), focus: false));
                return Task.CompletedTask;
            },
        },
    };

    private void NewNote() => _host.Dispatcher.InvokeAsync(() =>
    {
        var p = System.Windows.Forms.Cursor.Position;
        Open(Create("", p.X, p.Y), focus: true);
    });

    private void RegisterHotkeys()
    {
        _host.UnregisterHotkey("new");
        _host.UnregisterHotkey("show");
        if (!string.IsNullOrWhiteSpace(_cfg.NewHotkey) && !_host.RegisterHotkey("new", _cfg.NewHotkey, NewNote))
            _host.Log.Warn($"hotkey {_cfg.NewHotkey} unavailable");
        if (!string.IsNullOrWhiteSpace(_cfg.ShowAllHotkey) && !_host.RegisterHotkey("show", _cfg.ShowAllHotkey, () => _host.Dispatcher.InvokeAsync(ShowAll)))
            _host.Log.Warn($"hotkey {_cfg.ShowAllHotkey} unavailable");
    }

    private Note Create(string text, int x, int y)
    {
        var n = new Note { Text = text, X = x, Y = y, Color = _cfg.Notes.Count % Palette.Length };
        // Size to the text: wider for long lines, taller for many lines (DIP, capped).
        var lines = text.Split('\n');
        int longest = lines.Length == 0 ? 0 : lines.Max(l => l.Length);
        n.Width = Math.Clamp(60 + longest * 8.5, 220, 420);
        n.Height = Math.Clamp(70 + Math.Max(lines.Length, text.Length * 8.5 / n.Width) * 21, 140, 420);
        _cfg.Notes.Add(n);
        Save();
        return n;
    }

    private void Open(Note n, bool focus)
    {
        if (_open.TryGetValue(n.Id, out var existing)) { existing.Activate(); return; }
        var w = new NoteWindow(n, _host, Save, () => Delete(n));
        _open[n.Id] = w;
        w.Closed += (_, _) => _open.Remove(n.Id);
        w.Show();
        if (focus) w.FocusText();
    }

    private void ShowAll()
    {
        foreach (var n in _cfg.Notes) Open(n, focus: false);
        foreach (var w in _open.Values) { w.Topmost = true; w.Topmost = w.Note.Topmost; w.Activate(); }
    }

    private void Delete(Note n)
    {
        _cfg.Notes.RemoveAll(x => x.Id == n.Id);
        Save();
    }

    private void Save() => _host.Storage.Set(_cfg);

    public FrameworkElement? CreateSettingsView()
    {
        // Edit a draft; it only takes effect when the settings window is saved.
        var d = SettingsDraft.Clone(_cfg);
        void Changed() => _host.Settings.Changed();
        _host.Settings.OnApply(() =>
        {
            _cfg.NewHotkey = d.NewHotkey;
            _cfg.ShowAllHotkey = d.ShowAllHotkey;
            Save();
            RegisterHotkeys();
        });
        return Form.Stack(
            Form.Card(
                Form.Row("新建便签", _host.Ui.HotkeyBox(d.NewHotkey, v => { d.NewHotkey = v; Changed(); }), "全局快捷键，在鼠标位置新建空白便签"),
                Form.Row("显示全部便签", _host.Ui.HotkeyBox(d.ShowAllHotkey, v => { d.ShowAllHotkey = v; Changed(); }), "全局快捷键，把所有便签带到最前"),
                Form.Row("便签数量", new TextBlock { Text = _cfg.Notes.Count.ToString(), VerticalAlignment = VerticalAlignment.Center })),
            Form.Hint("选中文字后点操作条上的「便签」，或从托盘菜单「新建便签」。拖动顶部移动，右下角调整大小，Ctrl+滚轮调透明度；删除后不可恢复。"));
    }

    public void Dispose()
    {
        foreach (var w in _open.Values.ToList()) w.CloseQuietly();
    }
}

/// <summary>One sticky note: pastel card, drag bar with colour / pin / copy / delete, editable text, resize grip.</summary>
internal sealed class NoteWindow : Window
{
    private static readonly Brush Ink = Frozen(Color.FromRgb(0x1F, 0x29, 0x37));

    public NotesPlugin.Note Note { get; }
    private readonly IPluginHost _host;
    private readonly Action _save;
    private readonly Action _delete;
    private readonly Border _card = new() { CornerRadius = new CornerRadius(10), Margin = new Thickness(14) };
    private readonly Border _bar = new() { CornerRadius = new CornerRadius(10, 10, 0, 0), Height = 30 };
    private readonly TextBox _text = new();
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Border? _pinBtn;

    public NoteWindow(NotesPlugin.Note note, IPluginHost host, Action save, Action delete)
    {
        Note = note; _host = host; _save = save; _delete = delete;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        TextOptions.SetTextHintingMode(this, TextHintingMode.Animated); // smooth CJK, matching the rest of PopBar
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = note.Topmost;
        Title = "PopBar 便签";
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        double s = ShotNative.ScaleAt(note.X, note.Y);
        Left = note.X / s - 14; Top = note.Y / s - 14;
        Width = note.Width + 28; Height = note.Height + 28;

        _card.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.22 };
        _card.Opacity = note.Opacity;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        _bar.Child = BuildBar();
        _bar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) try { DragMove(); } catch { } };
        _bar.MouseWheel += OnBarWheel;
        grid.Children.Add(_bar);

        _text.Text = note.Text;
        _text.AcceptsReturn = true; _text.AcceptsTab = true;
        _text.TextWrapping = TextWrapping.Wrap;
        _text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _text.Background = Brushes.Transparent; _text.BorderThickness = new Thickness(0);
        _text.Foreground = Ink; _text.CaretBrush = Ink;
        _text.FontSize = 14; _text.Padding = new Thickness(10, 6, 10, 10);
        _text.Template = PlainTextTemplate();
        _text.TextChanged += (_, _) => { Note.Text = _text.Text; _saveTimer.Stop(); _saveTimer.Start(); };
        Grid.SetRow(_text, 1);
        grid.Children.Add(_text);

        var grip = new Thumb { Width = 16, Height = 16, Cursor = Cursors.SizeNWSE, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.5, Template = GripTemplate() };
        grip.DragDelta += (_, e) =>
        {
            Width = Math.Max(180 + 28, Width + e.HorizontalChange);
            Height = Math.Max(110 + 28, Height + e.VerticalChange);
        };
        grip.DragCompleted += (_, _) => { Note.Width = Width - 28; Note.Height = Height - 28; _save(); };
        Grid.SetRow(grip, 1);
        grid.Children.Add(grip);

        _card.Child = grid;
        Content = _card;
        ApplyColor();
        RenderOptions.SetClearTypeHint(_card, ClearTypeHint.Enabled);

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _save(); };
        SourceInitialized += (_, _) => ShotNative.MakeToolWindow(this);
        LocationChanged += (_, _) => RememberPosition();
        Closed += (_, _) => { if (_saveTimer.IsEnabled) { _saveTimer.Stop(); _save(); } };
    }

    public void FocusText() => Dispatcher.InvokeAsync(() => { Activate(); _text.Focus(); _text.CaretIndex = _text.Text.Length; });

    /// <summary>Close the window but keep the note (app shutdown); only the delete button removes it.</summary>
    public void CloseQuietly() => Close();

    private void RememberPosition()
    {
        if (!IsLoaded) return;
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero || !ShotNative.GetWindowRect(h, out var r)) return;
        double s = ShotNative.ScaleAt(r.L, r.T);
        int m = (int)Math.Round(14 * s);
        Note.X = r.L + m; Note.Y = r.T + m;
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private UIElement BuildBar()
    {
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 0, 4, 0) };
        var dot = BarButton(null, "切换颜色", () => { Note.Color = (Note.Color + 1) % NotesPlugin.Palette.Length; ApplyColor(); _save(); });
        dot.Child = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1.5), BorderBrush = Ink, Opacity = 0.55 };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);

        var close = BarButton("x", "删除便签", () => { _delete(); Close(); });
        var copy = BarButton("copy", "复制全部", () => _host.CopyToClipboard(_text.Text));
        _pinBtn = BarButton("pin", "置顶", () => { Note.Topmost = !Note.Topmost; Topmost = Note.Topmost; UpdatePin(); _save(); });
        foreach (var b in new[] { close, copy, _pinBtn }) { DockPanel.SetDock(b, Dock.Right); row.Children.Add(b); }
        UpdatePin();
        return row;
    }

    private void UpdatePin() { if (_pinBtn != null) _pinBtn.Opacity = Note.Topmost ? 0.9 : 0.35; }

    private Border BarButton(string? icon, string tip, Action click)
    {
        var b = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
        if (icon != null) b.Child = _host.Ui.Icon(icon, 13, Ink);
        var hover = Frozen(Color.FromArgb(0x22, 0, 0, 0));
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true; // no drag from buttons
        b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return b;
    }

    private void OnBarWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        Note.Opacity = Math.Clamp(Note.Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.3, 1);
        _card.Opacity = Note.Opacity;
        _saveTimer.Stop(); _saveTimer.Start();
        e.Handled = true;
    }

    private void ApplyColor()
    {
        var (paper, bar) = NotesPlugin.Palette[Math.Clamp(Note.Color, 0, NotesPlugin.Palette.Length - 1)];
        _card.Background = Frozen(paper);
        _bar.Background = Frozen(bar);
    }

    private static ControlTemplate PlainTextTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(ScrollViewer.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        t.VisualTree = sv;
        return t;
    }

    private static ControlTemplate GripTemplate()
    {
        // Three diagonal dots, the classic resize affordance.
        var t = new ControlTemplate(typeof(Thumb));
        var path = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        path.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M12 4 L4 12 M12 8 L8 12"));
        path.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Ink);
        path.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.4);
        path.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        path.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        var host = new FrameworkElementFactory(typeof(Grid));
        host.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        host.AppendChild(path);
        t.VisualTree = host;
        return t;
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
