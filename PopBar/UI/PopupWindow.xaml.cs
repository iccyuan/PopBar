using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using PopBar.Actions;
using PopBar.Config;
using PopBar.Core;
using static PopBar.Native.NativeMethods;

namespace PopBar.UI;

/// <summary>The floating bar. Never takes focus; positioned in physical pixels next to the mouse-up point.</summary>
public partial class PopupWindow : Window
{
    public event Action<ActionDefinition, Selection>? ActionClicked;
    internal event Action<RECT>? Shown;
    public event Action? Hidden;

    private IntPtr _hwnd;
    private Selection _selection = new("", IntPtr.Zero, 0, 0);
    private bool _visible;
    private string _theme = "";
    private int _lastX, _lastY;
    private bool _lastAbove;
    private double _lastScale = 1;

    public PopupWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
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
        _lastX = x; _lastY = y;
        ApplyTheme(cfg.Theme);
        BuildButtons(actions, cfg.ShowLabels);

        var (work, dpi) = GetMonitorAt(x, y);
        double scale = dpi / 96.0;
        _lastScale = scale;

        // First pass: measure with the bottom arrow to decide placement.
        ArrowTop.Visibility = Visibility.Collapsed;
        ArrowBottom.Visibility = Visibility.Visible;
        var size = MeasureRoot();
        bool above = y - size.Height * scale - 2 * scale >= work.Top;
        _lastAbove = above;
        if (!above)
        {
            ArrowTop.Visibility = Visibility.Visible;
            ArrowBottom.Visibility = Visibility.Collapsed;
            size = MeasureRoot();
        }

        int w = (int)Math.Ceiling(size.Width * scale);
        int h = (int)Math.Ceiling(size.Height * scale);
        int px = Math.Clamp(x - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        double margin = Root.Margin.Top; // DIP space reserved for the shadow
        int py = above
            ? (int)Math.Round(y - h + (margin - 8) * scale)
            : (int)Math.Round(y - (margin - 14) * scale);
        py = Math.Clamp(py, work.Top, Math.Max(work.Top, work.Bottom - h));

        // Point the arrow at the mouse-up x.
        double tipDip = (x - px) / scale - margin;
        double arrowLeft = Math.Clamp(tipDip - 9, 10, Math.Max(10, size.Width - 2 * margin - 28));
        ArrowTop.Margin = new Thickness(arrowLeft, 0, 0, -1);
        ArrowBottom.Margin = new Thickness(arrowLeft, -1, 0, 0);

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
                int py2 = above ? (int)Math.Round(y - h2 + (margin - 8) * scale) : py;
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
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
        var slide = new DoubleAnimation(above ? 6 : -6, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        Root.BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    public void HideNow()
    {
        if (!_visible) return;
        _visible = false;
        Root.BeginAnimation(OpacityProperty, null);
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Root.Opacity = 0;
        ShowWindow(_hwnd, SW_HIDE);
        Hidden?.Invoke();
    }

    // ------------------------------------------------------------------ content

    private void ApplyTheme(string theme)
    {
        if (theme == _theme) return;
        _theme = theme;
        bool light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        SetBrush("BarBrush", light ? "#F7FFFFFF" : "#F0262B36");
        SetBrush("BarBorderBrush", light ? "#24000000" : "#2AFFFFFF");
        SetBrush("BarFgBrush", light ? "#1F2937" : "#ECEFF4");
        SetBrush("BarHoverBrush", light ? "#14000000" : "#22FFFFFF");
        SetBrush("BarPressedBrush", light ? "#22000000" : "#3AFFFFFF");
        SetBrush("BarSepBrush", light ? "#16000000" : "#1AFFFFFF");
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
                    Width = 1, Margin = new Thickness(2, 7, 2, 7),
                    Fill = (Brush)Resources["BarSepBrush"],
                });
            }

            const double iconPx = 16;
            var canvas = new Canvas
            {
                Width = 24, Height = 24,
                LayoutTransform = new ScaleTransform(iconPx / 24, iconPx / 24),
            };
            canvas.Children.Add(new Path
            {
                Data = Icons.Get(a.Icon),
                Stroke = fg, StrokeThickness = 2,
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
                    Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
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
