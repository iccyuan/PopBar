using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace PopBar.Plugins.Builtin.Screenshot;

/// <summary>
/// An image pinned on top of everything (Snipaste "贴图"): drag to move, wheel to zoom, Ctrl+wheel for opacity,
/// double-click or Esc to close, right-click menu for copy / save / original size / close.
/// </summary>
internal sealed class PinWindow : Window
{
    private const double Shadow = 16; // DIP reserved around the image for the drop shadow
    private const double Radius = 6;

    private static readonly Brush Glass = Frozen(Color.FromArgb(0xD9, 0x17, 0x1B, 0x24));

    private readonly IPluginHost _host;
    private readonly BitmapSource _image;
    private readonly Image _view = new() { Stretch = Stretch.Fill };
    private readonly Grid _stage = new();
    private readonly Border _shadow = new();
    private readonly Border _ring = new() { BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(Radius), IsHitTestVisible = false, Opacity = 0 };
    private readonly Border _zoomTag = new() { CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 10, 4), Background = Glass, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8), Opacity = 0, IsHitTestVisible = false };
    private readonly TextBlock _zoomText = new() { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Medium };
    private readonly System.Windows.Threading.DispatcherTimer _tagTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private double _zoom = 1;
    private double _s = 1;
    private readonly Int32Rect _origin;
    private readonly string _saveDir;
    private readonly ScreenshotPlugin.Settings _cfg;

    public PinWindow(IPluginHost host, BitmapSource image, Int32Rect screenRect, string saveDir, ScreenshotPlugin.Settings cfg)
    {
        _host = host; _image = image; _origin = screenRect; _saveDir = saveDir; _cfg = cfg;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        Title = "PopBar Pin";
        SizeToContent = SizeToContent.WidthAndHeight;

        _view.Source = image;
        RenderOptions.SetBitmapScalingMode(_view, BitmapScalingMode.HighQuality);
        _view.SizeChanged += (_, _) => _view.Clip = new RectangleGeometry(new Rect(_view.RenderSize), Radius, Radius);

        // The shadow is cast by a separate plate so the image itself never goes through an effect (stays pixel-exact).
        _shadow.CornerRadius = new CornerRadius(Radius);
        _shadow.Background = Brushes.White;
        _shadow.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.32 };
        _ring.BorderBrush = (Brush?)Application.Current?.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
        _zoomTag.Child = _zoomText;

        _stage.Margin = new Thickness(Shadow);
        _stage.Children.Add(_shadow);
        _stage.Children.Add(_view);
        _stage.Children.Add(new Border { CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(1), BorderBrush = Frozen(Color.FromArgb(40, 0, 0, 0)), IsHitTestVisible = false });
        _stage.Children.Add(_ring);
        _stage.Children.Add(_zoomTag);
        Content = _stage;

        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        _tagTimer.Tick += (_, _) => { _tagTimer.Stop(); Fade(_zoomTag, 0, 180); };

        // Size and place from the target monitor DPI before the first layout; snap to exact pixels once rendered.
        // (Calling SetWindowPos with SHOWWINDOW before a layered window has rendered leaves it empty and unsized.)
        _s = ShotNative.ScaleAt(_origin.X, _origin.Y);
        ApplyZoom();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _origin.X / _s - Shadow;
        Top = _origin.Y / _s - Shadow;
        SourceInitialized += (_, _) => ShotNative.MakeToolWindow(this);
        Loaded += (_, _) =>
        {
            int m = (int)Math.Round(Shadow * _s);
            ShotNative.SetWindowPos(new WindowInteropHelper(this).Handle, ShotNative.HWND_TOPMOST, _origin.X - m, _origin.Y - m, 0, 0, ShotNative.SWP_NOSIZE | ShotNative.SWP_NOACTIVATE);
        };
        DpiChanged += (_, e) => { _s = e.NewDpi.DpiScaleX; ApplyZoom(); };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Close(); return; }
            try { DragMove(); } catch { }
        };
        MouseWheel += OnWheel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (Shortcut.Matches(_cfg.KeyCopy, e)) Copy();
            else if (Shortcut.Matches(_cfg.KeySave, e)) Save();
        };
        ContextMenu = BuildMenu();
        // Destroy, not hide: drop the bitmap so a closed pin frees its memory right away.
        Closed += (_, _) => { _tagTimer.Stop(); _view.Source = null; ContextMenu = null; };
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private void SetHover(bool on) => Fade(_ring, on ? 1 : 0, 140);

    private static void Fade(UIElement e, double to, int ms) =>
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase() });

    /// <summary>Image size in DIP so that 100 % zoom shows it 1:1 in physical pixels.</summary>
    private void ApplyZoom()
    {
        _view.Width = _image.PixelWidth * _zoom / _s;
        _view.Height = _image.PixelHeight * _zoom / _s;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _stage.Opacity = Math.Clamp(_stage.Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1);
            ShowTag($"不透明度 {(int)Math.Round(_stage.Opacity * 100)}%");
        }
        else
        {
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 5);
            if (Math.Abs(_zoom - 1) < 0.04) _zoom = 1;
            ApplyZoom();
            ShowTag($"{(int)Math.Round(_zoom * 100)}%");
        }
        e.Handled = true;
    }

    private void ShowTag(string text)
    {
        _zoomText.Text = text;
        _zoomTag.BeginAnimation(OpacityProperty, null);
        _zoomTag.Opacity = 1;
        _tagTimer.Stop(); _tagTimer.Start();
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        var muted = (Brush?)Application.Current?.TryFindResource("MutedBrush") ?? Brushes.Gray;
        void Add(string icon, string header, string gesture, Action a)
        {
            var i = new MenuItem { Header = header, InputGestureText = gesture, Icon = _host.Ui.Icon(icon, 15, muted) };
            i.Click += (_, _) => a();
            m.Items.Add(i);
        }
        Add("copy", "复制图片", Shortcut.Display(_cfg.KeyCopy), Copy);
        Add("download", "另存为…", Shortcut.Display(_cfg.KeySave), Save);
        Add("search", "原始大小", "100%", () => { _zoom = 1; _stage.Opacity = 1; ApplyZoom(); ShowTag("100%"); });
        m.Items.Add(new Separator());
        Add("x", "销毁", "Esc", Close);
        return m;
    }

    private void Copy()
    {
        try { Clipboard.SetImage(_image); } catch (Exception ex) { _host.Log.Error("pin copy", ex); }
    }

    private void Save()
    {
        Topmost = false;
        try
        {
            var path = _host.Ui.SaveFile("PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg", $"PopBar_{DateTime.Now:yyyyMMdd_HHmmss}", _saveDir, owner: this);
            if (path == null) return;
            BitmapEncoder enc = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(_image));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        catch (Exception ex) { _host.Log.Error("pin save", ex); }
        finally { Topmost = true; }
    }
}
