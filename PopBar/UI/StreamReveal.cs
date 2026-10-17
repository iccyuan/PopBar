using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PopBar.UI;

/// <summary>
/// Smooths streamed text in a read-only text view. Chunks arrive in bursts; instead of dumping each one, the text is
/// held back a moment and then revealed a few characters at a time at a rate that grows with the backlog (so it never
/// falls far behind). In a <see cref="RichTextBox"/> each piece fades in from transparent, the way Codex / ChatGPT
/// write; in a plain <see cref="TextBox"/> (editable panels) it simply appears.
/// <para>
/// Because the text is held back, its height is known before it shows: the holder opens toward the height of
/// everything received on a spring (no jumps, no pulsing per line), so the text writes into space that is already
/// there. The view glides to the end once the text really scrolls, unless the user scrolled up. When everything has
/// settled the holder goes back to automatic height.
/// </para>
/// </summary>
internal sealed class StreamReveal
{
    /// <summary>Characters per second with nothing queued, plus this much per queued character.</summary>
    private const double BaseRate = 40, BacklogRate = 6;
    /// <summary>How long a burst waits before it starts showing, so the holder can open ahead of it.</summary>
    private const long HoldMs = 150;
    /// <summary>Spring stiffness of the height (critically damped: no overshoot).</summary>
    private const double Omega = 13;
    /// <summary>How fast the scroll position closes the gap (per second, exponential).</summary>
    private const double Ease = 14;
    /// <summary>At most one new piece this often: fewer, slightly larger pieces read as words rather than a flicker.</summary>
    private static readonly TimeSpan PieceEvery = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(420);

    private readonly TextBoxBase _box;
    private readonly FrameworkElement _host;
    private readonly Action<double>? _needRoom;
    private readonly Action? _idle;
    private readonly StringBuilder _pending = new();
    private double _credit;
    private TimeSpan _last, _lastPiece;
    private long _holdUntil;
    private bool _running, _follow = true;
    private double _lastOffset;
    private double _velocity;
    /// <summary>Text height of everything received (shown + pending), measured when text arrives; NaN = not known.</summary>
    private double _predicted = double.NaN;

    // RichTextBox mode: the shown text, the paragraph it lives in, one plain run of settled text and the pieces still fading
    private readonly StringBuilder _shown = new();
    private readonly Paragraph? _para;
    private Run? _settled;
    private readonly Queue<Run> _fading = new();

    /// <param name="box">The view the text goes into: a <see cref="RichTextBox"/> (fades) or a <see cref="TextBox"/>.</param>
    /// <param name="host">The element around it whose height is animated (it should clip its content).</param>
    /// <param name="needRoom">Told how much more the holder is about to grow, so the window can make room ahead of it.</param>
    /// <param name="idle">Told when the animation has settled.</param>
    public StreamReveal(TextBoxBase box, FrameworkElement host, Action<double>? needRoom = null, Action? idle = null)
    {
        _box = box;
        _host = host;
        _needRoom = needRoom;
        _idle = idle;
        if (box is RichTextBox rich)
        {
            _para = new Paragraph { Margin = new Thickness(0) };
            rich.Document = new FlowDocument(_para) { PagePadding = new Thickness(0) };
        }
    }

    /// <summary>Everything received, including what is still waiting to be shown.</summary>
    public string Text => (_para != null ? _shown.ToString() : ((TextBox)_box).Text) + _pending;

    public bool IsRunning => _running;

    public void Append(string text)
    {
        if (text.Length == 0) return;
        if (!_running) _follow = AtEnd();
        if (_pending.Length == 0) _holdUntil = Environment.TickCount64 + HoldMs;
        _pending.Append(text);
        _predicted = Predict(Text);
        Start();
    }

    /// <summary>Replace the text at once (a complete result); it fades in as one piece and the holder opens to fit.</summary>
    public void Set(string text)
    {
        _pending.Clear();
        if (_para != null)
        {
            _para.Inlines.Clear();
            _fading.Clear();
            _settled = null;
            _shown.Clear();
            if (text.Length > 0) Show(text);
        }
        else ((TextBox)_box).Text = text;
        _predicted = double.NaN;
        _follow = true;
        Start();
    }

    /// <summary>Show what is queued now and stop animating (the holder is about to fold, or the window closes).</summary>
    public void Flush()
    {
        if (_pending.Length > 0) { Show(_pending.ToString()); _pending.Clear(); }
        while (_fading.Count > 0) Settle(_fading.Dequeue());
        if (_follow) _box.ScrollToEnd();
        Stop();
    }

    private bool AtEnd() => _box.VerticalOffset >= _box.ExtentHeight - _box.ViewportHeight - 2;

