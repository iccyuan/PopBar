using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PopBar.Actions;
using PopBar.Config;
using PopBar.Core;
using static PopBar.Native.NativeMethods;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace PopBar.UI;

/// <summary>The floating bar. Never takes focus; acrylic backdrop; positioned in physical pixels next to the mouse-up point.</summary>
public partial class PopupWindow : Window, IPopupPresenter
{
    public event Action<ActionDefinition, Selection>? ActionClicked;
    internal event Action<RECT>? Shown;
    public event Action? Hidden;
    event Action<RECT>? IPopupPresenter.Shown { add => Shown += value; remove => Shown -= value; }

    private IntPtr _hwnd;
    private Selection _selection = new("", IntPtr.Zero, 0, 0);
    private bool _visible;
    private string _theme = "";

    /// <summary>Resolves the fold group of an action (plugin-declared); null = never fold.</summary>
    internal Func<ActionDefinition, BarGroup?>? GroupOf { get; set; }

    // Layout of the current show, kept so the bar can re-size in place when a group expands / collapses.
    private IReadOnlyList<ActionDefinition> _actions = Array.Empty<ActionDefinition>();
    private bool _showLabels, _above;
    /// <summary>DIP between the bar and the selected text.</summary>
    private const double TextGap = 6;
    /// <summary>Half a text line (DIP), for estimating the text around the mouse when the app does not report it.</summary>
    private const double HalfLine = 12;
    /// <summary>DIP below the mouse hotspot the cursor arrow covers.</summary>
    private const double CursorHeight = 28;

    private int _anchorX, _barBottom, _py, _m;
    private double _scale = 1;
    private RECT _work;
    /// <summary>The bar button whose drop-down is open, if any.</summary>
    private Button? _menuAnchor;
    /// <summary>Whether the open drop-down hangs above the bar (only when there is no room below it).</summary>
    private bool _menuAbove;

    public PopupWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        ThemeManager.Changed += _ => Dispatcher.InvokeAsync(() => ApplyTheme(ThemeManager.Setting, force: true));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyTheme(ThemeManager.Setting, force: true);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>Create the HWND and warm up the render pipeline so the first real show is instant.</summary>
    public void Preload()
    {
        Left = -20000; Top = -20000;
        Show();
        // The accent policy only sticks reliably once the layered surface exists; re-apply after the first show.
        ApplyTheme(ThemeManager.Setting, force: true);
        Hide();
    }

    // ------------------------------------------------------------------ show / hide

