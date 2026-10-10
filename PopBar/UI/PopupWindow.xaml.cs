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
        var size = MeasureRoot();
        int w = (int)Math.Ceiling(size.Width * scale);
        int h = (int)Math.Ceiling(size.Height * scale);
        int gap = (int)Math.Round(10 * scale);
        bool above = y - h - gap >= work.Top;
        int px = Math.Clamp(x - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        int py = above ? y - h - gap : y + (int)Math.Round(16 * scale);
        py = Math.Clamp(py, work.Top, Math.Max(work.Top, work.Bottom - h));

        Root.Opacity = 0;
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _visible = true;
        Shown?.Invoke(new RECT { Left = px, Top = py, Right = px + w, Bottom = py + h });

        // If the window hopped to a monitor with a different DPI, WPF re-lays out; re-apply the final rect once.
        Dispatcher.InvokeAsync(() =>
        {
            if (!_visible) return;
            int w2 = (int)Math.Ceiling(Root.DesiredSize.Width * scale);
            int h2 = (int)Math.Ceiling(Root.DesiredSize.Height * scale);
            if (w2 != w || h2 != h)
            {
                int px2 = Math.Clamp(x - w2 / 2, work.Left, Math.Max(work.Left, work.Right - w2));
                int py2 = above ? y - h2 - gap : py;
                SetWindowPos(_hwnd, HWND_TOPMOST, px2, py2, w2, h2, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                Shown?.Invoke(new RECT { Left = px2, Top = py2, Right = px2 + w2, Bottom = py2 + h2 });
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
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.RenderTransformOrigin = new Point(0.5, above ? 1 : 0);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
        var slide = new DoubleAnimation(above ? 5 : -5, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
        var zoom = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
        Root.BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.YProperty, slide);
        Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
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
        bool blur = _hwnd != IntPtr.Zero && Acrylic.Apply(this, BackdropKind.Acrylic, !light, roundCorners: true,
            tintArgb: light ? 0xB0FFFFFFu : 0xB0202530u);
        // With a blur backdrop the bar is a translucent tint; without one it must be opaque.
        SetBrush("BarBrush", blur ? (light ? "#8CFFFFFF" : "#8C1E222B") : (light ? "#FAFFFFFF" : "#FA262B36"));
        SetBrush("BarEdgeBrush", light ? "#33000000" : "#2EFFFFFF");
        SetBrush("BarFgBrush", light ? "#1F2937" : "#F1F3F7");
        SetBrush("BarHoverBrush", light ? "#18000000" : "#2EFFFFFF");
        SetBrush("BarPressedBrush", light ? "#26000000" : "#45FFFFFF");
        SetBrush("BarSepBrush", light ? "#1A000000" : "#22FFFFFF");
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

            const double iconPx = 17;
            var canvas = new Canvas
            {
                Width = 24, Height = 24,
                LayoutTransform = new ScaleTransform(iconPx / 24, iconPx / 24),
            };
            canvas.Children.Add(new Path
            {
                Data = Icons.Get(a.Icon),
                Stroke = fg, StrokeThickness = 1.9,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.None,
            });

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(canvas);
            if (showLabels && !string.IsNullOrWhiteSpace(a.Label))
            {
                content.Children.Add(new TextBlock
                {
                    Text = a.Label, Foreground = fg, FontSize = 12.5,
                    FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
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