    /// <summary>How tall <paramref name="text"/> lays out in the box (same font, the box's wrapping width).</summary>
    private double Predict(string text)
    {
        double width = _box.ViewportWidth > 0 ? _box.ViewportWidth : _box.ActualWidth - _box.Padding.Left - _box.Padding.Right;
        if (width <= 0) return double.NaN;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(_box.FontFamily, _box.FontStyle, _box.FontWeight, _box.FontStretch), _box.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(_box).PixelsPerDip) { MaxTextWidth = width };
        return ft.Height;
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        _last = TimeSpan.Zero;
        _velocity = 0;
        _lastOffset = _box.VerticalOffset;
        _host.Height = _host.ActualHeight;   // open from where it is (0 while it was hidden)
        CompositionTarget.Rendering += Tick;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        _credit = 0;
        _predicted = double.NaN;
        CompositionTarget.Rendering -= Tick;
        _host.Height = double.NaN;
        ScrollViewer.SetVerticalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        _idle?.Invoke();
    }

    /// <summary>Put a piece on screen: a run fading in from transparent, or plain text.</summary>
    private void Show(string piece)
    {
        if (_para == null) { _box.AppendText(piece); return; }
        _shown.Append(piece);
        var to = (_box.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
        var brush = new SolidColorBrush(Color.FromArgb(0, to.R, to.G, to.B));
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, FadeIn) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        var run = new Run(piece) { Foreground = brush };
        _para.Inlines.Add(run);
        _fading.Enqueue(run);
    }

    private static bool Faded(Run run) => run.Foreground is SolidColorBrush b && b.Color.A == 255;

    /// <summary>A piece that finished fading joins one plain run, so the document stays a handful of inlines.</summary>
    private void Settle(Run run)
    {
        if (_para == null) return;
        if (_settled == null)
        {
            run.ClearValue(TextElement.ForegroundProperty);   // inherit the box's colour (a null brush would draw nothing)
            _settled = run;
            return;
        }
        _settled.Text += run.Text;
        _para.Inlines.Remove(run);
    }

    private void Tick(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _last) return;   // Rendering can fire more than once per frame
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        bool settled = true;
        double expected = _box.VerticalOffset;

        // the user scrolled since the last frame: follow only while they are at the end
        if (Math.Abs(_box.VerticalOffset - _lastOffset) > 1) _follow = AtEnd();

        if (_pending.Length > 0)
        {
            settled = false;
            if (Environment.TickCount64 >= _holdUntil)
            {
                _credit += (BaseRate + BacklogRate * _pending.Length) * dt;
                int n = Math.Min((int)_credit, _pending.Length);
                if (n > 0 && (_para == null || now - _lastPiece >= PieceEvery))
                {
                    _credit -= n;
                    _lastPiece = now;
                    if (n < _pending.Length && char.IsHighSurrogate(_pending[n - 1])) n++;   // never split a surrogate pair
                    Show(_pending.ToString(0, n));
                    _pending.Remove(0, n);
                }
            }
        }
        else _credit = 0;

        // pieces fully opaque by now merge into the plain run (from the front only, so the order is kept)
        while (_fading.Count > 0 && Faded(_fading.Peek())) Settle(_fading.Dequeue());
        if (_fading.Count > 0) settled = false;

        if (_host.IsVisible)
        {
            double extra = _host is Border b ? b.Padding.Top + b.Padding.Bottom + b.BorderThickness.Top + b.BorderThickness.Bottom : 0;
            double pad = _box.Padding.Top + _box.Padding.Bottom;
            double shownHeight = _box.ExtentHeight + pad;
            // while text is still queued, open for all of it; once everything shows, the real layout is the truth
            double content = _pending.Length > 0 && !double.IsNaN(_predicted) ? Math.Max(shownHeight, _predicted + pad) : shownHeight;
            double target = Math.Clamp(content, _box.MinHeight, _box.MaxHeight) + _box.Margin.Top + _box.Margin.Bottom + extra;
            double h = double.IsNaN(_host.Height) ? _host.ActualHeight : _host.Height;
            // while the holder is still catching up the text overflows it for a moment: no scroll bar flashing for that
            ScrollViewer.SetVerticalScrollBarVisibility(_box, shownHeight > _box.MaxHeight ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
            if (target > h) _needRoom?.Invoke(target - h);
            if (Math.Abs(target - h) > 0.5 || Math.Abs(_velocity) > 4)
            {
                settled = false;
                // critically damped spring: follows a target that keeps moving without jumps or overshoot
                _velocity += (Omega * Omega * (target - h) - 2 * Omega * _velocity) * dt;
                _host.Height = Math.Max(0, h + _velocity * dt);
            }
            else { _host.Height = target; _velocity = 0; }

            // follow only text that really scrolls: while the holder is still opening toward a shorter text, scrolling
            // would lift the text and drop it back as the holder catches up
            if (_follow && shownHeight > _box.MaxHeight)
            {
                double end = Math.Max(0, _box.ExtentHeight - _box.ViewportHeight), off = _box.VerticalOffset;
                if (end - off > 0.5)
                {
                    settled = false;
                    off += (end - off) * Math.Min(1, dt * Ease);
                    if (end - off < 0.5) off = end;
                    _box.ScrollToVerticalOffset(off);
                    expected = off;   // applied at the next layout
                }
            }
        }
        _lastOffset = expected;

        if (settled) Stop();
    }
}
