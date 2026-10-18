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
    /// <summary>Streamed answer / reasoning, revealed smoothly instead of in bursts.</summary>
    private readonly StreamReveal _answer, _think;
    /// <summary>Where the answer shows: <see cref="ResultBox"/> for editable panels, else <see cref="StreamBox"/>, which fades text in.</summary>
    private readonly System.Windows.Controls.Primitives.TextBoxBase _view;

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
    public string Text => Dispatcher.CheckAccess() ? _answer.Text : Dispatcher.Invoke(() => _answer.Text);

    public PanelWindow(PanelOptions opt, string theme)
    {
        _opt = opt;
        InitializeComponent();
        if (opt.Editable) { ResultBox.IsReadOnly = false; _view = ResultBox; }
        else
        {
            ResultBox.Visibility = Visibility.Collapsed;
            StreamBox.Visibility = Visibility.Visible;
            _view = StreamBox;
        }
        _answer = new StreamReveal(_view, TextArea, NeedRoom, ReleaseRoom);
        _think = new StreamReveal(ThinkText, ThinkBody, NeedRoom, ReleaseRoom);
        Width = opt.Width;
        ApplyTheme(theme);
        TitleText.Text = opt.Title;
        ApplyBadge(opt.Badge);
        TitleIcon.Icon = opt.Icon;
        if (!string.IsNullOrWhiteSpace(opt.Source))
        {
            _sourceFull = opt.Source.Trim();
            _sourceToolTip = opt.SourceToolTip;
            ApplySource();
            SourceBox.Visibility = Visibility.Visible;
        }
        _view.MaxHeight = opt.MaxBodyHeight;
        TextArea.Visibility = opt.ShowTextArea ? Visibility.Visible : Visibility.Collapsed;
        if (opt.Pinned) { _pinned = true; PinIcon.Brush = (Brush)Resources["PAccent"]; }
        if (!opt.CloseOnOutsideClick) PinBtn.Visibility = Visibility.Collapsed;
        if (opt.Bare) MakeBare();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    /// <summary>
    /// Container-only mode: strip the chrome so the plugin's content is the whole panel. The content is clipped to the
    /// frame's corners (a Border's CornerRadius does not clip children), and empty spots of it drag the window since
    /// there is no title bar to grab.
    /// </summary>
    private void MakeBare()
    {
        TitleBar.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        SourceBox.Visibility = Visibility.Collapsed;
        TextArea.Visibility = Visibility.Collapsed;
        Body.Margin = new Thickness(0);
        var clip = new RectangleGeometry { RadiusX = 12, RadiusY = 12 };
        ShellBody.Clip = clip;
        ShellBody.SizeChanged += (_, e) => clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        Shell.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragPanel(); };
    }

    /// <summary>Gap (DIP) between the text kept clear and the panel.</summary>
    private const double TextGap = 8;
    /// <summary>Room (DIP) kept clear below the mouse point when no text area is known: the cursor arrow and the line under it.</summary>
    private const double PointClearance = 18;
    /// <summary>The panel's chrome (DIP) besides the body: title, footer, padding. With the body's cap, how tall a panel gets.</summary>
    private const double ChromeHeight = 140;

    /// <summary>Placed above the text: the card grows upwards from <see cref="_cardBottom"/> (physical px) instead of down over it.</summary>
    private bool _growUp;
    private int _cardBottom;

    /// <summary>
    /// Show the panel near the anchor point (physical pixels) without covering <paramref name="avoid"/>, the selected
    /// text: below it, lined up with its left edge, when a full-grown panel fits there; else above it, growing upwards;
    /// else on the roomier side. Without an area, the panel is centred under the point. Stays inside the work area.
    /// </summary>
    public void ShowAt(int anchorX, int anchorY, ScreenRect? avoid = null)
    {
        var (work, dpi) = GetMonitorAt(anchorX, anchorY);
        double scale = dpi / 96.0;
        int Px(double dip) => (int)Math.Round(dip * scale);
        int m = Px(Root.Margin.Left);
        // Measure first so the initial placement is right (DesiredSize includes the shadow margin).
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int w = (int)Math.Ceiling(Width * scale);
        int cardW = w - 2 * m;
        int cardH = (int)Math.Ceiling(Math.Max(DesiredSize.Height, 180) * scale) - 2 * m;
        int full = Math.Max(cardH, Px(_opt.MaxBodyHeight + ChromeHeight));   // how tall it may get as text streams in

        // what to keep clear, and where the panel's text column should start
        int clearTop, clearBottom, cardLeft;
        if (avoid is { } a)
        {
            clearTop = Math.Min(a.Top, anchorY);
            clearBottom = Math.Max(a.Bottom, anchorY + Px(PointClearance));   // nor the cursor arrow
            cardLeft = a.Left - Px(Body.Margin.Left);                           // the text column starts where the selection does
        }
        else
        {
            clearTop = anchorY - Px(PointClearance);
            clearBottom = anchorY + Px(PointClearance);
            cardLeft = anchorX - cardW / 2;
        }
        int gap = Px(TextGap);
        int below = work.Bottom - (clearBottom + gap), above = clearTop - gap - work.Top;
        _growUp = below < full && (above >= full || above > below);

        int cardTop = _growUp ? clearTop - gap - cardH : clearBottom + gap;
        _cardBottom = clearTop - gap;
        if (_growUp) Card.VerticalAlignment = VerticalAlignment.Bottom;   // spare room goes above it, see NeedRoom
        int x = Math.Clamp(cardLeft - m, work.Left - m, Math.Max(work.Left - m, work.Right - cardW - m));
        int y = Math.Clamp(cardTop - m, work.Top - m, Math.Max(work.Top - m, work.Bottom - cardH - m));
        _scale = scale;
        _work = work;
        CaptureMonitorGlass(work);
        Left = x / scale; Top = y / scale; // DIP relative to the primary monitor; corrected below with SetWindowPos
        Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        KeepOnScreen();   // the first layout ran before the move: settle the card against its real size now
        UpdateGlassViewbox();
        Activate();
        LocationChanged += (_, _) => UpdateGlassViewbox();
        Glass.SizeChanged += (_, _) => UpdateGlassViewbox();
        SizeChanged += (_, _) => KeepOnScreen();
        Card.SizeChanged += (_, _) => KeepOnScreen();   // a card growing upwards inside spare room moves without the window resizing
        LayoutUpdated += (_, _) => MeasureCard();       // for ContainsScreenPoint, called from the mouse hook's thread
    }

    /// <summary>The user moved the panel: a panel that grows upwards does so from where it was put down.</summary>
    private void DragPanel()
    {
        DragMove();
        if (_growUp && GetWindowRect(_hwnd, out var r)) _cardBottom = CardRect(r).Bottom;
    }

    // ---- source box 「显示全部 / 收起」 ----

    private const double SourceCollapsedHeight = 52;   // three lines of the 12px source text
    private const double SourceExpandedHeight = 240;   // beyond this the source scrolls
    private string _sourceFull = "";
    private bool _sourceCut, _sourceExpanded, _sourceToolTip;
    private RECT _work;

    /// <summary>
    /// Collapsed, the box shows the head and the tail of the source with the middle elided (the ends are what
    /// identifies a selection); expanded, all of it. The toggle appears only when something was cut.
    /// </summary>
    private const double SourceToggleGutter = 20;   // right margin the text leaves for the corner toggle

    private void ApplySource()
    {
        double width = Width - Root.Margin.Left - Root.Margin.Right - 32 - 20; // body margin + box padding
        var fitted = MiddleEllipsis(_sourceFull, width, SourceCollapsedHeight);
        _sourceCut = fitted.Length != _sourceFull.Length;
        // Only a cut text gets the toggle, and then it wraps in the narrower column so the icon never sits on a word.
        if (_sourceCut) fitted = MiddleEllipsis(_sourceFull, width - SourceToggleGutter, SourceCollapsedHeight);
        SourceScroll.Margin = new Thickness(0, 0, _sourceCut ? SourceToggleGutter : 0, 0);
        SourceText.Text = _sourceExpanded ? _sourceFull : fitted;
        SourceText.ToolTip = _sourceToolTip && _sourceCut && !_sourceExpanded ? _sourceFull : null;
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
            _view.MaxHeight = h;
            if (CustomHost.Content is ScrollViewer sv) sv.MaxHeight = h;
            else if (_opt.Bare) CustomHost.MaxHeight = h;
        }
        KeepOnScreen();
    });

    /// <summary>Extra transparent room reserved at a time, so the window is resized once per few lines, not per frame.</summary>
    private const double RoomStep = 120;

    /// <summary>
    /// The card is about to grow by <paramref name="more"/>: make sure the window already has room for it - below the
    /// card, or above it for a panel that grows upwards.
    /// </summary>
    private void NeedRoom(double more)
    {
        double need = Card.ActualHeight + more;
        if (_closed || need <= Root.MinHeight) return;
        // the spare room never reaches past the work area: it would only push the window around for nothing
        double avail = double.PositiveInfinity;
        if (_hwnd != IntPtr.Zero && _work.Bottom != 0 && GetWindowRect(_hwnd, out var r))
        {
            var card = CardRect(r);
            avail = (_growUp ? card.Bottom - _work.Top : _work.Bottom - card.Top) / _scale;
        }
        Root.MinHeight = Math.Max(need, Math.Min(need + RoomStep, avail));
    }

    /// <summary>
    /// Streaming settled: give back the spare room (it is transparent, so the window shrinking is not seen). Not for a
    /// panel growing upwards: its room is above the card, and the window shrinking from the bottom would show the card
    /// jumping up for a frame before <see cref="KeepOnScreen"/> moves it back down.
    /// </summary>
    private void ReleaseRoom()
    {
        if (!_growUp && !_answer.IsRunning && !_think.IsRunning) Root.MinHeight = 0;
    }

    /// <summary>
    /// The card in physical pixels, given the window rect: the window also holds the shadow margin and, while text
    /// streams in, spare room below the card (or above it, for a panel growing upwards).
    /// </summary>
    private RECT CardRect(RECT window)
    {
        // the mouse hook asks from its own thread, where the layout cannot be read: it gets the last one seen
        var l = Dispatcher.CheckAccess() ? MeasureCard() : _cardLayout;
        if (l == null) return window;
        int left = window.Left + (int)Math.Round(l.X * _scale), top = window.Top + (int)Math.Round(l.Y * _scale);
        return new RECT
        {
            Left = left, Top = top,
            Right = left + (int)Math.Round(l.Width * _scale), Bottom = top + (int)Math.Round(l.Height * _scale),
        };
    }

    /// <summary>The card's box inside the window (DIP) and the shadow margin around it.</summary>
    private sealed record CardLayout(double X, double Y, double Width, double Height, double Margin);
    private volatile CardLayout? _cardLayout;

    private CardLayout MeasureCard()
    {
        var at = Card.TranslatePoint(new Point(0, 0), this);
        return _cardLayout = new CardLayout(at.X, at.Y, Card.ActualWidth, Card.ActualHeight, Root.Margin.Left);
    }

    /// <summary>
    /// After a size change, move the window so the card is where it belongs: a panel growing upwards keeps its bottom
    /// edge (off the text); and every card stays inside the work area - overlapping the text only when it does not fit.
    /// </summary>
    private void KeepOnScreen()
    {
        if (_hwnd == IntPtr.Zero || _work.Bottom == 0 || !GetWindowRect(_hwnd, out var r)) return;
        var card = CardRect(r);
        int dx = 0, dy = _growUp ? _cardBottom - card.Bottom : 0;
        if (card.Bottom + dy > _work.Bottom) dy = _work.Bottom - card.Bottom;
        if (card.Top + dy < _work.Top) dy = _work.Top - card.Top;
        if (card.Right > _work.Right) dx = _work.Right - card.Right;
        if (card.Left + dx < _work.Left) dx = _work.Left - card.Left;
        if (dx == 0 && dy == 0) return;
        SetWindowPos(_hwnd, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
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
        var card = CardRect(r);
        double gx = (card.Left - _glassX) * GlassDownscale;
        double gy = (card.Top - _glassY) * GlassDownscale;
        double gw = Math.Max(1, Glass.ActualWidth * _scale * GlassDownscale);
        double gh = Math.Max(1, Glass.ActualHeight * _scale * GlassDownscale);
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
        // the card and its shadow margin, not the spare room above or below it
        var card = CardRect(r);
        int m = (int)Math.Round((_cardLayout?.Margin ?? 0) * _scale);
        return new RECT { Left = card.Left - m, Top = card.Top - m, Right = card.Right + m, Bottom = card.Bottom + m }.Contains(x, y);
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
    public void SetText(string text) => Ui(() => { _answer.Set(text); ShowTextArea(); if (text.Length > 0) EndThinking(); });
    public void AppendText(string text) => Ui(() => { _answer.Append(text); ShowTextArea(); if (text.Length > 0) EndThinking(); });

    public void AppendThinking(string text) => Ui(() =>
    {
        if (text.Length == 0) return;
        if (ThinkBox.Visibility != Visibility.Visible)
        {
            ThinkBox.Visibility = Visibility.Visible;
            _thinkStarted = DateTime.UtcNow;
            _thinking = true;
            SetThinkOpen(true);
        }
        _think.Append(text);
    });

    private bool _thinking, _thinkToggled;
    private DateTime _thinkStarted;

    /// <summary>The answer has started: stamp how long the model thought and fold the block, unless the user opened / closed it themselves.</summary>
    private void EndThinking()
    {
        if (!_thinking) return;
        _thinking = false;
        _think.Flush();
        var secs = Math.Max(1, (int)Math.Round((DateTime.UtcNow - _thinkStarted).TotalSeconds));
        ThinkHeader.Text = $"已思考 {secs} 秒";
        if (!_thinkToggled) SetThinkOpen(false);
    }

    private void SetThinkOpen(bool open)
    {
        if (!open) _think.Flush();
        ThinkBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ThinkChevron.Icon = open ? "chevron-down" : "chevron-right";
        ThinkToggle.ToolTip = open ? "收起思考过程" : "展开思考过程";
    }

    private void ThinkToggle_Click(object sender, RoutedEventArgs e)
    {
        _thinkToggled = true;
        SetThinkOpen(ThinkBody.Visibility != Visibility.Visible);
    }

    /// <summary>The text area hides while an error is the only content (no blank block above the red box) and returns with text.</summary>
    private void ShowTextArea()
    {
        if (_opt.ShowTextArea && CustomHost.Visibility != Visibility.Visible && TextArea.Visibility != Visibility.Visible && _answer.Text.Length > 0)
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
            EndThinking();
            StatusText.Text = "";
            if (_answer.Text.Length == 0 && TextArea.Visibility == Visibility.Visible) TextArea.Visibility = Visibility.Collapsed;
        }
        else ShowTextArea();
    });

    public void SetBusy(bool busy) => Ui(() => { SetBusyCore(busy); if (!busy) EndThinking(); });

    private bool _busy;

    /// <summary>Fade the indicator in / out and run the comet across the full track width.</summary>
    private void SetBusyCore(bool busy)
    {
        if (busy == _busy) return;
        _busy = busy;
        var fade = new DoubleAnimation(busy ? 1 : 0, TimeSpan.FromMilliseconds(busy ? 320 : 420)) { EasingFunction = new SineEase() };
        if (!busy) fade.Completed += (_, _) => { if (!_busy) StopFlow(); };
        BusyTrack.BeginAnimation(OpacityProperty, fade);
        if (busy) StartFlow();
    }

    /// <summary>
    /// Drift each repeating gradient by one full period at constant speed, forever, so the colours slide along the edge
    /// seamlessly; the halo goes the other way, slower, and breathes a little. The interference of the two makes the
    /// light swell and fade in places like iOS's glow, with nothing racing across or snapping back.
    /// </summary>
    private void StartFlow()
    {
        BusyX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 0, TimeSpan.FromSeconds(4.5)) { RepeatBehavior = RepeatBehavior.Forever });
        BusyGlowX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -1, TimeSpan.FromSeconds(7)) { RepeatBehavior = RepeatBehavior.Forever });
        BusyGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.32, 0.7, TimeSpan.FromSeconds(1.6))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void StopFlow()
    {
        BusyX.BeginAnimation(TranslateTransform.XProperty, null);
        BusyGlowX.BeginAnimation(TranslateTransform.XProperty, null);
        BusyGlow.BeginAnimation(OpacityProperty, null);
    }

    /// <summary>
    /// The panel grew (e.g. first layout, custom content): keep the clip on the frame's rounded corners.
    /// The clip is a tall rounded rect so only its top corners fall inside the 3px line.
    /// </summary>
    private void BusyTrack_SizeChanged(object sender, SizeChangedEventArgs e) => BusyClip.Rect = new Rect(0, 0, e.NewSize.Width, 24);

    public void AddButton(string label, Action<string> onClick, string? icon = null) => Ui(() =>
    {
        // Explicit TextBlock so the app-wide implicit TextBlock style cannot recolor the label.
        var b = new Button
        {
            Style = (Style)Resources["PFooterBtn"],
            Content = new TextBlock { Text = label, Foreground = (Brush)Resources["PFg"], FontSize = 12.5 },
        };
        b.Click += (_, _) => { try { onClick(_answer.Text); } catch (Exception ex) { Log.Error("panel button", ex); } };
        FooterButtons.Children.Add(b);
    });

    public new void Close()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        _answer.Flush();
        _think.Flush();
        try { base.Close(); } catch { }
        PanelClosed?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_closed) { _closed = true; _cts.Cancel(); PanelClosed?.Invoke(); }
        _answer.Flush();
        _think.Flush();
    }

    // ------------------------------------------------------------------ handlers

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragPanel();
    }

    public void SetContent(Func<FrameworkElement> factory) => Ui(() =>
    {
        try
        {
            var el = factory();
            CustomHost.Content = _opt.Bare
                ? el
                : new ScrollViewer { Content = el, MaxHeight = _opt.MaxBodyHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Style = (Style)FindResource("PBodyScroll") };
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
