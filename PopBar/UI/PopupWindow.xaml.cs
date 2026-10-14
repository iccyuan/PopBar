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
    private bool _showLabels, _above, _hasGroup;
    /// <summary>DIP between the bar and the selected text.</summary>
    private const double TextGap = 6;
    /// <summary>Half a text line (DIP), for estimating the text around the mouse when the app does not report it.</summary>
    private const double HalfLine = 12;
    /// <summary>DIP below the mouse hotspot the cursor arrow covers.</summary>
    private const double CursorHeight = 28;

    private int _anchorX, _barBottom, _py, _m;
    private double _scale = 1;
    private RECT _work;
    // Blurred strip of the whole work-area width at the bar's height: lets a wider (expanded) bar keep its glass
    // without re-capturing the screen while the bar itself is on it.
    private ImageSource? _strip;
    private int _stripX, _stripY;

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
        _strip = null;
        if (ThemeManager.BlurOpacity >= 100) Glass.Background = null;
        else if (_hasGroup)
        {
            // The bar may widen later: blur the full-width strip now, while nothing of ours is on screen.
            _stripX = work.Left; _stripY = py + m;
            _strip = FrostedGlass.Capture(_stripX, _stripY, work.Right - work.Left, h - 2 * m);
            Glass.Background = StripBrush(px + m, py + m, w - 2 * m, h - 2 * m);
        }
        else Glass.Background = FrostedGlass.Brush(px + m, py + m, w - 2 * m, h - 2 * m);
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
                if (_strip != null) Glass.Background = StripBrush(px2 + m, py2 + m, w2 - 2 * m, h2 - 2 * m);
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
        if (cfg.ShowLabels == _showLabels && cfg.MaxBarItems == _maxItems && actions.SequenceEqual(_actions)) return;
        _actions = actions;
        _showLabels = cfg.ShowLabels;
        _maxItems = cfg.MaxBarItems;
        BuildButtons();
        Relayout();
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

    private ImageBrush? StripBrush(int x, int y, int w, int h)
    {
        if (_strip == null) return null;
        var b = new ImageBrush(_strip)
        {
            Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(x - _stripX, y - _stripY, w, h),
        };
        b.Freeze();
        return b;
    }

    /// <summary>One top-level bar button: an action, or a folded plugin group.</summary>
    private sealed record Entry(string Icon, string Label, Action OnClick, bool More);

    private int _maxItems;

    /// <summary>
    /// The main page: every action, with each plugin group of two or more members folded into one button, and
    /// everything past <see cref="AppConfig.MaxBarItems"/> folded behind a "更多" button so the bar stays short.
    /// </summary>
    private void BuildButtons()
    {
        Buttons.Children.Clear();
        var entries = TopLevelEntries();
        _hasGroup = entries.Any(e => e.More);
        int limit = _maxItems;
        if (limit > 0 && entries.Count > limit)
        {
            var rest = entries.Skip(limit - 1).ToList();
            foreach (var e in entries.Take(limit - 1)) AddButton(e.Icon, e.Label, _showLabels, e.OnClick, e.More);
            AddButton("ellipsis", "更多", false, () => Overflow(rest), compact: true);
            _hasGroup = true; // the bar can widen later: keep the full-width glass strip
        }
        else foreach (var e in entries) AddButton(e.Icon, e.Label, _showLabels, e.OnClick, e.More);
    }

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
                entries.Add(new Entry(g.Icon, g.Label, () => Expand(list), true));
            }
            else entries.Add(new Entry(a.Icon, a.Label, () => Run(a), false));
        }
        return entries;
    }

    /// <summary>The buttons that did not fit, with a back button; groups among them still open their own list.</summary>
    private void Overflow(IReadOnlyList<Entry> rest)
    {
        Buttons.Children.Clear();
        AddButton("chevron-left", "返回", false, Collapse, compact: true);
        foreach (var e in rest) AddButton(e.Icon, e.Label, _showLabels, e.OnClick, e.More);
        Relayout();
    }

    /// <summary>Swap the bar to a group's members, with a back button. Labels always show: this is a chooser.</summary>
    private void Expand(IReadOnlyList<ActionDefinition> list)
    {
        Buttons.Children.Clear();
        AddButton("chevron-left", "返回", false, Collapse, compact: true);
        foreach (var a in list) AddButton(a.Icon, a.Label, true, () => Run(a));
        Relayout();
    }

    private void Collapse()
    {
        BuildButtons();
        Relayout();
    }

    /// <summary>Re-size the visible bar to its new content, staying centred on the original anchor.</summary>
    private void Relayout()
    {
        if (!_visible) return;
        var size = MeasureRoot();
        int m = _m;
        int w = (int)Math.Ceiling(size.Width * _scale);
        int h = (int)Math.Ceiling(size.Height * _scale);
        int px = Math.Clamp(_anchorX - w / 2, _work.Left - m, Math.Max(_work.Left - m, _work.Right - w + m));
        int py = _above ? _barBottom - h + m : _py;
        if (_strip != null) Glass.Background = StripBrush(px + m, py + m, w - 2 * m, h - 2 * m);
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Shown?.Invoke(new RECT { Left = px + m, Top = py + m, Right = px + w - m, Bottom = py + h - m });

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Buttons.BeginAnimation(OpacityProperty, fade);
    }

    private void AddButton(string iconName, string label, bool showLabel, Action onClick, bool more = false, bool compact = false)
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
