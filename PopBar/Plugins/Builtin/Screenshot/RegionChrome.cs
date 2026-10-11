using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using PopBar.Sdk;

namespace PopBar.Plugins.Builtin.Screenshot;

/// <summary>
/// On-screen chrome for a live region session (GIF recording, scrolling capture): a click-through dashed frame just
/// outside the region and a small control bar beside it. Neither window takes focus and both stay out of the region,
/// so they never end up in the captured pixels. UI thread only.
/// </summary>
internal sealed class RegionChrome
{
    private readonly IPluginHost _host;
    private readonly Window _frame;
    private readonly Window _bar;
    private readonly TextBlock _status = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0), MinWidth = 120 };
    private readonly Ellipse _dot = new() { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private Border? _pauseBtn;

    public event Action? Done;
    public event Action? Cancelled;
    public event Action? PauseToggled;

    public RegionChrome(IPluginHost host, Int32Rect r, string doneLabel, bool canPause)
    {
        _host = host;
        double s = ShotNative.ScaleAt(r.X, r.Y);
        var accent = (Brush?)Application.Current?.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;

        // Frame: 3px band just outside the region.
        const int pad = 3;
        _frame = Overlay(clickThrough: true);
        _frame.Width = (r.Width + 2 * pad) / s; _frame.Height = (r.Height + 2 * pad) / s;
        _frame.Content = new Rectangle { Stroke = accent, StrokeThickness = 2 / s * 1.0, StrokeDashArray = new DoubleCollection { 4, 3 }, SnapsToDevicePixels = true };
        Place(_frame, r.X - pad, r.Y - pad);

        // Control bar: below the region, else above, else inside the bottom edge.
        _bar = Overlay(clickThrough: false);
        _bar.SizeToContent = SizeToContent.WidthAndHeight;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        _dot.Visibility = canPause ? Visibility.Visible : Visibility.Collapsed;
        row.Children.Add(_status);
        if (canPause) { _pauseBtn = Btn("pause", "暂停 / 继续", () => PauseToggled?.Invoke()); row.Children.Add(_pauseBtn); }
        row.Children.Add(Btn("x", "取消", () => Cancelled?.Invoke()));
        row.Children.Add(Pill(doneLabel, () => Done?.Invoke()));
        _bar.Content = new Border
        {
            Margin = new Thickness(10), Padding = new Thickness(6), CornerRadius = new CornerRadius(12),
            Background = (Brush?)Application.Current?.TryFindResource("CardBrush") ?? Brushes.White,
            BorderBrush = (Brush?)Application.Current?.TryFindResource("BorderBrush") ?? Brushes.Gainsboro, BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.22 },
            Child = row,
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _bar.Loaded += (_, _) =>
        {
            var h = new WindowInteropHelper(_bar).Handle;
            ShotNative.GetWindowRect(h, out var br);
            int bw = br.R - br.L, bh = br.B - br.T;
            var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(r.X, r.Y, r.Width, r.Height)).WorkingArea;
            int x = Math.Clamp(r.X + r.Width - bw, screen.Left, screen.Right - bw);
            int y = r.Y + r.Height + pad;
            if (y + bh > screen.Bottom) y = r.Y - pad - bh;
            if (y < screen.Top) y = r.Y + r.Height - bh;
            ShotNative.SetWindowPos(h, ShotNative.HWND_TOPMOST, x, y, 0, 0, ShotNative.SWP_NOSIZE | ShotNative.SWP_NOACTIVATE);
        };
    }

    public void Show() { _frame.Show(); _bar.Show(); }

    public void Close() { _frame.Close(); _bar.Close(); }

    public void SetStatus(string text) => _status.Text = text;

    public void SetPaused(bool paused)
    {
        _dot.Opacity = paused ? 0.3 : 1;
        if (_pauseBtn != null) _pauseBtn.Child = _host.Ui.Icon(paused ? "circle" : "pause", 15, (Brush)_status.Foreground);
    }

    private static Window Overlay(bool clickThrough)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true, Title = "PopBar",
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        w.SourceInitialized += (_, _) => ShotNative.MakeOverlay(w, clickThrough);
        return w;
    }

    private static void Place(Window w, int x, int y) =>
        w.Loaded += (_, _) => ShotNative.SetWindowPos(new WindowInteropHelper(w).Handle, ShotNative.HWND_TOPMOST, x, y, 0, 0, ShotNative.SWP_NOSIZE | ShotNative.SWP_NOACTIVATE);

    private Border Btn(string icon, string tip, Action click)
    {
        var b = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(1, 0, 1, 0) };
        b.Child = _host.Ui.Icon(icon, 15, (Brush?)Application.Current?.TryFindResource("TextBrush") ?? Brushes.Black);
        var hover = (Brush?)Application.Current?.TryFindResource("HoverBrush") ?? Brushes.WhiteSmoke;
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    private Border Pill(string label, Action click)
    {
        var accent = (Brush?)Application.Current?.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
        var hover = (Brush?)Application.Current?.TryFindResource("AccentHoverBrush") ?? Brushes.RoyalBlue;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_host.Ui.Icon("check", 15, Brushes.White));
        row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(6, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Height = 32, CornerRadius = new CornerRadius(8), Background = accent, Padding = new Thickness(10, 0, 12, 0), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand, Child = row };
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = accent;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }
}
