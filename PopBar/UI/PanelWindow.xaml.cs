using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PopBar.Core;
using PopBar.Sdk;
using static PopBar.Native.NativeMethods;

namespace PopBar.UI;

/// <summary>Floating result panel used by plugins (translation, AI, OCR...). Implements <see cref="IPanel"/>.</summary>
public partial class PanelWindow : Window, IPanel
{
    private readonly CancellationTokenSource _cts = new();
    private readonly PanelOptions _opt;
    private bool _pinned;
    private bool _closed;
    private IntPtr _hwnd;

    /// <summary>Raised once when the panel goes away (distinct from Window.Closed, which is EventHandler-typed).</summary>
    public event Action? PanelClosed;
    event Action? IPanel.Closed { add => PanelClosed += value; remove => PanelClosed -= value; }
    public CancellationToken Cancellation => _cts.Token;
    public bool IsOpen => !_closed;
    public bool IsPinned => _pinned;
    public string Text => Dispatcher.CheckAccess() ? ResultBox.Text : Dispatcher.Invoke(() => ResultBox.Text);

    public PanelWindow(PanelOptions opt, string theme)
    {
        _opt = opt;
        InitializeComponent();
        Width = opt.Width;
        ApplyTheme(theme);
        TitleText.Text = opt.Title;
        TitleIcon.Data = Icons.Get(opt.Icon);
        if (!string.IsNullOrWhiteSpace(opt.Source))
        {
            SourceText.Text = opt.Source.Trim();
            SourceBox.Visibility = Visibility.Visible;
        }
        ResultBox.IsReadOnly = !opt.Editable;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
            bool dark = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
            bool blur = Acrylic.Apply(this, BackdropKind.Acrylic, dark, roundCorners: true, tintArgb: dark ? 0xD0202530u : 0xD0FFFFFFu);
            // Translucent tint over the blur (opacity from settings), opaque card without it.
            var tint = new SolidColorBrush(ThemeManager.BlurTint(dark, blur));
            tint.Freeze();
            Resources["PBg"] = tint;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    /// <summary>Place the panel below the anchor point (physical pixels), clamped to the monitor work area.</summary>
    public void ShowAt(int anchorX, int anchorY)
    {
        var (work, dpi) = GetMonitorAt(anchorX, anchorY);
        double scale = dpi / 96.0;
        // Measure first so the initial placement is right.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int w = (int)Math.Ceiling(Width * scale);
        int h = (int)Math.Ceiling(Math.Max(DesiredSize.Height, 160) * scale);
        int x = Math.Clamp(anchorX - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        int y = anchorY + (int)(18 * scale);
        if (y + h > work.Bottom) y = Math.Max(work.Top, anchorY - h - (int)(18 * scale));
        Left = x / scale; Top = y / scale; // DIP relative to the primary monitor; corrected below with SetWindowPos
        Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        Activate();
    }

    public bool ContainsScreenPoint(int x, int y)
    {
        if (_closed || _hwnd == IntPtr.Zero) return false;
        var (_, dpi) = GetMonitorAt(x, y);
        double scale = dpi / 96.0;
        var p = PointFromScreen(new Point(x / scale, y / scale));
        return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
    }

    private void ApplyTheme(string theme)
    {
        bool dark = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
        Set("PBg", dark ? "#FF1F2430" : "#FFFFFFFF");
        Set("PBorder", dark ? "#2EFFFFFF" : "#22000000");
        Set("PFg", dark ? "#FFECEFF4" : "#FF1F2937");
        Set("PMuted", dark ? "#FF9AA3B2" : "#FF6B7280");
        Set("PHover", dark ? "#1FFFFFFF" : "#14000000");
        Set("PSource", dark ? "#16FFFFFF" : "#10000000");
        Set("PAccent", dark ? "#FF5B9BFF" : "#FF3B82F6");
    }

    private void Set(string key, string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        Resources[key] = b;
    }

    // ------------------------------------------------------------------ IPanel (marshalled)

    private void Ui(Action a)
    {
        if (_closed) return;
        if (Dispatcher.CheckAccess()) a(); else Dispatcher.InvokeAsync(a);
    }

    public void SetTitle(string title) => Ui(() => TitleText.Text = title);
    public void SetText(string text) => Ui(() => { ResultBox.Text = text; ResultBox.ScrollToEnd(); });
    public void AppendText(string text) => Ui(() => { ResultBox.AppendText(text); ResultBox.ScrollToEnd(); });
    public void SetStatus(string status) => Ui(() => StatusText.Text = status);
    public void SetBusy(bool busy) => Ui(() => Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed);

    public void AddButton(string label, Action<string> onClick, string? icon = null) => Ui(() =>
    {
        // Explicit TextBlock so the app-wide implicit TextBlock style cannot recolor the label.
        var b = new Button
        {
            Style = (Style)Resources["PFooterBtn"],
            Content = new TextBlock { Text = label, Foreground = (Brush)Resources["PFg"], FontSize = 12.5 },
        };
        b.Click += (_, _) => { try { onClick(ResultBox.Text); } catch (Exception ex) { Log.Error("panel button", ex); } };
        FooterButtons.Children.Add(b);
    });

    public new void Close()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        try { base.Close(); } catch { }
        PanelClosed?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_closed) { _closed = true; _cts.Cancel(); PanelClosed?.Invoke(); }
    }

    // ------------------------------------------------------------------ handlers

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        PinIcon.Stroke = _pinned ? (Brush)Resources["PAccent"] : (Brush)Resources["PMuted"];
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ResultBox_TextChanged(object sender, TextChangedEventArgs e) { }
}
