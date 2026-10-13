using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace PopBar.Plugins.Builtin.Screenshot;

/// <summary>
/// Snipaste-style capture: the screen is frozen, hovering highlights the window under the cursor, click takes it,
/// drag selects a region; then resize/move the selection, annotate, and copy / save / pin.
/// Everything here is in overlay DIP; the frozen bitmap is physical pixels (scale = DPI / 96).
/// </summary>
internal sealed class CaptureOverlay : Window
{
    private enum State { Idle, Selecting, Selected, Moving, Resizing, Drawing }
    [Flags] private enum Zone { None = 0, Inside = 1, N = 2, S = 4, W = 8, E = 16 }

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0x22, 0xC5, 0x5E),
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0x11, 0x18, 0x27), Colors.White,
    };
    private static readonly double[] Widths = { 2, 4, 7 };

    private readonly IPluginHost _host;
    private readonly ScreenshotPlugin.Settings _cfg;
    private readonly BitmapSource _screen;
    private readonly Int32Rect _vs;
    private readonly List<ShotTarget> _windows;
    /// <summary>Rects under the cursor from the smallest part out to the whole window; the wheel walks this list.</summary>
    private readonly List<Int32Rect> _hoverStack = new();
    private int _hoverLevel;

    private readonly Canvas _root = new();
    private readonly Image _bg = new();
    /// <summary>Blurred copy of <see cref="_screen"/> behind the toolbar and style bar; null = plain cards.</summary>
    private readonly BitmapSource? _glass;
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _border = new() { StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly List<Rectangle> _handles = new();
    private readonly Border _sizeTag = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x18, 0x27)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };
    private readonly AnnotationLayer _layer = new();
    private readonly Border _toolbar = new();
    private readonly Border _styleBar = new();
    private readonly Border _magnifier = new();
    // Magnifier: MagColumns x MagRows source pixels (odd, so the cursor has a middle cell), each MagCell DIP square.
    private const int MagColumns = 21, MagRows = 15;
    private const double MagCell = 8;
    private readonly Image _magImage = new() { Width = MagColumns * MagCell, Height = MagRows * MagCell, Stretch = Stretch.Fill };
    private readonly MagnifierGrid _magGrid = new(MagColumns, MagRows, MagCell);
    private const double MagRadius = 10;
    private static readonly FontFamily MagMono = new("Cascadia Mono, Consolas");
    private static readonly Brush MagMuted = Frozen(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
    private readonly Border _magSwatch = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Frozen(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)) };
    private readonly TextBlock _magHex = new() { Foreground = Brushes.White, FontFamily = MagMono, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
    private readonly TextBlock _magRgb = new() { Foreground = MagMuted, FontFamily = MagMono, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _magPos = new() { Foreground = MagMuted, FontFamily = MagMono, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<ShotTool, Button> _toolButtons = new();
    private readonly List<Border> _swatches = new();
    private readonly List<Border> _widthDots = new();

    private double _s = 1;              // DIP → physical
    private State _state = State.Idle;
    private Rect _sel = Rect.Empty;     // DIP, snapped to physical pixels
    private Rect? _hover;               // auto-detected window under cursor
    private Point _anchor;              // drag start
    private Rect _selAtDragStart;
    private Zone _zone;
    private ShotTool _tool = ShotTool.None;
    private Color _color = Palette[0];
    private double _width = Widths[1];
    private TextBox? _editor;
    private Point _editorPos;
    private Color _pixelColor;

    /// <summary>Raised when the user asks to pin the capture; args: image, physical screen rect.</summary>
    public event Action<BitmapSource, Int32Rect>? PinRequested;
    /// <summary>Raised for "long" (scrolling screenshot) or "gif" (recording); arg: physical screen rect of the selection.</summary>
    public event Action<string, Int32Rect>? LiveRequested;

    public CaptureOverlay(IPluginHost host, ScreenshotPlugin.Settings cfg, BitmapSource screen, Int32Rect vs, List<ShotTarget> windows)
    {
        _host = host; _cfg = cfg; _screen = screen; _vs = vs; _windows = windows;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Title = "PopBar Screenshot";
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _bg.Source = screen;
        _glass = host.Ui.BlurImage(screen); // before the bars are built: they pick their tint by whether it exists
        RenderOptions.SetBitmapScalingMode(_bg, BitmapScalingMode.NearestNeighbor);
        _root.Children.Add(_bg);
        _root.Children.Add(_dim);
        _root.Children.Add(_layer);
        _root.Children.Add(_border);
        for (int i = 0; i < 8; i++)
        {
            var h = new Rectangle { Width = 7, Height = 7, Fill = Brushes.White, StrokeThickness = 1.2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            _handles.Add(h);
            _root.Children.Add(h);
        }
        _sizeTag.Child = _sizeText;
        _root.Children.Add(_sizeTag);
        BuildToolbar();
        BuildStyleBar();
        BuildMagnifier();
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            ShotNative.MakeToolWindow(this);
            var hwnd = new WindowInteropHelper(this).Handle;
            ShotNative.SetWindowPos(hwnd, ShotNative.HWND_TOPMOST, vs.X, vs.Y, vs.Width, vs.Height, ShotNative.SWP_SHOWWINDOW);
        };
        Loaded += (_, _) =>
        {
            _s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _bg.Width = _vs.Width / _s;
            _bg.Height = _vs.Height / _s;
            _root.Width = _bg.Width; _root.Height = _bg.Height;
            _layer.Width = _bg.Width; _layer.Height = _bg.Height;
            var accent = (Brush?)TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
            _border.Stroke = accent;
            foreach (var h in _handles) h.Stroke = accent;
            Activate();
            ShotNative.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            Focus();
            UpdateVisuals();
            UpdateHover(Mouse.GetPosition(_root));
        };

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseWheel += OnHoverWheel;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += OnRightClick;
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (_editor == null && !_dialogOpen) { /* stay open; Snipaste keeps the overlay */ } };
    }

    // ------------------------------------------------------------------ geometry helpers

    private double Snap(double v) => Math.Round(v * _s) / _s;
    private Rect SnapRect(Rect r) { var x = Snap(r.X); var y = Snap(r.Y); return new Rect(x, y, Math.Max(0, Snap(r.Right) - x), Math.Max(0, Snap(r.Bottom) - y)); }
    private Rect ToDip(Int32Rect px) => new((px.X - _vs.X) / _s, (px.Y - _vs.Y) / _s, px.Width / _s, px.Height / _s);
    private Int32Rect ToPx(Rect dip) => new((int)Math.Round(dip.X * _s), (int)Math.Round(dip.Y * _s), Math.Max(1, (int)Math.Round(dip.Width * _s)), Math.Max(1, (int)Math.Round(dip.Height * _s)));
    private Rect Bounds => new(0, 0, _bg.Width, _bg.Height);

    private static Rect Norm(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private Zone HitZone(Point p)
    {
        if (_sel.IsEmpty) return Zone.None;
        const double g = 6;
        bool inX = p.X >= _sel.Left - g && p.X <= _sel.Right + g, inY = p.Y >= _sel.Top - g && p.Y <= _sel.Bottom + g;
        if (!inX || !inY) return Zone.None;
        Zone z = Zone.None;
        if (Math.Abs(p.Y - _sel.Top) <= g) z |= Zone.N;
        else if (Math.Abs(p.Y - _sel.Bottom) <= g) z |= Zone.S;
        if (Math.Abs(p.X - _sel.Left) <= g) z |= Zone.W;
        else if (Math.Abs(p.X - _sel.Right) <= g) z |= Zone.E;
        if (z == Zone.None && _sel.Contains(p)) z = Zone.Inside;
        return z;
    }

    private static Cursor CursorFor(Zone z) => z switch
    {
        Zone.N | Zone.W or Zone.S | Zone.E => Cursors.SizeNWSE,
        Zone.N | Zone.E or Zone.S | Zone.W => Cursors.SizeNESW,
        Zone.N or Zone.S => Cursors.SizeNS,
        Zone.W or Zone.E => Cursors.SizeWE,
        Zone.Inside => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    // ------------------------------------------------------------------ mouse

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_root);
        if (_editor != null) { CommitText(); if (_tool == ShotTool.Text) return; }
        if (IsOverChrome(e)) return;

        if (_state == State.Selected && e.ClickCount == 2 && _sel.Contains(p) && _tool == ShotTool.None) { Copy(); return; }

        switch (_state)
        {
            case State.Idle:
                _anchor = p;
                _state = State.Selecting;
                CaptureMouse();
                break;
            case State.Selected:
                if (_tool != ShotTool.None && _sel.Contains(p)) { BeginDraw(p); break; }
                _zone = HitZone(p);
                if (_zone == Zone.None) break;
                if (_zone == Zone.Inside && _tool != ShotTool.None) break;
                _anchor = p; _selAtDragStart = _sel;
                _state = _zone == Zone.Inside ? State.Moving : State.Resizing;
                CaptureMouse();
                break;
        }
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_root);
        switch (_state)
        {
            case State.Idle:
                UpdateHover(p);
                break;
            case State.Selecting:
                _sel = SnapRect(Rect.Intersect(Norm(_anchor, p), Bounds));
                break;
            case State.Moving:
            {
                var d = p - _anchor;
                double x = Math.Clamp(_selAtDragStart.X + d.X, 0, Bounds.Width - _selAtDragStart.Width);
                double y = Math.Clamp(_selAtDragStart.Y + d.Y, 0, Bounds.Height - _selAtDragStart.Height);
                _sel = SnapRect(new Rect(x, y, _selAtDragStart.Width, _selAtDragStart.Height));
                break;
            }
            case State.Resizing:
            {
                double l = _selAtDragStart.Left, t = _selAtDragStart.Top, r = _selAtDragStart.Right, b = _selAtDragStart.Bottom;
                if (_zone.HasFlag(Zone.W)) l = p.X;
                if (_zone.HasFlag(Zone.E)) r = p.X;
                if (_zone.HasFlag(Zone.N)) t = p.Y;
                if (_zone.HasFlag(Zone.S)) b = p.Y;
                _sel = SnapRect(Rect.Intersect(Norm(new Point(l, t), new Point(r, b)), Bounds));
                break;
            }
            case State.Drawing:
                ContinueDraw(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case State.Selected:
                Cursor = _tool != ShotTool.None && _sel.Contains(p) ? (_tool == ShotTool.Text ? Cursors.IBeam : Cursors.Cross) : CursorFor(HitZone(p));
                break;
        }
        UpdateMagnifier(p);
        UpdateVisuals();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_root);
        ReleaseMouseCapture();
        switch (_state)
        {
            case State.Selecting:
                if ((p - _anchor).Length < 4)
                    _sel = _hover is Rect h ? SnapRect(h) : Rect.Empty;
                _state = _sel.IsEmpty || _sel.Width < 2 || _sel.Height < 2 ? State.Idle : State.Selected;
                if (_state == State.Idle) _sel = Rect.Empty;
                break;
            case State.Moving:
            case State.Resizing:
                _state = State.Selected;
                break;
            case State.Drawing:
                EndDraw();
                _state = State.Selected;
                break;
        }
        UpdateMagnifier(p); // hides it once a selection exists
        UpdateVisuals();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_editor != null) { CommitText(); return; }
        if (_state == State.Selected && _layer.Items.Count == 0) { ResetSelection(); return; }
        Close();
    }

    private bool IsOverChrome(MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, _toolbar) || ReferenceEquals(d, _styleBar)) return true;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>
    /// The topmost window under the cursor, narrowed to the smallest of its parts there (a control, a taskbar button).
    /// The wheel then steps out to enclosing parts and the whole window, and back in; moving to another element
    /// starts again from the smallest.
    /// </summary>
    private void UpdateHover(Point p)
    {
        if (!_cfg.DetectWindows) { _hover = null; return; }
        int px = (int)(p.X * _s) + _vs.X, py = (int)(p.Y * _s) + _vs.Y;
        static bool Has(Int32Rect r, int x, int y) => x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;
        var window = _windows.FirstOrDefault(w => Has(w.Rect, px, py));
        if (window == null) { _hoverStack.Clear(); _hover = Bounds; return; }
        var stack = window.Parts.Where(r => Has(r, px, py)).Distinct()
            .OrderBy(r => (long)r.Width * r.Height).Append(window.Rect).ToList();
        if (!stack.SequenceEqual(_hoverStack))
        {
            // Keep the chosen level while the cursor stays inside the same element.
            var current = _hoverLevel < _hoverStack.Count ? _hoverStack[_hoverLevel] : (Int32Rect?)null;
            _hoverStack.Clear();
            _hoverStack.AddRange(stack);
            int same = current is { } c ? _hoverStack.IndexOf(c) : -1;
            _hoverLevel = same >= 0 && Has(_hoverStack[same], px, py) ? same : 0;
        }
        _hover = ToDip(_hoverStack[_hoverLevel]);
    }

    private void OnHoverWheel(object sender, MouseWheelEventArgs e)
    {
        if (_state != State.Idle || _hoverStack.Count < 2) return;
        int level = Math.Clamp(_hoverLevel + (e.Delta > 0 ? 1 : -1), 0, _hoverStack.Count - 1);
        if (level == _hoverLevel) return;
        _hoverLevel = level;
        _hover = ToDip(_hoverStack[level]);
        UpdateVisuals();
        e.Handled = true;
    }

    private void ResetSelection()
    {
        _sel = Rect.Empty;
        _layer.Items.Clear();
        _layer.Current = null;
        SetTool(ShotTool.None);
        _state = State.Idle;
        UpdateHover(Mouse.GetPosition(_root));
        UpdateVisuals();
    }

    // ------------------------------------------------------------------ drawing

    private void BeginDraw(Point p)
    {
        if (_tool == ShotTool.Text) { StartText(p); return; }
        _anchor = p;
        _layer.Current = _tool switch
        {
            ShotTool.Rect => new RectAnnot { Color = _color, Width = _width, R = new Rect(p, p) },
            ShotTool.Ellipse => new EllipseAnnot { Color = _color, Width = _width, R = new Rect(p, p) },
            ShotTool.Arrow => new ArrowAnnot { Color = _color, Width = _width, A = p, B = p },
            ShotTool.Pen => new PenAnnot { Color = _color, Width = _width, Points = { p } },
            ShotTool.Mosaic => new MosaicAnnot { Color = _color, Width = _width, R = new Rect(p, p) },
            _ => null,
        };
        _state = State.Drawing;
        CaptureMouse();
    }

    private void ContinueDraw(Point p, bool constrain)
    {
        p = new Point(Math.Clamp(p.X, _sel.Left, _sel.Right), Math.Clamp(p.Y, _sel.Top, _sel.Bottom));
        switch (_layer.Current)
        {
            case RectAnnot r: r.R = constrain ? Square(_anchor, p) : Norm(_anchor, p); break;
            case EllipseAnnot el: el.R = constrain ? Square(_anchor, p) : Norm(_anchor, p); break;
            case ArrowAnnot a: a.B = p; break;
            case PenAnnot pen: pen.Points.Add(p); break;
            case MosaicAnnot m: m.R = Norm(_anchor, p); break;
        }
        _layer.InvalidateVisual();
    }

    private static Rect Square(Point a, Point b)
    {
        double d = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        return Norm(a, new Point(a.X + Math.Sign(b.X - a.X) * d, a.Y + Math.Sign(b.Y - a.Y) * d));
    }

    private void EndDraw()
    {
        var cur = _layer.Current;
        _layer.Current = null;
        if (cur == null) return;
        if (cur is MosaicAnnot m)
        {
            if (m.R.Width < 3 || m.R.Height < 3) { _layer.InvalidateVisual(); return; }
            var px = ToPx(m.R);
            px = new Int32Rect(px.X, px.Y, Math.Min(px.Width, _screen.PixelWidth - px.X), Math.Min(px.Height, _screen.PixelHeight - px.Y));
            m.Pixels = MosaicAnnot.Pixelate(_screen, px, Math.Max(6, (int)Math.Round(8 * _s)));
        }
        else
        {
            bool tooSmall = cur switch
            {
                RectAnnot r => r.R.Width < 2 || r.R.Height < 2,
                EllipseAnnot el => el.R.Width < 2 || el.R.Height < 2,
                ArrowAnnot a => (a.B - a.A).Length < 3,
                _ => false,
            };
            if (tooSmall) { _layer.InvalidateVisual(); return; }
        }
        _layer.Items.Add(cur);
        _layer.InvalidateVisual();
    }

    private void StartText(Point p)
    {
        _editorPos = p;
        _editor = new TextBox
        {
            MinWidth = 40, Background = Brushes.Transparent, BorderThickness = new Thickness(1), Padding = new Thickness(0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), Foreground = new SolidColorBrush(_color),
            CaretBrush = new SolidColorBrush(_color), FontSize = FontSizeFor(_width), FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            Template = MinimalTextBoxTemplate(),
        };
        Canvas.SetLeft(_editor, p.X);
        Canvas.SetTop(_editor, p.Y);
        _root.Children.Add(_editor);
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter || e.Key == Key.Escape) { CommitText(); e.Handled = true; }
        };
        Dispatcher.InvokeAsync(() => _editor?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static double FontSizeFor(double width) => width switch { <= 2 => 14, <= 4 => 18, _ => 26 };

    private static ControlTemplate MinimalTextBoxTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        border.AppendChild(host);
        t.VisualTree = border;
        return t;
    }

    private void CommitText()
    {
        if (_editor == null) return;
        var text = _editor.Text;
        _root.Children.Remove(_editor);
        var size = _editor.FontSize;
        _editor = null;
        if (!string.IsNullOrWhiteSpace(text))
            _layer.Items.Add(new TextAnnot { Color = _color, Width = _width, P = new Point(_editorPos.X + 3, _editorPos.Y + 1), Text = text, FontSize = size, PixelsPerDip = _s });
        _layer.InvalidateVisual();
        Focus();
    }

    private void Undo()
    {
        if (_editor != null) { _root.Children.Remove(_editor); _editor = null; return; }
        if (_layer.Items.Count > 0) { _layer.Items.RemoveAt(_layer.Items.Count - 1); _layer.InvalidateVisual(); }
        else if (_state == State.Selected) ResetSelection();
    }


    // ------------------------------------------------------------------ chrome (toolbar + style bar)

    private Brush Res(string key, Brush fallback) => (Brush?)TryFindResource(key) ?? fallback;
    private Brush TextFg => Res("TextBrush", Brushes.Black);
    private Brush Accent => Res("AccentBrush", Brushes.DodgerBlue);
    private Brush AccentStrong => Res("AccentHoverBrush", Brushes.RoyalBlue);
    private Brush AccentSoft => Res("AccentSoftBrush", Brushes.AliceBlue);
    private Brush Hover => Res("HoverBrush", Brushes.WhiteSmoke);

    private readonly Dictionary<ShotTool, string> _toolIcons = new();

    // Bar metrics: 30px buttons in 4px of padding make a 40px bar, the height of a compact Windows 11 command bar.
    private const double ButtonSize = 30, IconSize = 16, ChromePadding = 4, ChromeRadius = 10, PlateRadius = 7;

    /// <summary>Flat 36x36 icon button with a rounded hover plate. Active tools are tinted with the accent.</summary>
    private Button IconBtn(string icon, string tip, Action click, Brush? iconBrush = null)
    {
        var plate = new Border { CornerRadius = new CornerRadius(PlateRadius), Background = Brushes.Transparent, Width = ButtonSize, Height = ButtonSize, RenderTransformOrigin = new Point(0.5, 0.5) };
        var b = new Button
        {
            ToolTip = tip, Focusable = false, Cursor = Cursors.Hand,
            Template = PlainButtonTemplate(), Content = plate,
        };
        plate.Child = Centered(_host.Ui.Icon(icon, IconSize, iconBrush ?? TextFg));
        b.MouseEnter += (_, _) => { if (!IsActiveTool(b)) plate.Background = Hover; };
        b.MouseLeave += (_, _) => { if (!IsActiveTool(b)) plate.Background = Brushes.Transparent; plate.RenderTransform = null; };
        b.PreviewMouseLeftButtonDown += (_, _) => plate.RenderTransform = new ScaleTransform(0.92, 0.92);
        b.PreviewMouseLeftButtonUp += (_, _) => plate.RenderTransform = null;
        b.Click += (_, _) => click();
        return b;
    }

    private bool IsActiveTool(Button b) => _toolButtons.TryGetValue(_tool, out var a) && ReferenceEquals(a, b);

    private static FrameworkElement Centered(FrameworkElement e)
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        return e;
    }

    private static ControlTemplate PlainButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return t;
    }

    private void BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void Tool(ShotTool t, string icon, string tip)
        {
            _toolIcons[t] = icon;
            var b = IconBtn(icon, tip, () => SetTool(_tool == t ? ShotTool.None : t));
            _toolButtons[t] = b;
            panel.Children.Add(b);
        }
        Tool(ShotTool.Rect, "square", Shortcut.Tip("矩形（Shift 正方形）", _cfg.KeyRect));
        Tool(ShotTool.Ellipse, "circle", Shortcut.Tip("椭圆（Shift 正圆）", _cfg.KeyEllipse));
        Tool(ShotTool.Arrow, "move-up-right", Shortcut.Tip("箭头", _cfg.KeyArrow));
        Tool(ShotTool.Pen, "pen", Shortcut.Tip("画笔", _cfg.KeyPen));
        Tool(ShotTool.Mosaic, "grid", Shortcut.Tip("马赛克", _cfg.KeyMosaic));
        Tool(ShotTool.Text, "type", Shortcut.Tip("文字", _cfg.KeyText));
        panel.Children.Add(Separator());
        panel.Children.Add(IconBtn("undo", Shortcut.Tip("撤销", _cfg.KeyUndo), Undo));
        panel.Children.Add(Separator());
        panel.Children.Add(IconBtn("scroll-capture", Shortcut.Tip("长截图（自动滚动拼接）", _cfg.KeyLong), () => Live("long")));
        panel.Children.Add(IconBtn("film", Shortcut.Tip("录制 GIF", _cfg.KeyGif), () => Live("gif")));
        panel.Children.Add(Separator());
        panel.Children.Add(IconBtn("pin", Shortcut.Tip("贴到屏幕", _cfg.KeyPin), Pin));
        panel.Children.Add(IconBtn("download", Shortcut.Tip("保存", _cfg.KeySave), Save));
        panel.Children.Add(IconBtn("x", "取消  Esc", Close, new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))));

        // Primary action: accent pill with a label.
        var done = new Border { CornerRadius = new CornerRadius(PlateRadius), Background = Accent, Height = ButtonSize, Padding = new Thickness(10, 0, 12, 0), Margin = new Thickness(4, 0, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
        var doneRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        doneRow.Children.Add(Centered(_host.Ui.Icon("check", IconSize - 2, Brushes.White)));
        doneRow.Children.Add(new TextBlock { Text = "完成", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(5, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        done.Child = doneRow;
        var doneBtn = new Button { Template = PlainButtonTemplate(), Content = done, Focusable = false, Cursor = Cursors.Hand, ToolTip = "复制到剪贴板  " + string.Join(" / ", new[] { "Enter", Shortcut.Display(_cfg.KeyCopy), "双击" }.Where(s => s.Length > 0)) };
        doneBtn.MouseEnter += (_, _) => done.Background = AccentStrong;
        doneBtn.MouseLeave += (_, _) => { done.Background = Accent; done.RenderTransform = null; };
        doneBtn.PreviewMouseLeftButtonDown += (_, _) => done.RenderTransform = new ScaleTransform(0.96, 0.96);
        doneBtn.Click += (_, _) => Copy();
        panel.Children.Add(doneBtn);

        StyleChrome(_toolbar, panel);
        _root.Children.Add(_toolbar);
    }

    /// <summary>Hairline that fades out at both ends (same look as the selection bar and the menus).</summary>
    private UIElement Separator() => new Rectangle
    {
        Width = 1, Height = ButtonSize - 6, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
        Fill = Res("SeparatorBrush", Brushes.LightGray), OpacityMask = Res("FadeMaskV", Brushes.Black),
    };

    private void BuildStyleBar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var w in Widths)
        {
            double d = 4 + w * 1.4;
            var dot = new Border { Width = ButtonSize, Height = ButtonSize, CornerRadius = new CornerRadius(PlateRadius), Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = w, ToolTip = "粗细" };
            dot.Child = new Ellipse { Width = d, Height = d, Fill = TextFg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dot.MouseEnter += (_, _) => { if ((double)dot.Tag != _width) dot.Background = Hover; };
            dot.MouseLeave += (_, _) => { if ((double)dot.Tag != _width) dot.Background = Brushes.Transparent; };
            dot.MouseLeftButtonDown += (_, e) => { _width = (double)dot.Tag; RefreshStyleBar(); e.Handled = true; };
            _widthDots.Add(dot);
            panel.Children.Add(dot);
        }
        panel.Children.Add(Separator());
        foreach (var c in Palette)
        {
            // Outer ring shows selection; inner disc is the colour (hairline keeps white visible).
            var disc = new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(c), Stroke = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), StrokeThickness = 1 };
            var ring = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, Tag = c, Background = Brushes.Transparent, Child = Centered(disc) };
            ring.MouseLeftButtonDown += (_, e) => { _color = (Color)ring.Tag; RefreshStyleBar(); if (_editor != null) _editor.Foreground = new SolidColorBrush(_color); e.Handled = true; };
            _swatches.Add(ring);
            panel.Children.Add(ring);
        }
        StyleChrome(_styleBar, panel);
        _root.Children.Add(_styleBar);
        RefreshStyleBar();
    }

    /// <summary>
    /// Frosted bar: the outer border shows the blurred screenshot under it (see <see cref="PlaceGlass"/>), the inner one
    /// lays the popup tint over that and holds the buttons. Without a blurred copy it is a plain card.
    /// </summary>
    private void StyleChrome(Border b, UIElement child)
    {
        b.Child = new Border { CornerRadius = new CornerRadius(ChromeRadius - 1), Padding = new Thickness(ChromePadding), Child = child };
        // The selection bar's tint, not the thinner popup one: a toolbar stays up over any content and must read cleanly.
        ((Border)b.Child).SetResourceReference(Border.BackgroundProperty, _glass != null ? "BarTintBrush" : "CardBrush");
        b.CornerRadius = new CornerRadius(ChromeRadius);
        b.Background = Brushes.Transparent;
        b.BorderBrush = Res("BorderBrush", Brushes.Gainsboro);
        b.BorderThickness = new Thickness(1);
        b.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Direction = 270, Opacity = 0.18 };
        b.Visibility = Visibility.Collapsed;
        b.Cursor = Cursors.Arrow;
        RenderOptions.SetClearTypeHint(b, ClearTypeHint.Enabled);
    }

    /// <summary>Point a bar's background at the part of the blurred screenshot it covers.</summary>
    private void PlaceGlass(Border bar, double x, double y)
    {
        if (_glass == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var size = bar.DesiredSize;
        var brush = new ImageBrush(_glass)
        {
            Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = new Rect(x / Bounds.Width, y / Bounds.Height, size.Width / Bounds.Width, size.Height / Bounds.Height),
        };
        brush.Freeze();
        bar.Background = brush;
    }

    private void RefreshStyleBar()
    {
        foreach (var d in _widthDots)
        {
            bool on = (double)d.Tag == _width;
            d.Background = on ? AccentSoft : Brushes.Transparent;
            if (d.Child is Ellipse e) e.Fill = on ? AccentStrong : TextFg;
        }
        foreach (var s in _swatches) s.BorderBrush = (Color)s.Tag == _color ? Accent : Brushes.Transparent;
    }

    private void SetTool(ShotTool t)
    {
        if (_editor != null) CommitText();
        _tool = t;
        foreach (var (tool, b) in _toolButtons)
        {
            bool on = tool == t;
            var plate = (Border)b.Content;
            plate.Background = on ? AccentSoft : Brushes.Transparent;
            plate.Child = Centered(_host.Ui.Icon(_toolIcons[tool], 18, on ? AccentStrong : TextFg));
        }
        UpdateVisuals();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Dark card: rounded zoom with the pixel grid on top, then colour (swatch, hex, rgb) and position below.</summary>
    private void BuildMagnifier()
    {
        RenderOptions.SetBitmapScalingMode(_magImage, BitmapScalingMode.NearestNeighbor);
        double w = MagColumns * MagCell, h = MagRows * MagCell, inner = MagRadius - 4;
        var zoom = new Grid { Width = w, Height = h, Clip = new RectangleGeometry(new Rect(0, 0, w, h), inner, inner) };
        zoom.Children.Add(_magImage);
        zoom.Children.Add(_magGrid);
        zoom.Children.Add(new Border { CornerRadius = new CornerRadius(inner), BorderThickness = new Thickness(1), BorderBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) });

        var hint = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Background = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            Child = new TextBlock { Text = "C 复制", Foreground = MagMuted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
        };
        // Row 1: swatch + hex, the copy hint on the right. Row 2: rgb, the position on the right.
        var colour = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 8, 2, 0) };
        DockPanel.SetDock(hint, Dock.Right);
        colour.Children.Add(hint);
        colour.Children.Add(_magSwatch);
        colour.Children.Add(_magHex);
        var position = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 5, 2, 1) };
        DockPanel.SetDock(_magPos, Dock.Right);
        position.Children.Add(_magPos);
        position.Children.Add(_magRgb);

        var stack = new StackPanel { Width = w };
        stack.Children.Add(zoom);
        stack.Children.Add(colour);
        stack.Children.Add(position);
        _magnifier.Child = stack;
        _magnifier.Background = Frozen(Color.FromArgb(0xEB, 0x15, 0x18, 0x1F));
        _magnifier.BorderBrush = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        _magnifier.BorderThickness = new Thickness(1);
        _magnifier.CornerRadius = new CornerRadius(MagRadius);
        _magnifier.Padding = new Thickness(4, 4, 4, 6);
        _magnifier.Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.3 };
        _magnifier.IsHitTestVisible = false;
        _magnifier.Visibility = Visibility.Collapsed;
        _root.Children.Add(_magnifier);
    }

    private void UpdateMagnifier(Point p)
    {
        bool show = _cfg.ShowMagnifier && _state is State.Idle or State.Selecting or State.Resizing;
        _magnifier.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        int px = (int)(p.X * _s), py = (int)(p.Y * _s);
        int x0 = Math.Clamp(px - MagColumns / 2, 0, _screen.PixelWidth - MagColumns), y0 = Math.Clamp(py - MagRows / 2, 0, _screen.PixelHeight - MagRows);
        _magImage.Source = new CroppedBitmap(_screen, new Int32Rect(x0, y0, MagColumns, MagRows));
        _magGrid.SetCursor(Math.Clamp(px - x0, 0, MagColumns - 1), Math.Clamp(py - y0, 0, MagRows - 1));
        var pixel = new byte[4];
        _screen.CopyPixels(new Int32Rect(Math.Clamp(px, 0, _screen.PixelWidth - 1), Math.Clamp(py, 0, _screen.PixelHeight - 1), 1, 1), pixel, 4, 0);
        _pixelColor = Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        if (_magSwatch.Background is not SolidColorBrush { Color: var shown } || shown != _pixelColor) _magSwatch.Background = Frozen(_pixelColor);
        _magHex.Text = $"#{_pixelColor.R:X2}{_pixelColor.G:X2}{_pixelColor.B:X2}";
        _magRgb.Text = $"{_pixelColor.R}, {_pixelColor.G}, {_pixelColor.B}";
        _magPos.Text = $"{px + _vs.X}, {py + _vs.Y}";
        _magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = _magnifier.DesiredSize;
        double x = p.X + 18, y = p.Y + 22;
        if (x + sz.Width > Bounds.Width) x = p.X - 18 - sz.Width;
        if (y + sz.Height > Bounds.Height) y = p.Y - 22 - sz.Height;
        Canvas.SetLeft(_magnifier, x);
        Canvas.SetTop(_magnifier, y);
    }

    private void UpdateVisuals()
    {
        var shown = !_sel.IsEmpty ? _sel : _state == State.Idle && _hover is Rect h ? h : Rect.Empty;
        var full = new RectangleGeometry(Bounds);
        if (shown.IsEmpty) _dim.Data = full;
        else
        {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(full);
            g.Children.Add(new RectangleGeometry(shown));
            _dim.Data = g;
        }

        bool has = !shown.IsEmpty;
        _border.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has)
        {
            Canvas.SetLeft(_border, shown.X); Canvas.SetTop(_border, shown.Y);
            _border.Width = shown.Width; _border.Height = shown.Height;
        }

        bool selected = !_sel.IsEmpty && _state != State.Selecting;
        var hp = new[]
        {
            new Point(_sel.Left, _sel.Top), new Point(_sel.Left + _sel.Width / 2, _sel.Top), new Point(_sel.Right, _sel.Top),
            new Point(_sel.Right, _sel.Top + _sel.Height / 2), new Point(_sel.Right, _sel.Bottom), new Point(_sel.Left + _sel.Width / 2, _sel.Bottom),
            new Point(_sel.Left, _sel.Bottom), new Point(_sel.Left, _sel.Top + _sel.Height / 2),
        };
        for (int i = 0; i < 8; i++)
        {
            _handles[i].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            if (selected) { Canvas.SetLeft(_handles[i], hp[i].X - 3.5); Canvas.SetTop(_handles[i], hp[i].Y - 3.5); }
        }

        _sizeTag.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has)
        {
            _sizeText.Text = $"{(int)Math.Round(shown.Width * _s)} × {(int)Math.Round(shown.Height * _s)}";
            _sizeTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double ty = shown.Top - _sizeTag.DesiredSize.Height - 6;
            if (ty < 2) ty = shown.Top + 6;
            Canvas.SetLeft(_sizeTag, Math.Max(2, shown.Left));
            Canvas.SetTop(_sizeTag, ty);
        }

        _layer.ClipRect = _sel;
        _layer.InvalidateVisual();

        bool bars = _state == State.Selected || _state == State.Drawing;
        _toolbar.Visibility = bars ? Visibility.Visible : Visibility.Collapsed;
        _styleBar.Visibility = bars && _tool != ShotTool.None ? Visibility.Visible : Visibility.Collapsed;
        if (bars) PlaceBars();
    }

    private void PlaceBars()
    {
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var ts = _toolbar.DesiredSize;
        double styleH = 0;
        if (_styleBar.Visibility == Visibility.Visible)
        {
            _styleBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            styleH = _styleBar.DesiredSize.Height + 6;
        }
        double x = Math.Clamp(_sel.Right - ts.Width, 4, Math.Max(4, Bounds.Width - ts.Width - 4));
        double y = _sel.Bottom + 8;
        bool below = y + ts.Height + styleH <= Bounds.Height;
        if (!below)
        {
            y = _sel.Top - ts.Height - 8 - styleH;
            if (y < 4) y = _sel.Bottom - ts.Height - 8 - styleH; // inside the selection as a last resort
        }
        double toolbarY = below ? y : y + styleH;
        Canvas.SetLeft(_toolbar, x); Canvas.SetTop(_toolbar, toolbarY);
        PlaceGlass(_toolbar, x, toolbarY);
        if (_styleBar.Visibility == Visibility.Visible)
        {
            double styleY = below ? y + ts.Height + 6 : y;
            Canvas.SetLeft(_styleBar, x);
            Canvas.SetTop(_styleBar, styleY);
            PlaceGlass(_styleBar, x, styleY);
        }
    }

    // ------------------------------------------------------------------ keys & output

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_editor != null) return; // the text box handles its own keys
        bool sel = _state == State.Selected;
        // Esc / Enter / arrows are fixed; everything else follows the user's shortcut settings.
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Enter) { if (sel) Copy(); }
        else if (Shortcut.Matches(_cfg.KeyCopy, e)) { if (sel) Copy(); }
        else if (_state == State.Idle && Shortcut.Matches(_cfg.KeyColor, e))
        {
            Clipboard.SetText($"#{_pixelColor.R:X2}{_pixelColor.G:X2}{_pixelColor.B:X2}");
            Close();
        }
        else if (Shortcut.Matches(_cfg.KeySave, e)) { if (sel) Save(); }
        else if (Shortcut.Matches(_cfg.KeyPin, e)) { if (sel) Pin(); }
        else if (Shortcut.Matches(_cfg.KeyUndo, e)) Undo();
        else if (Shortcut.Matches(_cfg.KeyLong, e)) { if (sel) Live("long"); }
        else if (Shortcut.Matches(_cfg.KeyGif, e)) { if (sel) Live("gif"); }
        else if (sel && ToolFor(e) is ShotTool t) SetTool(_tool == t ? ShotTool.None : t);
        else if (sel && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double step = 1 / _s;
            var d = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) };
            var r = _sel; r.Offset(d);
            if (Bounds.Contains(r)) { _sel = SnapRect(r); UpdateVisuals(); }
        }
        else return;
        e.Handled = true;
    }

    private ShotTool? ToolFor(KeyEventArgs e) =>
        Shortcut.Matches(_cfg.KeyRect, e) ? ShotTool.Rect :
        Shortcut.Matches(_cfg.KeyEllipse, e) ? ShotTool.Ellipse :
        Shortcut.Matches(_cfg.KeyArrow, e) ? ShotTool.Arrow :
        Shortcut.Matches(_cfg.KeyPen, e) ? ShotTool.Pen :
        Shortcut.Matches(_cfg.KeyMosaic, e) ? ShotTool.Mosaic :
        Shortcut.Matches(_cfg.KeyText, e) ? ShotTool.Text : null;

    /// <summary>Selection + annotations rendered at physical resolution.</summary>
    private BitmapSource Export()
    {
        if (_editor != null) CommitText();
        var px = ToPx(_sel);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-_sel.X, -_sel.Y));
            dc.DrawImage(_screen, new Rect(0, 0, Bounds.Width, Bounds.Height));
            foreach (var a in _layer.Items) a.Draw(dc);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(px.Width, px.Height, 96 * _s, 96 * _s, PixelFormats.Pbgra32);
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private void Copy()
    {
        if (_sel.IsEmpty) return;
        var img = Export();
        try { Clipboard.SetImage(img); }
        catch (Exception ex) { _host.Log.Error("clipboard image", ex); }
        Close();
    }

    private bool _dialogOpen;

    private void Save()
    {
        if (_sel.IsEmpty) return;
        var img = Export();
        var dir = string.IsNullOrWhiteSpace(_cfg.SaveDir) ? ScreenshotPlugin.DefaultSaveDir : _cfg.SaveDir;
        try { Directory.CreateDirectory(dir); } catch { }
        _dialogOpen = true;
        Topmost = false;
        var path = _host.Ui.SaveFile("PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg", $"PopBar_{DateTime.Now:yyyyMMdd_HHmmss}", dir, owner: this);
        _dialogOpen = false;
        if (path == null) { Topmost = true; Activate(); return; }
        try
        {
            BitmapEncoder enc = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using var fs = File.Create(path);
            enc.Save(fs);
            _cfg.SaveDir = System.IO.Path.GetDirectoryName(path) ?? dir;
        }
        catch (Exception ex) { _host.Log.Error("save screenshot", ex); }
        Close();
    }

    private void Live(string kind)
    {
        if (_sel.IsEmpty) return;
        var px = ToPx(_sel);
        var rect = new Int32Rect(px.X + _vs.X, px.Y + _vs.Y, px.Width, px.Height);
        Close();
        LiveRequested?.Invoke(kind, rect);
    }

    private void Pin()
    {
        if (_sel.IsEmpty) return;
        var img = Export();
        var px = ToPx(_sel);
        PinRequested?.Invoke(img, new Int32Rect(px.X + _vs.X, px.Y + _vs.Y, px.Width, px.Height));
        Close();
    }
}
