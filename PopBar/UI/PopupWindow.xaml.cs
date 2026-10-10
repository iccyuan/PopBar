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
public partial class PopupWindow : Window
{
    public event Action<ActionDefinition, Selection>? ActionClicked;
    internal event Action<RECT>? Shown;
    public event Action? Hidden;

    private IntPtr _hwnd;
    private Selection _selection = new("", IntPtr.Zero, 0, 0);
    private bool _visible;
    private string _theme = "";

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
        BuildButtons(actions, cfg.ShowLabels);

        var (work, dpi) = GetMonitorAt(x, y);
        double scale = dpi / 96.0;
        var size = MeasureRoot();            // includes the 16 DIP shadow margin on every side
        double margin = Root.Margin.Left;
        int w = (int)Math.Ceiling(size.Width * scale);
        int h = (int)Math.Ceiling(size.Height * scale);
        int m = (int)Math.Round(margin * scale);
        int gap = (int)Math.Round(8 * scale);
        bool above = y - (h - m) - gap >= work.Top;
        int px = Math.Clamp(x - w / 2, work.Left - m, Math.Max(work.Left - m, work.Right - w + m));
        int py = above ? y - h + m - gap : y - m + (int)Math.Round(16 * scale);
        py = Math.Clamp(py, work.Top - m, Math.Max(work.Top - m, work.Bottom - h + m));

        // Frosted glass: blur what is on screen under the bar (window is still hidden / elsewhere).
        if (_hwnd != IntPtr.Zero && IsVisible) ShowWindow(_hwnd, SW_HIDE);
        Glass.Background = ThemeManager.BlurOpacity < 100 ? FrostedGlass.Brush(px + m, py + m, w - 2 * m, h - 2 * m) : null;
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
                int py2 = above ? y - h2 + m - gap : py;
                SetWindowPos(_hwnd, HWND_TOPMOST, px2, py2, w2, h2, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                Shown?.Invoke(new RECT { Left = px2 + m, Top = py2 + m, Right = px2 + w2 - m, Bottom = py2 + h2 - m });
            }
            Animate(above);
        }, DispatcherPriority.Loaded);
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
        SetBrush("BarSepBrush", light ? "#1A000000" : "#22FFFFFF");
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

    private void BuildButtons(IReadOnlyList<ActionDefinition> actions, bool showLabels)
    {
        Buttons.Children.Clear();
        var fg = (Brush)Resources["BarFgBrush"];
        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            if (i > 0)
            {
                Buttons.Children.Add(new Rectangle
                {
                    Width = 1, Margin = new Thickness(3, 9, 3, 9),
                    Fill = (Brush)Resources["BarSepBrush"],
                });
            }

            // 16 DIP = 24 px at 150 % DPI, i.e. the icon's native grid: pixel-crisp strokes.
            var icon = new IconImage { Icon = a.Icon, Size = 16, Brush = fg, Thickness = 1.9, VerticalAlignment = VerticalAlignment.Center };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            if (showLabels && !string.IsNullOrWhiteSpace(a.Label))
            {
                content.Children.Add(new TextBlock
                {
                    Text = a.Label, Foreground = fg, FontSize = 13,
                    FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                    Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var btn = new Button
            {
                Style = (Style)Resources["BarButton"],
                Content = content,
                Tag = a,
                ToolTip = showLabels ? null : a.Label,
            };
            btn.Click += OnButtonClick;
            Buttons.Children.Add(btn);
        }
    }

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ActionDefinition a }) return;
        var sel = _selection;
        HideNow();
        ActionClicked?.Invoke(a, sel);
    }
}
