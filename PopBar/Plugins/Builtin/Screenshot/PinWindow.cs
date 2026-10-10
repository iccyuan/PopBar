using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PopBar.Sdk;

namespace PopBar.Plugins.Builtin.Screenshot;

/// <summary>
/// An image pinned on top of everything (Snipaste "贴图"): drag to move, wheel to zoom, Ctrl+wheel for opacity,
/// double-click or Esc to close, right-click for copy / save / original size / close.
/// </summary>
internal sealed class PinWindow : Window
{
    private const double Shadow = 12; // DIP reserved around the image for the drop shadow

    private readonly IPluginHost _host;
    private readonly BitmapSource _image;
    private readonly Image _view = new() { Stretch = Stretch.Fill };
    private readonly Border _frame = new();
    private readonly Border _zoomTag = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x18, 0x27)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock _zoomText = new() { Foreground = Brushes.White, FontSize = 11.5 };
    private readonly System.Windows.Threading.DispatcherTimer _tagTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private double _zoom = 1;
    private double _s = 1;
    private readonly Int32Rect _origin;
    private readonly string _saveDir;

    public PinWindow(IPluginHost host, BitmapSource image, Int32Rect screenRect, string saveDir)
    {
        _host = host; _image = image; _origin = screenRect; _saveDir = saveDir;
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
        var accent = (Brush?)Application.Current?.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
        _frame.Child = new Grid { Children = { _view, _zoomTag } };
        _zoomTag.Child = _zoomText;
        _frame.BorderThickness = new Thickness(1);
        _frame.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        _frame.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.35 };
        _frame.Margin = new Thickness(Shadow);
        MouseEnter += (_, _) => _frame.BorderBrush = accent;
        MouseLeave += (_, _) => _frame.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        Content = _frame;

        _tagTimer.Tick += (_, _) => { _tagTimer.Stop(); _zoomTag.Visibility = Visibility.Collapsed; };

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
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Copy();
            else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Save();
        };
        ContextMenu = BuildMenu();
    }

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
            _frame.Opacity = Math.Clamp(_frame.Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1);
            ShowTag($"不透明度 {(int)Math.Round(_frame.Opacity * 100)}%");
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
        _zoomTag.Visibility = Visibility.Visible;
        _tagTimer.Stop(); _tagTimer.Start();
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        void Add(string header, Action a) { var i = new MenuItem { Header = header }; i.Click += (_, _) => a(); m.Items.Add(i); }
        Add("复制图片", Copy);
        Add("另存为…", Save);
        Add("原始大小", () => { _zoom = 1; _frame.Opacity = 1; ApplyZoom(); });
        m.Items.Add(new Separator());
        Add("关闭", Close);
        return m;
    }

    private void Copy()
    {
        try { Clipboard.SetImage(_image); } catch (Exception ex) { _host.Log.Error("pin copy", ex); }
    }

    private void Save()
    {
        try { Directory.CreateDirectory(_saveDir); } catch { }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = _saveDir,
            FileName = $"PopBar_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt = ".png",
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg",
        };
        Topmost = false;
        try
        {
            if (dlg.ShowDialog(this) != true) return;
            BitmapEncoder enc = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(_image));
            using var fs = File.Create(dlg.FileName);
            enc.Save(fs);
        }
        catch (Exception ex) { _host.Log.Error("pin save", ex); }
        finally { Topmost = true; }
    }
}
