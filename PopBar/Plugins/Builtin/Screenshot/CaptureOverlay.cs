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
    private readonly List<Int32Rect> _windows;

    private readonly Canvas _root = new();
    private readonly Image _bg = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _border = new() { StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly List<Rectangle> _handles = new();
    private readonly Border _sizeTag = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x18, 0x27)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };
    private readonly AnnotationLayer _layer = new();
    private readonly Border _toolbar = new();
    private readonly Border _styleBar = new();
    private readonly Border _magnifier = new();
    private readonly Image _magImage = new() { Width = 120, Height = 90, Stretch = Stretch.Fill };
    private readonly TextBlock _magText = new() { Foreground = Brushes.White, FontSize = 11.5, Margin = new Thickness(6, 4, 6, 4) };
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

    public CaptureOverlay(IPluginHost host, ScreenshotPlugin.Settings cfg, BitmapSource screen, Int32Rect vs, List<Int32Rect> windows)
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

    private void UpdateHover(Point p)
    {
        if (!_cfg.DetectWindows) { _hover = null; return; }
        int px = (int)(p.X * _s) + _vs.X, py = (int)(p.Y * _s) + _vs.Y;
        foreach (var r in _windows)
            if (px >= r.X && px < r.X + r.Width && py >= r.Y && py < r.Y + r.Height) { _hover = ToDip(r); return; }
        _hover = Bounds;
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

    // ------------------------------------------------------------------ chrome

    private Button ToolbarButton(string icon, string tip, Action click, Brush? brush = null)
    {
        var b = new Button { Width = 34, Height = 34, ToolTip = tip, Focusable = false, Cursor = Cursors.Arrow, Margin = new Thickness(1, 0, 1, 0) };
        if (TryFindResource("IconButton") is Style st) b.Style = st;
        b.Content = _host.Ui.Icon(icon, 17, brush);
        b.Click += (_, _) => click();
        return b;
    }

    private void BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        void Tool(ShotTool t, string icon, string tip)
        {
            var b = ToolbarButton(icon, tip, () => SetTool(_tool == t ? ShotTool.None : t));
            _toolButtons[t] = b;
            panel.Children.Add(b);
        }
        Tool(ShotTool.Rect, "square", "矩形（按住 Shift 为正方形）");
        Tool(ShotTool.Ellipse, "circle", "椭圆");
        Tool(ShotTool.Arrow, "move-up-right", "箭头");
        Tool(ShotTool.Pen, "pen", "画笔");
        Tool(ShotTool.Mosaic, "grid", "马赛克");
        Tool(ShotTool.Text, "type", "文字");
        panel.Children.Add(Separator());
        panel.Children.Add(ToolbarButton("undo", "撤销 (Ctrl+Z)", Undo));
        panel.Children.Add(Separator());
        panel.Children.Add(ToolbarButton("pin", "贴到屏幕 (Ctrl+T)", Pin));
        panel.Children.Add(ToolbarButton("download", "保存 (Ctrl+S)", Save));
        panel.Children.Add(ToolbarButton("x", "取消 (Esc)", Close, new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))));
        panel.Children.Add(ToolbarButton("check", "复制并完成 (Enter / 双击)", Copy, (Brush?)TryFindResource("AccentBrush") ?? Brushes.DodgerBlue));
        StyleChrome(_toolbar, panel);
        _root.Children.Add(_toolbar);
    }

    private UIElement Separator() => new Rectangle { Width = 1, Height = 20, Margin = new Thickness(4, 0, 4, 0), Fill = (Brush?)TryFindResource("BorderBrush") ?? Brushes.LightGray };

    private void BuildStyleBar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
        foreach (var w in Widths)
        {
            double d = 4 + w * 1.3;
            var dot = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = w, ToolTip = "粗细" };
            dot.Child = new Ellipse { Width = d, Height = d, Fill = (Brush?)TryFindResource("TextBrush") ?? Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dot.MouseLeftButtonDown += (_, e) => { _width = (double)dot.Tag; RefreshStyleBar(); e.Handled = true; };
            _widthDots.Add(dot);
            panel.Children.Add(dot);
        }
        panel.Children.Add(Separator());
        foreach (var c in Palette)
        {
            var sw = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(3), Cursor = Cursors.Hand, Background = new SolidColorBrush(c), BorderThickness = new Thickness(2), Tag = c };
            sw.MouseLeftButtonDown += (_, e) => { _color = (Color)sw.Tag; RefreshStyleBar(); if (_editor != null) _editor.Foreground = new SolidColorBrush(_color); e.Handled = true; };
            _swatches.Add(sw);
            panel.Children.Add(sw);
        }
        StyleChrome(_styleBar, panel);
        _root.Children.Add(_styleBar);
        RefreshStyleBar();
    }

    private void StyleChrome(Border b, UIElement child)
    {
        b.Child = child;
        b.Padding = new Thickness(4);
        b.CornerRadius = new CornerRadius(10);
        b.Background = (Brush?)TryFindResource("CardBrush") ?? Brushes.White;
        b.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.3 };
        b.Visibility = Visibility.Collapsed;
        b.Cursor = Cursors.Arrow;
        RenderOptions.SetClearTypeHint(b, ClearTypeHint.Enabled);
    }

    private void RefreshStyleBar()
    {
        var accent = (Brush?)TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
        var soft = (Brush?)TryFindResource("AccentSoftBrush") ?? Brushes.AliceBlue;
        foreach (var d in _widthDots) d.Background = (double)d.Tag == _width ? soft : Brushes.Transparent;
        foreach (var s in _swatches)
            s.BorderBrush = (Color)s.Tag == _color ? accent : new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));
    }

    private void SetTool(ShotTool t)
    {
        if (_editor != null) CommitText();
        _tool = t;
        var soft = (Brush?)TryFindResource("AccentSoftBrush") ?? Brushes.AliceBlue;
        foreach (var (tool, b) in _toolButtons) b.Background = tool == t ? soft : Brushes.Transparent;
        UpdateVisuals();
    }

    private void BuildMagnifier()
    {
        RenderOptions.SetBitmapScalingMode(_magImage, BitmapScalingMode.NearestNeighbor);
        var grid = new Grid();
        grid.Children.Add(_magImage);
        var cross = new Brush[] { new SolidColorBrush(Color.FromArgb(150, 59, 130, 246)) };
        grid.Children.Add(new Rectangle { Width = 120, Height = 1, Fill = cross[0], VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        grid.Children.Add(new Rectangle { Width = 1, Height = 90, Fill = cross[0], HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false });
        var stack = new StackPanel();
        stack.Children.Add(new Border { Child = grid, BorderBrush = Brushes.White, BorderThickness = new Thickness(1) });
        stack.Children.Add(_magText);
        _magnifier.Child = stack;
        _magnifier.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x11, 0x18, 0x27));
        _magnifier.CornerRadius = new CornerRadius(6);
        _magnifier.Padding = new Thickness(3);
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
        const int hw = 7, hh = 5;
        int x0 = Math.Clamp(px - hw, 0, _screen.PixelWidth - (2 * hw + 1)), y0 = Math.Clamp(py - hh, 0, _screen.PixelHeight - (2 * hh + 1));
        _magImage.Source = new CroppedBitmap(_screen, new Int32Rect(x0, y0, 2 * hw + 1, 2 * hh + 1));
        var pixel = new byte[4];
        _screen.CopyPixels(new Int32Rect(Math.Clamp(px, 0, _screen.PixelWidth - 1), Math.Clamp(py, 0, _screen.PixelHeight - 1), 1, 1), pixel, 4, 0);
        _pixelColor = Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        _magText.Text = $"{px + _vs.X}, {py + _vs.Y}\n#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}   C 复制颜色";
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
        Canvas.SetLeft(_toolbar, x); Canvas.SetTop(_toolbar, below ? y : y + styleH);
        if (_styleBar.Visibility == Visibility.Visible)
        {
            Canvas.SetLeft(_styleBar, x);
            Canvas.SetTop(_styleBar, below ? y + ts.Height + 6 : y);
        }
    }

    // ------------------------------------------------------------------ keys & output

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_editor != null) return; // the text box handles its own keys
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Enter: if (_state == State.Selected) Copy(); break;
            case Key.C when ctrl: if (_state == State.Selected) Copy(); break;
            case Key.C when _state == State.Idle:
                Clipboard.SetText($"#{_pixelColor.R:X2}{_pixelColor.G:X2}{_pixelColor.B:X2}");
                Close();
                break;
            case Key.S when ctrl: if (_state == State.Selected) Save(); break;
            case Key.T when ctrl: if (_state == State.Selected) Pin(); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.Left or Key.Right or Key.Up or Key.Down when _state == State.Selected:
            {
                double step = 1 / _s;
                var d = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) };
                var r = _sel; r.Offset(d);
                if (Bounds.Contains(r)) { _sel = SnapRect(r); UpdateVisuals(); }
                break;
            }
            default: return;
        }
        e.Handled = true;
    }

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
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = dir,
            FileName = $"PopBar_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt = ".png",
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg",
        };
        _dialogOpen = true;
        Topmost = false;
        bool ok = dlg.ShowDialog(this) == true;
        _dialogOpen = false;
        if (!ok) { Topmost = true; Activate(); return; }
        try
        {
            BitmapEncoder enc = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using var fs = File.Create(dlg.FileName);
            enc.Save(fs);
            _cfg.SaveDir = System.IO.Path.GetDirectoryName(dlg.FileName) ?? dir;
        }
        catch (Exception ex) { _host.Log.Error("save screenshot", ex); }
        Close();
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
