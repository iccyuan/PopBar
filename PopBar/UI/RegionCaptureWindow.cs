using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PopBar.Sdk;
using static PopBar.Native.NativeMethods;
using Drawing = System.Drawing;

namespace PopBar.UI;

/// <summary>Full-virtual-screen overlay: drag a rectangle, get PNG bytes of that region. Esc cancels.</summary>
internal sealed class RegionCaptureWindow : Window
{
    private readonly TaskCompletionSource<RegionCapture?> _tcs = new();
    private readonly Canvas _canvas = new();
    private readonly Rectangle _sel = new() { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(40, 30, 144, 255)), Visibility = Visibility.Collapsed };
    private readonly TextBlock _hint = new() { Text = "拖动框选要识别的区域，Esc 取消", Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(20) };
    private readonly Border _hintBg = new() { Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0) };
    private Point _start;
    private bool _dragging;
    private bool _finishing;
    // physical rect of the virtual screen
    private readonly int _vx, _vy, _vw, _vh;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int n);

    public Task<RegionCapture?> Result => _tcs.Task;

    public RegionCaptureWindow()
    {
        _vx = GetSystemMetrics(76); _vy = GetSystemMetrics(77); _vw = GetSystemMetrics(78); _vh = GetSystemMetrics(79); // SM_XVIRTUALSCREEN...
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        _hintBg.Child = _hint;
        _hint.Margin = new Thickness(0);
        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_hintBg);
        _canvas.Children.Add(_sel);
        Content = root;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Rough DIP placement so WPF does not fight us; the exact physical rect is applied with SetWindowPos.
        var (_, dpi) = GetMonitorAt(_vx, _vy);
        double s = dpi / 96.0;
        Left = _vx / s; Top = _vy / s; Width = _vw / s; Height = _vh / s;

        void Cover()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, _vx, _vy, _vw, _vh, SWP_SHOWWINDOW | SWP_NOACTIVATE);
        }
        SourceInitialized += (_, _) => Cover();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        bool wasActive = false;
        Activated += (_, _) => wasActive = true;
        // Only treat losing focus as "cancel" once we actually had it (a --run trigger may never activate us).
        Deactivated += (_, _) => { if (wasActive && !_dragging && !_finishing && !_tcs.Task.IsCompleted) Finish(null); };
        Loaded += (_, _) => { Cover(); ForceForeground(new WindowInteropHelper(this).Handle); Activate(); Focus(); };
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _dragging = true;
        _hintBg.Visibility = Visibility.Collapsed;
        _sel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sel, _start.X); Canvas.SetTop(_sel, _start.Y);
        _sel.Width = 0; _sel.Height = 0;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_canvas);
        Canvas.SetLeft(_sel, Math.Min(p.X, _start.X));
        Canvas.SetTop(_sel, Math.Min(p.Y, _start.Y));
        _sel.Width = Math.Abs(p.X - _start.X);
        _sel.Height = Math.Abs(p.Y - _start.Y);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var p = e.GetPosition(_canvas);
        // PointToScreen returns physical pixels under per-monitor DPI awareness.
        var a = _canvas.PointToScreen(_start);
        var b = _canvas.PointToScreen(p);
        int x = (int)Math.Round(Math.Min(a.X, b.X));
        int y = (int)Math.Round(Math.Min(a.Y, b.Y));
        int w = (int)Math.Round(Math.Abs(b.X - a.X));
        int h = (int)Math.Round(Math.Abs(b.Y - a.Y));
        if (w < 4 || h < 4) { Finish(null); return; }

        // Hide the overlay before grabbing pixels so the tint is not in the capture.
        _finishing = true;
        Hide();
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                using var bmp = new Drawing.Bitmap(w, h);
                using (var g = Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, bmp.Size);
                using var ms = new MemoryStream();
                bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
                Finish(new RegionCapture(ms.ToArray(), x, y, w, h));
            }
            catch (Exception ex) { Log.Error("region capture", ex); Finish(null); }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void Finish(RegionCapture? r)
    {
        _tcs.TrySetResult(r);
        try { Close(); } catch { }
    }
}
