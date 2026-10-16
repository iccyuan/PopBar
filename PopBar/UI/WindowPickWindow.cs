using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PopBar.Sdk;
using static PopBar.Native.NativeMethods;

namespace PopBar.UI;

/// <summary>
/// Full-virtual-screen overlay for choosing a window: moving the mouse outlines the top-level window under it,
/// a click picks that window (reported as its program and window class), Esc cancels.
/// </summary>
internal sealed class WindowPickWindow : Window
{
    private readonly TaskCompletionSource<PickedWindow?> _tcs = new();
    private readonly Canvas _canvas = new();
    private readonly Rectangle _box = new() { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(36, 30, 144, 255)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock _label = new() { Foreground = Brushes.White, FontSize = 13 };
    private readonly Border _labelBg = new() { Background = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _hintBg = new() { Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0), IsHitTestVisible = false };
    private IntPtr _hover;
    private bool _finishing;
    // physical rect of the virtual screen
    private readonly int _vx, _vy, _vw, _vh;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int n);

    public Task<PickedWindow?> Result => _tcs.Task;

    public WindowPickWindow()
    {
        _vx = GetSystemMetrics(76); _vy = GetSystemMetrics(77); _vw = GetSystemMetrics(78); _vh = GetSystemMetrics(79); // SM_XVIRTUALSCREEN...
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        _hintBg.Child = new TextBlock { Text = "点击要选择的窗口，Esc 取消", Foreground = Brushes.White, FontSize = 14 };
        _labelBg.Child = _label;
        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_hintBg);
        _canvas.Children.Add(_box);
        _canvas.Children.Add(_labelBg);
        Content = root;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var (_, dpi) = GetMonitorAt(_vx, _vy);
        double s = dpi / 96.0;
        Left = _vx / s; Top = _vy / s; Width = _vw / s; Height = _vh / s;

        void Cover()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, _vx, _vy, _vw, _vh, SWP_SHOWWINDOW | SWP_NOACTIVATE);
        }
        SourceInitialized += (_, _) => Cover();
        MouseMove += (_, e) => Track(e.GetPosition(_canvas));
        MouseLeftButtonDown += (_, e) => { Track(e.GetPosition(_canvas)); Pick(); };
        MouseRightButtonDown += (_, _) => Finish(null);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        bool wasActive = false;
        Activated += (_, _) => wasActive = true;
        Deactivated += (_, _) => { if (wasActive && !_finishing && !_tcs.Task.IsCompleted) Finish(null); };
        Loaded += (_, _) => { Cover(); ForceForeground(new WindowInteropHelper(this).Handle); Activate(); Focus(); };
    }

    private void Track(Point p)
    {
        var sp = _canvas.PointToScreen(p); // physical pixels under per-monitor DPI awareness
        var h = WindowAt((int)Math.Round(sp.X), (int)Math.Round(sp.Y));
        if (h == _hover) return;
        _hover = h;
        if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) { _box.Visibility = _labelBg.Visibility = Visibility.Collapsed; return; }
        var a = _canvas.PointFromScreen(new Point(r.Left, r.Top));
        var b = _canvas.PointFromScreen(new Point(r.Right, r.Bottom));
        Canvas.SetLeft(_box, a.X); Canvas.SetTop(_box, a.Y);
        _box.Width = Math.Max(0, b.X - a.X); _box.Height = Math.Max(0, b.Y - a.Y);
        _box.Visibility = Visibility.Visible;
        var title = GetWindowTitle(h);
        _label.Text = title.Length > 0 ? $"{GetWindowProcessName(h)}  —  {title}" : GetWindowProcessName(h);
        Canvas.SetLeft(_labelBg, a.X + 8); Canvas.SetTop(_labelBg, Math.Max(0, a.Y - 34));
        _labelBg.Visibility = Visibility.Visible;
    }

    private void Pick()
    {
        var h = _hover;
        if (h == IntPtr.Zero) return;
        var path = GetWindowProcessPath(h);
        if (path.Length == 0) { Finish(null); return; } // elevated process we cannot inspect
        Finish(new PickedWindow(System.IO.Path.GetFileName(path), path, GetWindowTitle(h), GetWindowClass(h)));
    }

    /// <summary>Topmost on-screen window of another process containing the point, resolved to its owner chain root. Desktop and taskbar excluded.</summary>
    private static IntPtr WindowAt(int x, int y)
    {
        IntPtr hit = IntPtr.Zero;
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
            if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            if (!GetWindowRect(h, out var r) || !r.Contains(x, y)) return true;
            var cls = GetWindowClass(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            hit = h;
            return false;
        }, IntPtr.Zero);
        // A popup or tooltip stands for the window that owns it.
        for (var owner = hit == IntPtr.Zero ? IntPtr.Zero : PopBar.Native.NativeMethods.GetWindow(hit, GW_OWNER); owner != IntPtr.Zero; owner = PopBar.Native.NativeMethods.GetWindow(owner, GW_OWNER)) hit = owner;
        return hit;
    }

    private void Finish(PickedWindow? r)
    {
        _finishing = true;
        _tcs.TrySetResult(r);
        try { Close(); } catch { }
    }
}
