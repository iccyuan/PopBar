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
        public string CaptureHotkey { get; set; } = DefaultCaptureHotkey;
        public string PinHotkey { get; set; } = "F3";
        public string SaveDir { get; set; } = "";
        public bool ShowMagnifier { get; set; } = true;
        public bool DetectWindows { get; set; } = true;
        public int GifFps { get; set; } = 12;
        public int GifMaxSeconds { get; set; } = 60;
        public bool GifCursor { get; set; } = true;

        // In-window shortcuts (capture overlay; copy / save also apply to pinned images). Esc / Enter / arrows are fixed.
        public string KeyCopy { get; set; } = "Ctrl+C";
        public string KeySave { get; set; } = "Ctrl+S";
        public string KeyPin { get; set; } = "Ctrl+T";
        public string KeyUndo { get; set; } = "Ctrl+Z";
        public string KeyLong { get; set; } = "Ctrl+L";
        public string KeyGif { get; set; } = "Ctrl+G";
        public string KeyColor { get; set; } = "C";
        public string KeyRect { get; set; } = "R";
        public string KeyEllipse { get; set; } = "E";
        public string KeyArrow { get; set; } = "A";
        public string KeyPen { get; set; } = "P";
        public string KeyMosaic { get; set; } = "M";
        public string KeyText { get; set; } = "T";
    }

    /// <summary>Same Ctrl+Alt family as OCR / QR / screen translate; F1 is taken by Snipaste and similar tools on many machines.</summary>
    public const string DefaultCaptureHotkey = "Ctrl+Alt+A";
    /// <summary>The default before 0.4; a stored value equal to it is moved to <see cref="DefaultCaptureHotkey"/> once.</summary>
    private const string LegacyCaptureHotkey = "F1";

    public static string DefaultSaveDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PopBar");

    private IPluginHost _host = null!;
    private Settings _cfg = new();
    private CaptureOverlay? _overlay;

    public string Id => "screenshot";
    public string Icon => "crop";
    public string Name => "截图";
    public string Description => "框选、识别窗口、标注，复制、保存或贴到屏幕。";
    public Version Version => new(0, 3, 0);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        ShotTargets.Log = host.Log;
        _cfg = host.Storage.Get<Settings>();
        if (string.Equals(_cfg.CaptureHotkey, LegacyCaptureHotkey, StringComparison.OrdinalIgnoreCase))
        {
            _cfg.CaptureHotkey = DefaultCaptureHotkey;
            host.Storage.Set(_cfg);
        }
        host.AddTrayMenuItem("capture", "截图", Capture, "crop");
        host.AddTrayMenuItem("pin", "贴图（剪贴板）", PinClipboard, "pin");
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

    /// <summary>
    /// Fill in the windows' parts (controls, taskbar buttons) without holding up the overlay: the UI Automation walk of
    /// the taskbar takes long enough to be felt between the hotkey and the overlay.
    /// </summary>
    private async Task LoadPartsAsync(CaptureOverlay overlay, List<ShotTarget> windows)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var parts = await Task.Run(() => ShotTargets.CollectParts(windows));
            for (int i = 0; i < windows.Count; i++) windows[i].Parts = parts[i];
            _host.Log.Info($"window parts: {parts.Sum(p => p.Count)} in {clock.ElapsedMilliseconds} ms");
            if (ReferenceEquals(_overlay, overlay)) overlay.PartsReady();
        }
        catch (Exception ex) { _host.Log.Warn("window parts unavailable: " + ex.Message); }
    }

    /// <summary>Freeze the screen and open the selection overlay (UI thread).</summary>
    public void Capture()
    {
        _host.Dispatcher.InvokeAsync(() =>
        {
            if (_overlay != null) { _overlay.Activate(); return; }
            try
            {
                var vs = ShotNative.VirtualScreen();
                var windows = _cfg.DetectWindows ? ShotNative.WindowRects(vs) : new List<ShotTarget>();
                var screen = ShotNative.CaptureScreen(vs);   // before our overlay exists
                var suppress = _host.SuppressSelection();     // our drags must not look like text selections
                var overlay = new CaptureOverlay(_host, _cfg, screen, vs, windows);
                overlay.PinRequested += (img, rect) => ShowPin(img, rect);
                overlay.LiveRequested += (kind, rect) => StartLive(kind, rect);
                overlay.Closed += (_, _) =>
                {
                    suppress.Dispose();
                    _overlay = null;
                    _host.Storage.Set(_cfg); // remembers the last save folder
                };
                _overlay = overlay;
                overlay.Show();
                if (windows.Count > 0) _ = LoadPartsAsync(overlay, windows);
            }
            catch (Exception ex)
            {
                _host.Log.Error("capture failed", ex);
                _overlay = null;
            }
        });
    }

    private string SaveDir => string.IsNullOrWhiteSpace(_cfg.SaveDir) ? DefaultSaveDir : _cfg.SaveDir;

    /// <summary>Long screenshot / GIF recording of a region picked in the overlay (starts once the overlay is gone).</summary>
    private void StartLive(string kind, Int32Rect rect)
    {
        _host.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(150);
            try
            {
                if (kind == "gif") new GifRecorder(_host, rect, _cfg, SaveDir).Start();
                else new ScrollCapture(_host, rect, SaveDir).Start();
            }
            catch (Exception ex) { _host.Log.Error("live capture " + kind, ex); }
        });
    }

    private void ShowPin(BitmapSource img, Int32Rect rect)
    {
        var dir = string.IsNullOrWhiteSpace(_cfg.SaveDir) ? DefaultSaveDir : _cfg.SaveDir;
        new PinWindow(_host, img, rect, dir, _cfg).Show();
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
        // Edit a draft; it only takes effect when the settings window is saved.
        var d = SettingsDraft.Clone(_cfg);
        var origDir = d.SaveDir;
        void Changed() => _host.Settings.Changed();
        _host.Settings.OnApply(d, () =>
        {
            // The capture overlay remembers the last save folder at runtime; keep it unless the draft changed the folder.
            var folder = d.SaveDir == origDir ? _cfg.SaveDir : d.SaveDir;
            _cfg = SettingsDraft.Clone(d);
            _cfg.SaveDir = folder;
            _host.Storage.Set(_cfg);
            RegisterHotkeys();
        });
        var ui = _host.Ui;

        var dirBox = Form.TextBox(string.IsNullOrWhiteSpace(d.SaveDir) ? DefaultSaveDir : d.SaveDir, 260, v => { d.SaveDir = v.Trim(); Changed(); });
        var browse = ui.FolderBrowse("选择…", () => dirBox.Text, f => dirBox.Text = f);
        browse.Margin = new Thickness(8, 0, 0, 0);
        var dirRow = new StackPanel { Orientation = Orientation.Horizontal };
        dirRow.Children.Add(dirBox);
        dirRow.Children.Add(browse);

        return Form.Stack(
            Form.Card(
                Form.Row("截图快捷键", ui.HotkeyBox(d.CaptureHotkey, v => { d.CaptureHotkey = v; Changed(); }), null),
                Form.Row("贴图快捷键", ui.HotkeyBox(d.PinHotkey, v => { d.PinHotkey = v; Changed(); }), "把剪贴板内容贴到屏幕上"),
                Form.Row("保存目录", dirRow),
                Form.Row("显示放大镜", Form.Toggle(d.ShowMagnifier, v => { d.ShowMagnifier = v; Changed(); })),
                Form.Row("自动识别窗口", Form.Toggle(d.DetectWindows, v => { d.DetectWindows = v; Changed(); }), "鼠标悬停时高亮窗口，单击直接选中")),
            Form.Card(
                Form.Row("GIF 帧率", Form.Combo(new[] { ("8", "8 帧/秒"), ("12", "12 帧/秒"), ("15", "15 帧/秒"), ("20", "20 帧/秒") }, d.GifFps.ToString(), v => { d.GifFps = int.Parse(v); Changed(); }, 140), "越高越流畅，文件越大"),
                Form.Row("GIF 最长时长", Form.Combo(new[] { ("30", "30 秒"), ("60", "1 分钟"), ("180", "3 分钟"), ("600", "10 分钟") }, d.GifMaxSeconds.ToString(), v => { d.GifMaxSeconds = int.Parse(v); Changed(); }, 140)),
                Form.Row("录入鼠标指针", Form.Toggle(d.GifCursor, v => { d.GifCursor = v; Changed(); }))),
            Form.Card(
                KeyRow("复制", () => d.KeyCopy, v => d.KeyCopy = v, "截图和贴图里都生效；Enter / 双击也能复制"),
                KeyRow("保存", () => d.KeySave, v => d.KeySave = v, "截图和贴图里都生效"),
                KeyRow("贴到屏幕", () => d.KeyPin, v => d.KeyPin = v),
                KeyRow("撤销", () => d.KeyUndo, v => d.KeyUndo = v),
                KeyRow("长截图", () => d.KeyLong, v => d.KeyLong = v),
                KeyRow("录制 GIF", () => d.KeyGif, v => d.KeyGif = v),
                KeyRow("复制色值", () => d.KeyColor, v => d.KeyColor = v, "未框选时复制放大镜下的颜色"),
                KeyRow("矩形", () => d.KeyRect, v => d.KeyRect = v),
                KeyRow("椭圆", () => d.KeyEllipse, v => d.KeyEllipse = v),
                KeyRow("箭头", () => d.KeyArrow, v => d.KeyArrow = v),
                KeyRow("画笔", () => d.KeyPen, v => d.KeyPen = v),
                KeyRow("马赛克", () => d.KeyMosaic, v => d.KeyMosaic = v),
                KeyRow("文字", () => d.KeyText, v => d.KeyText = v)),
            Form.Hint("「长截图」自动向下滚动拼接，「GIF」开始录制；结果存到上面的目录并复制到剪贴板。"),
            Form.Hint("截图：拖动框选，单击选窗口，方向键微调，Esc 取消。贴图：拖动移动，滚轮缩放，Ctrl+滚轮调透明度，双击关闭。"));
    }

    private FrameworkElement KeyRow(string label, Func<string> get, Action<string> set, string? hint = null) =>
        Form.Row(label, _host.Ui.HotkeyBox(get(), v => { set(v); _host.Settings.Changed(); }), hint);

    public void Dispose() { }
}
