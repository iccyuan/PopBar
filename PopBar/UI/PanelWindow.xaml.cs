using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    private bool _dark;
    private IntPtr _hwnd;

    /// <summary>Raised once when the panel goes away (distinct from Window.Closed, which is EventHandler-typed).</summary>
    public event Action? PanelClosed;
    event Action? IPanel.Closed { add => PanelClosed += value; remove => PanelClosed -= value; }
    public CancellationToken Cancellation => _cts.Token;
    public bool IsOpen => !_closed;
    public bool IsPinned => _pinned || !_opt.CloseOnOutsideClick;
    public bool Pinned
    {
        get => _pinned;
        set => Ui(() => { _pinned = value; PinIcon.Brush = _pinned ? (Brush)Resources["PAccent"] : (Brush)Resources["PMuted"]; });
    }
    public string Text => Dispatcher.CheckAccess() ? ResultBox.Text : Dispatcher.Invoke(() => ResultBox.Text);

    public PanelWindow(PanelOptions opt, string theme)
    {
        _opt = opt;
        InitializeComponent();
        Width = opt.Width;
        ApplyTheme(theme);
        TitleText.Text = opt.Title;
        ApplyBadge(opt.Badge);
        TitleIcon.Icon = opt.Icon;
        if (!string.IsNullOrWhiteSpace(opt.Source))
        {
            _sourceFull = opt.Source.Trim();
            ApplySource();
            SourceBox.Visibility = Visibility.Visible;
        }
        ResultBox.IsReadOnly = !opt.Editable;
        ResultBox.MaxHeight = opt.MaxBodyHeight;
        TextArea.Visibility = opt.ShowTextArea ? Visibility.Visible : Visibility.Collapsed;
        if (opt.Pinned) { _pinned = true; PinIcon.Brush = (Brush)Resources["PAccent"]; }
        if (!opt.CloseOnOutsideClick) PinBtn.Visibility = Visibility.Collapsed;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    /// <summary>Place the panel below the anchor point (physical pixels), clamped to the monitor work area.</summary>
    public void ShowAt(int anchorX, int anchorY)
    {
        var (work, dpi) = GetMonitorAt(anchorX, anchorY);
        double scale = dpi / 96.0;
        int m = (int)Math.Round(Root.Margin.Left * scale);
        // Measure first so the initial placement is right (DesiredSize includes the shadow margin).
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int w = (int)Math.Ceiling(Width * scale);
        int h = (int)Math.Ceiling(Math.Max(DesiredSize.Height, 180) * scale);
        int x = Math.Clamp(anchorX - w / 2, work.Left - m, Math.Max(work.Left - m, work.Right - w + m));
        int y = anchorY + (int)(18 * scale) - m;
        if (y + h - m > work.Bottom) y = Math.Max(work.Top - m, anchorY - h - (int)(18 * scale) + m);
        _scale = scale;
        _work = work;
        CaptureMonitorGlass(work);
        Left = x / scale; Top = y / scale; // DIP relative to the primary monitor; corrected below with SetWindowPos
        Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        UpdateGlassViewbox();
        Activate();
        LocationChanged += (_, _) => UpdateGlassViewbox();
        Glass.SizeChanged += (_, _) => UpdateGlassViewbox();
        SizeChanged += (_, _) => KeepOnScreen();
    }

    // ---- source box 「显示全部 / 收起」 ----

    private const double SourceCollapsedHeight = 52;   // three lines of the 12px source text
    private const double SourceExpandedHeight = 240;   // beyond this the source scrolls
    private string _sourceFull = "";
    private bool _sourceCut, _sourceExpanded;
    private RECT _work;

    /// <summary>
    /// Collapsed, the box shows the head and the tail of the source with the middle elided (the ends are what
    /// identifies a selection); expanded, all of it. The toggle appears only when something was cut.
    /// </summary>
    private void ApplySource()
    {
        double width = Width - Root.Margin.Left - Root.Margin.Right - 32 - 20; // body margin + box padding
        var fitted = MiddleEllipsis(_sourceFull, width, SourceCollapsedHeight);
        _sourceCut = fitted.Length != _sourceFull.Length;
        SourceText.Text = _sourceExpanded ? _sourceFull : fitted;
        SourceText.ToolTip = _sourceCut && !_sourceExpanded ? _sourceFull : null;
        SourceExpandBtn.Visibility = _sourceExpanded || _sourceCut ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SourceExpand_Click(object sender, RoutedEventArgs e)
    {
        _sourceExpanded = !_sourceExpanded;
        SourceText.MaxHeight = _sourceExpanded ? double.PositiveInfinity : SourceCollapsedHeight;
        SourceScroll.MaxHeight = _sourceExpanded ? SourceExpandedHeight : double.PositiveInfinity;
        SourceScroll.VerticalScrollBarVisibility = _sourceExpanded ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        SourceExpandBtn.ToolTip = _sourceExpanded ? "收起" : "显示全部";
        SourceExpandBtn.Tag = _sourceExpanded ? "chevrons-up" : "chevrons-down";
        ApplySource();
    }

    /// <summary>Keep as many characters as fit in <paramref name="maxHeight"/> at <paramref name="width"/>: 60% from the start, 40% from the end.</summary>
    private string MiddleEllipsis(string text, double width, double maxHeight)
    {
        if (Measure(text) <= maxHeight + 0.5) return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Measure(Compose(text, mid)) <= maxHeight + 0.5) lo = mid; else hi = mid - 1;
        }
        return Compose(text, lo);

        double Measure(string s) => new FormattedText(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(SourceText.FontFamily, SourceText.FontStyle, SourceText.FontWeight, SourceText.FontStretch),
            SourceText.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = width }.Height;

        static string Compose(string s, int keep)
        {
            int head = (int)Math.Ceiling(keep * 0.6), tail = keep - head;
            return s[..head].TrimEnd() + " … " + (tail > 0 ? s[^tail..].TrimStart() : "");
        }
    }

    public void SetSize(double? width = null, double? maxBodyHeight = null) => Ui(() =>
    {
        if (width is double w && w > 0 && Math.Abs(w - Width) > 0.5)
        {
            Width = w;
            if (SourceBox.Visibility == Visibility.Visible) ApplySource();
        }
        if (maxBodyHeight is double h && h > 0)
        {
            ResultBox.MaxHeight = h;
            if (CustomHost.Content is ScrollViewer sv) sv.MaxHeight = h;
        }
        KeepOnScreen();
    });

    /// <summary>After growing, slide the window so it stays inside the work area (the shadow margin may hang over).</summary>
    private void KeepOnScreen()
    {
        if (_hwnd == IntPtr.Zero || _work.Bottom == 0 || !GetWindowRect(_hwnd, out var r)) return;
        int m = (int)Math.Round(Root.Margin.Left * _scale);
        int x = r.Left, y = r.Top;
        if (r.Bottom - m > _work.Bottom) y = Math.Max(_work.Top - m, _work.Bottom - (r.Bottom - r.Top) + m);
        if (r.Right - m > _work.Right) x = Math.Max(_work.Left - m, _work.Right - (r.Right - r.Left) + m);
        if (x == r.Left && y == r.Top) return;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
    }

    private double _scale = 1;
    private BitmapSource? _glassImage;
    private int _glassX, _glassY;
    private const double GlassDownscale = 0.5;

    /// <summary>
    /// One blurred snapshot of the whole work area (half resolution) taken while the panel is still hidden.
    /// Dragging or growing the panel then only moves the brush viewbox - no re-capture, no flicker.
    /// </summary>
    private void CaptureMonitorGlass(RECT work)
    {
        _glassImage = null;
        if (ThemeManager.BlurOpacity >= 100) { Glass.Background = null; ApplyTint(false); return; }
        _glassImage = FrostedGlass.CaptureScaled(work.Left, work.Top, work.Width, work.Height, GlassDownscale, 18);
        _glassX = work.Left; _glassY = work.Top;
        if (_glassImage == null) { Glass.Background = null; ApplyTint(false); return; }
        ApplyTint(true);
    }

    /// <summary>Point the brush at the part of the snapshot that lies under the card right now.</summary>
    private void UpdateGlassViewbox()
    {
        if (_glassImage == null || _closed || _hwnd == IntPtr.Zero) return;
        GetWindowRect(_hwnd, out var r);
        int m = (int)Math.Round(Root.Margin.Left * _scale);
        double gx = (r.Left + m - _glassX) * GlassDownscale;
        double gy = (r.Top + m - _glassY) * GlassDownscale;
        double gw = Math.Max(1, (r.Width - 2 * m) * GlassDownscale);
        double gh = Math.Max(1, (r.Height - 2 * m) * GlassDownscale);
        var brush = new ImageBrush(_glassImage)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(gx, gy, gw, gh),
        };
        brush.Freeze();
        Glass.Background = brush;
    }

    private void ApplyTint(bool glass)
    {
        var tint = new SolidColorBrush(ThemeManager.BlurTint(_dark, glass));
        tint.Freeze();
        Resources["PBg"] = tint;
    }

    public bool ContainsScreenPoint(int x, int y)
    {
        // Hook coordinates are physical pixels; compare against the physical window rect (no DIP conversion).
        if (_closed || _hwnd == IntPtr.Zero || !IsVisible) return false;
        if (!GetWindowRect(_hwnd, out var r)) return false;
        return r.Contains(x, y);
    }

    private void ApplyTheme(string theme)
    {
        bool dark = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
        _dark = dark;
        Set("PBg", dark ? "#FF1F2430" : "#FFFFFFFF");
        Shadow.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#FF1E232D" : "#FFFFFFFF"));
        Set("PBorder", dark ? "#2EFFFFFF" : "#22000000");
        Set("PFg", dark ? "#FFECEFF4" : "#FF1F2937");
        Set("PMuted", dark ? "#FF9AA3B2" : "#FF6B7280");
        Set("PHover", dark ? "#1FFFFFFF" : "#14000000");
        Set("PSource", dark ? "#16FFFFFF" : "#10000000");
        Set("PAccent", dark ? "#FF5B9BFF" : "#FF3B82F6");
        if (BusyComet.Background is LinearGradientBrush g)
        {
            var accent = (Color)ColorConverter.ConvertFromString(dark ? "#FF5B9BFF" : "#FF3B82F6");
            var comet = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, accent.R, accent.G, accent.B), 0),
                new(accent, 0.45), new(accent, 0.7),
                new(Color.FromArgb(0, accent.R, accent.G, accent.B), 1),
            }, new Point(0, 0), new Point(1, 0));
            comet.Freeze();
            BusyComet.Background = comet;
        }
        Set("PErrBg", dark ? "#33F87171" : "#FFFEF2F2");
        Set("PErrFg", dark ? "#FFFCA5A5" : "#FFB91C1C");
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
    public void SetBadge(string? badge) => Ui(() => ApplyBadge(badge));

    private void ApplyBadge(string? badge)
    {
        BadgeText.Text = badge ?? "";
        BadgeBox.Visibility = string.IsNullOrWhiteSpace(badge) ? Visibility.Collapsed : Visibility.Visible;
        BadgeBox.ToolTip = badge;
    }
    public void SetText(string text) => Ui(() => { ResultBox.Text = text; ResultBox.ScrollToEnd(); ShowTextArea(); });
    public void AppendText(string text) => Ui(() => { ResultBox.AppendText(text); ResultBox.ScrollToEnd(); ShowTextArea(); });

    /// <summary>The text area hides while an error is the only content (no blank block above the red box) and returns with text.</summary>
    private void ShowTextArea()
    {
        if (_opt.ShowTextArea && CustomHost.Visibility != Visibility.Visible && TextArea.Visibility != Visibility.Visible && ResultBox.Text.Length > 0)
            TextArea.Visibility = Visibility.Visible;
    }
    public void SetStatus(string status) => Ui(() => StatusText.Text = status);
    public void SetError(string? message) => Ui(() =>
    {
        ErrorText.Text = message ?? "";
        bool has = !string.IsNullOrWhiteSpace(message);
        ErrorBox.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has)
        {
            SetBusyCore(false);
            StatusText.Text = "";
            if (ResultBox.Text.Length == 0 && TextArea.Visibility == Visibility.Visible) TextArea.Visibility = Visibility.Collapsed;
        }
        else ShowTextArea();
    });

    public void SetBusy(bool busy) => Ui(() => SetBusyCore(busy));

    private bool _busy;

    /// <summary>Fade the indicator in / out and run the comet across the full track width.</summary>
    private void SetBusyCore(bool busy)
    {
        if (busy == _busy) return;
        _busy = busy;
        var fade = new DoubleAnimation(busy ? 1 : 0, TimeSpan.FromMilliseconds(busy ? 160 : 260)) { EasingFunction = new SineEase() };
        if (!busy) fade.Completed += (_, _) => { if (!_busy) BusyX.BeginAnimation(TranslateTransform.XProperty, null); };
        BusyTrack.BeginAnimation(OpacityProperty, fade);
        if (busy) StartComet();
    }

    private void StartComet()
    {
        double w = BusyTrack.ActualWidth > 0 ? BusyTrack.ActualWidth : Math.Max(120, _opt.Width - 32);
        BusyComet.Width = Math.Clamp(w * 0.38, 80, 260);
        var run = new DoubleAnimation(-BusyComet.Width, w, TimeSpan.FromMilliseconds(1400))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            RepeatBehavior = RepeatBehavior.Forever,
        };
        BusyX.BeginAnimation(TranslateTransform.XProperty, run);
    }

    /// <summary>The panel grew (e.g. first layout, custom content): re-fit the sweep to the new width.</summary>
    private void BusyTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_busy && e.WidthChanged) StartComet();
    }

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

    public void SetContent(Func<FrameworkElement> factory) => Ui(() =>
    {
        try
        {
            var el = factory();
            CustomHost.Content = new ScrollViewer { Content = el, MaxHeight = _opt.MaxBodyHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Style = (Style)FindResource("PBodyScroll") };
            CustomHost.Visibility = Visibility.Visible;
            TextArea.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { Log.Error("panel SetContent", ex); }
    });

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        PinIcon.Brush = _pinned ? (Brush)Resources["PAccent"] : (Brush)Resources["PMuted"];
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ResultBox_TextChanged(object sender, TextChangedEventArgs e) { }
}
