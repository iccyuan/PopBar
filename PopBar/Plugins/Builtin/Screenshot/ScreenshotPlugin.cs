using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace PopBar.Plugins.Builtin.Screenshot;

/// <summary>Snipaste-style screenshot + pin-to-screen. Uses only the SDK contract plus its own windows.</summary>
public sealed class ScreenshotPlugin : IPopBarPlugin
{
    public sealed class Settings
    {
        public string CaptureHotkey { get; set; } = "F1";
        public string PinHotkey { get; set; } = "F3";
        public string SaveDir { get; set; } = "";
        public bool ShowMagnifier { get; set; } = true;
        public bool DetectWindows { get; set; } = true;
    }

    public static string DefaultSaveDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PopBar");

    private IPluginHost _host = null!;
    private Settings _cfg = new();
    private CaptureOverlay? _overlay;

    public string Id => "screenshot";
    public string Name => "截图";
    public string Description => "冻结屏幕框选、自动识别窗口、标注、复制 / 保存 / 贴到屏幕。";
    public Version Version => new(0, 3, 0);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
        host.AddTrayMenuItem("capture", "截图", Capture);
        host.AddTrayMenuItem("pin", "贴图（剪贴板）", PinClipboard);
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        _host.UnregisterHotkey("capture");
        _host.UnregisterHotkey("pin");
        if (!string.IsNullOrWhiteSpace(_cfg.CaptureHotkey) && !_host.RegisterHotkey("capture", _cfg.CaptureHotkey, Capture))
            _host.Log.Warn($"capture hotkey {_cfg.CaptureHotkey} unavailable");
        if (!string.IsNullOrWhiteSpace(_cfg.PinHotkey) && !_host.RegisterHotkey("pin", _cfg.PinHotkey, PinClipboard))
            _host.Log.Warn($"pin hotkey {_cfg.PinHotkey} unavailable");
    }

    public IReadOnlyList<PluginAction> GetActions() => Array.Empty<PluginAction>();

    // ------------------------------------------------------------------ capture

    /// <summary>Freeze the screen and open the selection overlay (UI thread).</summary>
    public void Capture()
    {
        _host.Dispatcher.InvokeAsync(() =>
        {
            if (_overlay != null) { _overlay.Activate(); return; }
            try
            {
                var vs = ShotNative.VirtualScreen();
                var windows = _cfg.DetectWindows ? ShotNative.WindowRects(vs) : new List<Int32Rect>();
                var screen = ShotNative.CaptureScreen(vs);   // before our overlay exists
                var suppress = _host.SuppressSelection();     // our drags must not look like text selections
                var overlay = new CaptureOverlay(_host, _cfg, screen, vs, windows);
                overlay.PinRequested += (img, rect) => ShowPin(img, rect);
                overlay.Closed += (_, _) =>
                {
                    suppress.Dispose();
                    _overlay = null;
                    _host.Storage.Set(_cfg); // remembers the last save folder
                };
                _overlay = overlay;
                overlay.Show();
            }
            catch (Exception ex)
            {
                _host.Log.Error("capture failed", ex);
                _overlay = null;
            }
        });
    }

    private void ShowPin(BitmapSource img, Int32Rect rect)
    {
        var dir = string.IsNullOrWhiteSpace(_cfg.SaveDir) ? DefaultSaveDir : _cfg.SaveDir;
        new PinWindow(_host, img, rect, dir).Show();
    }

    /// <summary>Pin the clipboard image (or text, rendered as a card) next to the cursor.</summary>
    public void PinClipboard()
    {
        _host.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                BitmapSource? img = null;
                if (Clipboard.ContainsImage()) img = Clipboard.GetImage();
                else if (Clipboard.ContainsText()) img = RenderTextCard(Clipboard.GetText());
                if (img == null) { _host.Log.Info("pin: clipboard has no image or text"); return; }
                var pos = System.Windows.Forms.Cursor.Position;
                ShowPin(img, new Int32Rect(pos.X, pos.Y, img.PixelWidth, img.PixelHeight));
            }
            catch (Exception ex) { _host.Log.Error("pin clipboard", ex); }
        });
    }

    private static BitmapSource RenderTextCard(string text)
    {
        if (text.Length > 4000) text = text[..4000] + "…";
        var probe = new Window();
        double scale = 1;
        try { scale = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? probe).DpiScaleX; } catch { }
        var card = new Border
        {
            Background = (Brush?)Application.Current.TryFindResource("CardBrush") ?? Brushes.White,
            Padding = new Thickness(16, 12, 16, 12),
            Child = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, FontSize = 14,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Foreground = (Brush?)Application.Current.TryFindResource("TextBrush") ?? Brushes.Black,
            },
        };
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth * scale), (int)Math.Ceiling(card.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(card);
        rtb.Freeze();
        return rtb;
    }

    // ------------------------------------------------------------------ settings

    public FrameworkElement? CreateSettingsView()
    {
        void Save() => _host.Storage.Set(_cfg);
        var ui = _host.Ui;

        var dirBox = Form.TextBox(string.IsNullOrWhiteSpace(_cfg.SaveDir) ? DefaultSaveDir : _cfg.SaveDir, 260, v => { _cfg.SaveDir = v.Trim(); Save(); });
        var browse = Form.Button("选择…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = dirBox.Text };
            if (dlg.ShowDialog() == true) { dirBox.Text = dlg.FolderName; }
        });
        browse.Margin = new Thickness(8, 0, 0, 0);
        var dirRow = new StackPanel { Orientation = Orientation.Horizontal };
        dirRow.Children.Add(dirBox);
        dirRow.Children.Add(browse);

        return Form.Stack(
            Form.Card(
                Form.Row("截图快捷键", ui.HotkeyBox(_cfg.CaptureHotkey, v => { _cfg.CaptureHotkey = v; Save(); RegisterHotkeys(); }), "托盘菜单里也有「截图」"),
                Form.Row("贴图快捷键", ui.HotkeyBox(_cfg.PinHotkey, v => { _cfg.PinHotkey = v; Save(); RegisterHotkeys(); }), "把剪贴板里的图片或文字贴到屏幕上"),
                Form.Row("保存目录", dirRow),
                Form.Row("显示放大镜", Form.Toggle(_cfg.ShowMagnifier, v => { _cfg.ShowMagnifier = v; Save(); })),
                Form.Row("自动识别窗口", Form.Toggle(_cfg.DetectWindows, v => { _cfg.DetectWindows = v; Save(); }), "鼠标悬停时高亮窗口，单击直接选中")),
            Form.Hint("截图中：拖动框选，单击选中窗口；Enter / Ctrl+C / 双击 复制，Ctrl+S 保存，Ctrl+T 贴图，Ctrl+Z 撤销，方向键微调，Esc / 右键 取消，C 复制取色。"),
            Form.Hint("贴图：拖动移动，滚轮缩放，Ctrl+滚轮 透明度，双击 / Esc 销毁，悬停或右键更多操作。"));
    }

    public void Dispose() { }
}