    public void ShowFor(Selection sel, IReadOnlyList<ActionDefinition> actions, AppConfig cfg)
    {
        _selection = sel;
        int x = sel.X, y = sel.Y;
        ApplyTheme(cfg.Theme);
        _actions = actions;
        _showLabels = cfg.ShowLabels;
        _maxItems = cfg.MaxBarItems;
        _foldKey = FoldKey(cfg);
        BuildButtons();

        var (work, dpi) = GetMonitorAt(x, y);
        double scale = dpi / 96.0;
        var size = MeasureRoot();            // includes the 16 DIP shadow margin on every side
        double margin = Root.Margin.Left;
        int w = (int)Math.Ceiling(size.Width * scale);
        int h = (int)Math.Ceiling(size.Height * scale);
        int m = (int)Math.Round(margin * scale);
        int px = Math.Clamp(x - w / 2, work.Left - m, Math.Max(work.Left - m, work.Right - w + m));
        var (above, barTop) = PlaceVertically(sel, h - 2 * m, work, scale);
        int py = Math.Clamp(barTop - m, work.Top - m, Math.Max(work.Top - m, work.Bottom - h + m));

        _anchorX = x; _barBottom = barTop + h - 2 * m; _above = above; _py = py; _m = m; _scale = scale; _work = work;

        // Frosted glass: blur what is on screen under the bar (window is still hidden / elsewhere).
        if (_hwnd != IntPtr.Zero && IsVisible) ShowWindow(_hwnd, SW_HIDE);
        Glass.Background = ThemeManager.BlurOpacity >= 100 ? null : FrostedGlass.Brush(px + m, py + m, w - 2 * m, h - 2 * m);
        ApplyTint(Glass.Background != null);

        Root.Opacity = 0;
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _visible = true;
        Shown?.Invoke(new RECT { Left = px + m, Top = py + m, Right = px + w - m, Bottom = py + h - m });

        // If the window hopped to a monitor with a different DPI, WPF re-lays out; re-apply the final rect once.
        Dispatcher.InvokeAsync(() =>
        {
            if (!_visible) return;
            int w2 = (int)Math.Ceiling(Root.DesiredSize.Width * scale);
            int h2 = (int)Math.Ceiling(Root.DesiredSize.Height * scale);
            if (w2 != w || h2 != h)
            {
                int px2 = Math.Clamp(x - w2 / 2, work.Left - m, Math.Max(work.Left - m, work.Right - w2 + m));
                int py2 = above ? _barBottom - h2 + m : py;
                SetWindowPos(_hwnd, HWND_TOPMOST, px2, py2, w2, h2, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                Shown?.Invoke(new RECT { Left = px2 + m, Top = py2 + m, Right = px2 + w2 - m, Bottom = py2 + h2 - m });
            }
            Animate(above);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The bar's visible top edge (px) for a bar <paramref name="barH"/> tall, and whether it sits above the text: above the
    /// selected text (and the mouse), else below it (and the cursor arrow), else - a selection filling the screen - next to
    /// the mouse. The text is the reported <see cref="Selection.Bounds"/>, or estimated from the lines the mouse touched.
    /// </summary>
    private static (bool Above, int BarTop) PlaceVertically(Selection sel, int barH, RECT work, double scale)
    {
        int Px(double dip) => (int)Math.Round(dip * scale);
        int half = sel.LineHeight is { } lh ? (lh + 1) / 2 : Px(HalfLine);
        int textTop = sel.Bounds?.Top ?? sel.Top - half;
        int textBottom = sel.Bounds?.Bottom ?? sel.Bottom + half;
        int gap = Px(TextGap), cursor = sel.Y + Px(CursorHeight);

        int aboveBottom = Math.Min(textTop, sel.Y) - gap;
        if (aboveBottom - barH >= work.Top) return (true, aboveBottom - barH);
        int belowTop = Math.Max(textBottom + gap, cursor);
        if (belowTop + barH <= work.Bottom) return (false, belowTop);
        // No room around the whole selection: keep the line under the mouse readable instead.
        int mouseAbove = sel.Y - half - gap;
        return mouseAbove - barH >= work.Top ? (true, mouseAbove - barH) : (false, cursor);
    }

    public void UpdateFor(Selection sel, IReadOnlyList<ActionDefinition> actions, AppConfig cfg)
    {
        if (!_visible) { ShowFor(sel, actions, cfg); return; }
        _selection = sel;
        // Same buttons (the usual case): nothing on screen changes, the actions just act on the new text.
        if (cfg.ShowLabels == _showLabels && cfg.MaxBarItems == _maxItems && FoldKey(cfg) == _foldKey && actions.SequenceEqual(_actions)) return;
        _actions = actions;
        _showLabels = cfg.ShowLabels;
        _maxItems = cfg.MaxBarItems;
        _foldKey = FoldKey(cfg);
        BuildButtons();
        Relayout(fadeBar: true);
    }

    private Size MeasureRoot()
    {
        Root.InvalidateMeasure();
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Root.DesiredSize;
    }

    private void Animate(bool above)
    {
        // Short and subtle: a 90ms fade with a 3px rise. No zoom - scaling text made the entrance look wobbly.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.RenderTransformOrigin = new Point(0.5, above ? 1 : 0);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)) { EasingFunction = ease };
        var slide = new DoubleAnimation(above ? 3 : -3, 0, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease };
        Zoom.ScaleX = 1; Zoom.ScaleY = 1;
        Root.BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    public void HideNow()
    {
        if (!_visible) return;
        _visible = false;
        CloseMenu();
        Root.BeginAnimation(OpacityProperty, null);
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Root.Opacity = 0;
        ShowWindow(_hwnd, SW_HIDE);
        Hidden?.Invoke();
    }

    // ------------------------------------------------------------------ content

    private void ApplyTheme(string setting, bool force = false)
    {
        var theme = ThemeManager.Resolve(setting);
        if (theme == _theme && !force) return;
        _theme = theme;
        bool light = theme == "light";
        SetBrush("BarFgBrush", light ? "#1F2937" : "#F1F3F7");
        SetBrush("BarHoverBrush", light ? "#18000000" : "#2EFFFFFF");
        SetBrush("BarPressedBrush", light ? "#26000000" : "#45FFFFFF");
        SetBrush("BarSepBrush", light ? "#24000000" : "#30FFFFFF");
        Shadow.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#FFFFFFFF" : "#FF1E232D"));
        MenuShadow.Background = Shadow.Background;
        var menu = new SolidColorBrush(ThemeManager.BlurTint(!light, blurActive: false)); // opaque card: the list lies over the text
        menu.Freeze();
        Resources["MenuBrush"] = menu;
    }

    /// <summary>Tint layer over the glass snapshot: translucent when a snapshot exists, opaque otherwise.</summary>
    private void ApplyTint(bool glass)
    {
        var tint = new SolidColorBrush(ThemeManager.BlurTint(_theme == "dark", glass));
        tint.Freeze();
        Resources["BarBrush"] = tint;
    }

    private void SetBrush(string key, string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        Resources[key] = b;
    }

    /// <summary>One bar button or drop-down row: an action, or a folded group (<see cref="Members"/>) that opens a list of them.</summary>
    private sealed record Entry(string Icon, string Label, Action? Run, IReadOnlyList<ActionDefinition>? Members)
    {
        public bool More => Members != null;
    }

    private int _maxItems;
    /// <summary>Which plugins are folded (from config): a change regroups the same actions differently.</summary>
    private string _foldKey = "";
    private static string FoldKey(AppConfig cfg) => string.Join(",", cfg.FoldedPlugins);

    /// <summary>
    /// The bar: every action, with each plugin group of two or more members folded into one button, and everything
    /// past <see cref="AppConfig.MaxBarItems"/> folded behind a "更多" button so the bar stays short. Folded buttons
    /// open a drop-down (<see cref="ToggleMenu"/>) instead of acting; the bar itself never changes shape.
    /// </summary>
    private void BuildButtons()
    {
        CloseMenu();
        Buttons.Children.Clear();
        var entries = TopLevelEntries();
        int limit = _maxItems;
        if (limit > 0 && entries.Count > limit)
        {
            var rest = entries.Skip(limit - 1).ToList();
            foreach (var e in entries.Take(limit - 1)) AddEntry(e);
            Button more = null!;
            more = AddButton("ellipsis", "更多", false, () => ToggleMenu(more, rest), more: true, compact: true);
        }
        else foreach (var e in entries) AddEntry(e);
    }

    private void AddEntry(Entry e)
    {
        if (e.Members is { } members)
        {
            Button btn = null!;
            btn = AddButton(e.Icon, e.Label, _showLabels, () => ToggleMenu(btn, MemberEntries(members)), more: true);
        }
        else AddButton(e.Icon, e.Label, _showLabels, e.Run!);
    }

    private List<Entry> MemberEntries(IReadOnlyList<ActionDefinition> list) =>
        list.Select(a => new Entry(a.Icon, a.Label, () => Run(a), null)).ToList();

    private List<Entry> TopLevelEntries()
    {
        var groupOf = new Dictionary<ActionDefinition, BarGroup>();
        var members = new Dictionary<string, List<ActionDefinition>>();
        foreach (var a in _actions)
        {
            if (GroupOf?.Invoke(a) is not { } g) continue;
            groupOf[a] = g;
            if (!members.TryGetValue(g.Key, out var list)) members[g.Key] = list = new();
            list.Add(a);
        }

        var entries = new List<Entry>();
        var placed = new HashSet<string>();
        foreach (var a in _actions)
        {
            if (groupOf.TryGetValue(a, out var g) && members[g.Key].Count > 1)
            {
                // The folded button sits where the group's first member would be.
                if (!placed.Add(g.Key)) continue;
                var list = members[g.Key];
                entries.Add(new Entry(g.Icon, g.Label, null, list));
            }
            else entries.Add(new Entry(a.Icon, a.Label, () => Run(a), null));
        }
        return entries;
    }

    // ------------------------------------------------------------------ drop-down

    /// <summary>Open the drop-down with these rows at <paramref name="anchor"/>, or close it when it is that button's already.</summary>
    private void ToggleMenu(Button anchor, IReadOnlyList<Entry> entries)
    {
        if (_menuAnchor == anchor) { CloseMenu(); Relayout(); return; }
        _menuAnchor = anchor;
        FillMenu(entries);
        MenuBox.Margin = new Thickness(0); // measure the card alone: a stale margin from the last open would widen it
        MenuBox.Visibility = Visibility.Visible;
        MenuBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double menuW = MenuBox.DesiredSize.Width, menuH = MenuBox.DesiredSize.Height + MenuGap;

        // Below the bar by default; above only when the screen ends before the list would (and there is room above).
        int barH = (int)Math.Ceiling(BarBox.DesiredSize.Height * _scale), barTop = BarTopPx(barH);
        int menuHpx = (int)Math.Ceiling(menuH * _scale);
        _menuAbove = barTop + barH + menuHpx > _work.Bottom && barTop - menuHpx >= _work.Top;
        Grid.SetRow(MenuBox, _menuAbove ? 0 : 2);

        // Left edge on the button that opened it, kept within the bar's width rather than sticking out of its side.
        double left = anchor.TranslatePoint(new Point(0, 0), BarBox).X;
        double barW = BarBox.ActualWidth;
        if (left + menuW > barW) left = Math.Max(0, barW - menuW);
        MenuBox.Margin = _menuAbove ? new Thickness(left, 0, 0, MenuGap) : new Thickness(left, MenuGap, 0, 0);
        Relayout();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        MenuBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)) { EasingFunction = ease });
        MenuSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_menuAbove ? 4 : -4, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
    }

    /// <summary>DIP between the bar and its drop-down.</summary>
    private const double MenuGap = 6;

    /// <summary>Top edge (px) of the bar itself for a bar <paramref name="barH"/> px tall: it never moves while a list opens.</summary>
    private int BarTopPx(int barH) => _above ? _barBottom - barH : _py + _m;

    private void CloseMenu()
    {
        _menuAnchor = null;
        _menuAbove = false;
        MenuBox.BeginAnimation(OpacityProperty, null);
        MenuSlide.BeginAnimation(TranslateTransform.YProperty, null);
        MenuBox.Opacity = 0;
        MenuBox.Visibility = Visibility.Collapsed;
        MenuItems.Children.Clear();
    }

    /// <summary>The drop-down's rows. A group row (inside "更多") swaps in its members behind a back row.</summary>
    private void FillMenu(IReadOnlyList<Entry> entries)
    {
        MenuItems.Children.Clear();
        foreach (var e in entries)
        {
            var entry = e;
            AddMenuItem(entry.Icon, entry.Label, entry.More, () =>
            {
                if (entry.Members is { } members)
                {
                    var page = new List<Entry> { new("chevron-left", "返回", () => { FillMenu(entries); Relayout(); }, null) };
                    page.AddRange(MemberEntries(members));
                    FillMenu(page);
                    Relayout();
                }
                else entry.Run?.Invoke();
            });
        }
    }

    private void AddMenuItem(string iconName, string label, bool more, Action onClick)
    {
        var fg = (Brush)Resources["BarFgBrush"];
        var content = new DockPanel { LastChildFill = true };
        var icon = new IconImage { Icon = iconName, Size = 16, Brush = fg, Thickness = 1.9, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);
        if (more)
        {
            var caret = new IconImage { Icon = "chevron-right", Size = 10, Brush = fg, Thickness = 2, Opacity = 0.65, Margin = new Thickness(12, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(caret, Dock.Right);
            content.Children.Add(caret);
        }
        content.Children.Add(new TextBlock
        {
            Text = label, Foreground = fg, FontSize = 13,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        var btn = new Button { Style = (Style)Resources["MenuButton"], Content = content };
        btn.Click += (_, _) => onClick();
        MenuItems.Children.Add(btn);
    }

    /// <summary>Re-size the window to its content (bar plus any drop-down), the bar itself staying centred on the anchor.</summary>
    private void Relayout(bool fadeBar = false)
    {
        if (!_visible) return;
        var size = MeasureRoot();
        int m = _m;
        int w = (int)Math.Ceiling(size.Width * _scale);
        int h = (int)Math.Ceiling(size.Height * _scale);
        int barW = (int)Math.Ceiling(BarBox.DesiredSize.Width * _scale) + 2 * m;
        int barH = (int)Math.Ceiling(BarBox.DesiredSize.Height * _scale);
        int px = Math.Clamp(_anchorX - barW / 2, _work.Left - m, Math.Max(_work.Left - m, _work.Right - w + m));
        // The bar stays where it was shown; the window grows downwards for a list below it, upwards for one above.
        int barTop = BarTopPx(barH);
        int py = _menuAbove ? barTop + barH - h + m : barTop - m;
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Shown?.Invoke(new RECT { Left = px + m, Top = py + m, Right = px + w - m, Bottom = py + h - m });

        if (!fadeBar) return;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Buttons.BeginAnimation(OpacityProperty, fade);
    }

    private Button AddButton(string iconName, string label, bool showLabel, Action onClick, bool more = false, bool compact = false)
    {
        var fg = (Brush)Resources["BarFgBrush"];
        if (Buttons.Children.Count > 0)
        {
            // Hairline that fades out at both ends, so it reads as a soft gap rather than a drawn rule.
            var sep = ((SolidColorBrush)Resources["BarSepBrush"]).Color;
            var fade = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, sep.R, sep.G, sep.B), 0), new(sep, 0.35), new(sep, 0.65), new(Color.FromArgb(0, sep.R, sep.G, sep.B), 1),
            }, new Point(0, 0), new Point(0, 1));
            fade.Freeze();
            Buttons.Children.Add(new Rectangle { Width = 1, Margin = new Thickness(3, 7, 3, 7), Fill = fade, SnapsToDevicePixels = true });
        }

        // 16 DIP = 24 px at 150 % DPI, i.e. the icon's native grid: pixel-crisp strokes.
        var icon = new IconImage { Icon = iconName, Size = 16, Brush = fg, Thickness = 1.9, VerticalAlignment = VerticalAlignment.Center };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        if (showLabel && !string.IsNullOrWhiteSpace(label))
        {
            content.Children.Add(new TextBlock
            {
                Text = label, Foreground = fg, FontSize = 13,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        }
        if (more)
        {
            // Small caret: this button opens a list rather than acting.
            content.Children.Add(new IconImage
            {
                Icon = "chevron-down", Size = 10, Brush = fg, Thickness = 2, Opacity = 0.65,
                Margin = new Thickness(4, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var btn = new Button
        {
            Style = (Style)Resources["BarButton"],
            Content = content,
            ToolTip = showLabel ? null : label,
        };
        if (compact) btn.Padding = new Thickness(7, 0, 7, 0);
        btn.Click += (_, _) => onClick();
        // The hover plate has its own edges; the hairlines touching it step aside while it shows.
        btn.MouseEnter += (_, _) => FadeNeighbourSeparators(btn, 0);
        btn.MouseLeave += (_, _) => FadeNeighbourSeparators(btn, 1);
        Buttons.Children.Add(btn);
        return btn;
    }

    private void FadeNeighbourSeparators(Button btn, double to)
    {
        int i = Buttons.Children.IndexOf(btn);
        foreach (int j in new[] { i - 1, i + 1 })
        {
            if (j < 0 || j >= Buttons.Children.Count || Buttons.Children[j] is not Rectangle r) continue;
            r.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to == 0 ? 90 : 160)) { EasingFunction = new SineEase() });
        }
    }

    private void Run(ActionDefinition a)
    {
        var sel = _selection;
        HideNow();
        ActionClicked?.Invoke(a, sel);
    }
}
