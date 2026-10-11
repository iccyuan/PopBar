using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PopBar.Plugins.Builtin.Screenshot;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace PopBar.Plugins.Builtin;

/// <summary>
/// Translate what is on screen in place: drag a region, OCR it, translate paragraph by paragraph and paint the
/// translation over the original (sampled background / text colours). Uses the shared IOcrEngine + ITextTranslator.
/// </summary>
public sealed class ScreenTranslatePlugin : IPopBarPlugin
{
    public sealed class Settings
    {
        public string Hotkey { get; set; } = "Ctrl+Alt+T";
        /// <summary>Hold to show the original in the result window.</summary>
        public string KeyCompare { get; set; } = "Space";
        public string KeyCopy { get; set; } = "Ctrl+C";
    }

    private IPluginHost _host = null!;
    private Settings _cfg = new();
    private int _running;

    public string Id => "screentranslate";
    public string Name => "屏幕翻译";
    public string Description => "框选屏幕区域，识别文字并把译文覆盖在原位置；按住空格对照原文。依赖「屏幕 OCR」和「翻译」插件。";
    public Version Version => new(0, 1, 0);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
        host.AddTrayMenuItem("run", "屏幕翻译", () => _ = RunAsync());
        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        _host.UnregisterHotkey("run");
        if (!string.IsNullOrWhiteSpace(_cfg.Hotkey) && !_host.RegisterHotkey("run", _cfg.Hotkey, () => _ = RunAsync()))
            _host.Log.Warn($"hotkey {_cfg.Hotkey} unavailable");
    }

    public IReadOnlyList<PluginAction> GetActions() => Array.Empty<PluginAction>();

    public async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var ocr = _host.Services.Get<IOcrEngine>();
            var translator = _host.Services.Get<ITextTranslator>();
            if (ocr == null || translator == null)
            {
                var c = System.Windows.Forms.Cursor.Position;
                var p = _host.ShowPanel(new PanelOptions { Title = "屏幕翻译", Icon = "scan-text", AnchorX = c.X, AnchorY = c.Y });
                p.SetText("屏幕翻译需要同时启用「屏幕 OCR」和「翻译」插件。");
                return;
            }

            var region = await _host.CaptureScreenRegionAsync();
            if (region == null) return;
            var image = await _host.Dispatcher.InvokeAsync(() => Decode(region.Png));
            var win = await _host.Dispatcher.InvokeAsync(() =>
            {
                var w = new TranslateOverlay(_host, image, new Int32Rect(region.X, region.Y, region.Width, region.Height), _cfg);
                w.Show();
                w.SetStatus("识别中…", busy: true);
                return w;
            });

            try
            {
                var result = await ocr.RecognizeAsync(region.Png, win.Cancellation);
                var paragraphs = Group(result.Lines);
                if (paragraphs.Count == 0) { await _host.Dispatcher.InvokeAsync(() => win.SetStatus("没有识别到文字", busy: false)); return; }
                await _host.Dispatcher.InvokeAsync(() => win.SetStatus($"翻译中… {paragraphs.Count} 段", busy: true));

                var translated = await TranslateAll(translator, paragraphs.Select(p => p.Text).ToList(), win.Cancellation);
                await _host.Dispatcher.InvokeAsync(() =>
                {
                    win.ShowTranslation(paragraphs.Zip(translated, (p, t) => (p.Bounds, p.LineHeight, p.Text, t)).ToList());
                    win.SetStatus(string.IsNullOrWhiteSpace(_cfg.KeyCompare) ? "右键更多" : $"按住 {Shortcut.Display(_cfg.KeyCompare)} 看原文 · 右键更多", busy: false, fadeOut: true);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _host.Log.Error("screen translate", ex);
                await _host.Dispatcher.InvokeAsync(() => win.SetStatus("失败：" + ex.Message, busy: false));
            }
        }
        catch (Exception ex) { _host.Log.Error("screen translate", ex); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    /// <summary>One request for all paragraphs (line-separated); falls back to one request per paragraph if the line count drifts.</summary>
    private static async Task<List<string>> TranslateAll(ITextTranslator tr, List<string> paras, CancellationToken ct)
    {
        var joined = await tr.TranslateAsync(string.Join("\n", paras), null, ct);
        var lines = joined.Text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == paras.Count) return lines;
        var outp = new List<string>();
        foreach (var p in paras) outp.Add((await tr.TranslateAsync(p, null, ct)).Text.Trim());
        return outp;
    }

    // ------------------------------------------------------------------ layout analysis

    internal sealed record Paragraph(string Text, Rect Bounds, double LineHeight);

    /// <summary>Merge OCR lines into paragraphs: stacked lines of similar height that overlap horizontally and sit close together.</summary>
    internal static List<Paragraph> Group(IReadOnlyList<OcrLine> lines)
    {
        var result = new List<Paragraph>();
        var cur = new List<OcrLine>();
        void Flush()
        {
            if (cur.Count == 0) return;
            var sb = new StringBuilder();
            foreach (var l in cur)
            {
                var t = l.Text.Trim();
                if (sb.Length > 0 && !(IsCjk(sb[^1]) && t.Length > 0 && IsCjk(t[0])))
                {
                    if (sb[^1] == '-' && sb.Length > 1 && char.IsLetter(sb[^2])) sb.Length--; // re-join hyphenated words
                    else sb.Append(' ');
                }
                sb.Append(t);
            }
            var bounds = cur.Select(l => l.Bounds).Aggregate((a, b) => Rect.Union(a, b));
            result.Add(new Paragraph(sb.ToString(), bounds, cur.Average(l => l.Bounds.Height)));
            cur.Clear();
        }

        foreach (var l in lines.Where(l => l.Text.Trim().Length > 0).OrderBy(l => l.Bounds.Top).ThenBy(l => l.Bounds.Left))
        {
            if (cur.Count > 0)
            {
                var last = cur[^1];
                double h = cur.Average(x => x.Bounds.Height);
                var block = cur.Select(x => x.Bounds).Aggregate((a, b) => Rect.Union(a, b));
                bool close = l.Bounds.Top - last.Bounds.Bottom < h * 0.9;
                bool sameSize = Math.Abs(l.Bounds.Height - h) < h * 0.35;
                bool overlap = l.Bounds.Left < block.Right && l.Bounds.Right > block.Left;
                if (!(close && sameSize && overlap)) Flush();
            }
            cur.Add(l);
        }
        Flush();
        return result;
    }

    private static bool IsCjk(char c) => (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3040 && c <= 0x30FF) || (c >= 0xAC00 && c <= 0xD7AF) || (c >= 0x3000 && c <= 0x303F) || (c >= 0xFF00 && c <= 0xFFEF);

    private static BitmapSource Decode(byte[] png)
    {
        var f = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var b = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
        b.Freeze();
        return b;
    }

    public FrameworkElement? CreateSettingsView() => Form.Stack(
        Form.Card(
            Form.Row("快捷键", _host.Ui.HotkeyBox(_cfg.Hotkey, v => { _cfg.Hotkey = v; _host.Storage.Set(_cfg); RegisterHotkey(); }), "框选区域后自动识别并翻译；托盘菜单里也有入口"),
            Form.Row("按住看原文", _host.Ui.HotkeyBox(_cfg.KeyCompare, v => { _cfg.KeyCompare = v; _host.Storage.Set(_cfg); }), "结果窗口里按住时显示原文"),
            Form.Row("复制译文", _host.Ui.HotkeyBox(_cfg.KeyCopy, v => { _cfg.KeyCopy = v; _host.Storage.Set(_cfg); }))),
        Form.Hint("译文语言、翻译引擎跟随「翻译」插件的设置；识别语言跟随「屏幕 OCR」插件。结果窗口：拖动移动，双击 / Esc 关闭，右键更多；快捷键 Backspace 清除。"));

    public void Dispose() { }
}

/// <summary>Frozen copy of the region with translated paragraphs painted over it, placed exactly over the original.</summary>
internal sealed class TranslateOverlay : Window
{
    private readonly IPluginHost _host;
    private readonly BitmapSource _image;
    private readonly byte[] _px;
    private readonly int _stride;
    private readonly Int32Rect _rect;
    private readonly double _s;
    private readonly Canvas _layer = new();
    private readonly Border _status = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 5, 12, 6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0), IsHitTestVisible = false };
    private readonly TextBlock _statusText = new() { Foreground = Brushes.White, FontSize = 12.5 };
    private readonly CancellationTokenSource _cts = new();
    private readonly ScreenTranslatePlugin.Settings _cfg;
    private List<(Rect Bounds, double LineHeight, string Source, string Text)> _items = new();

    public CancellationToken Cancellation => _cts.Token;

    public TranslateOverlay(IPluginHost host, BitmapSource image, Int32Rect rect, ScreenTranslatePlugin.Settings cfg)
    {
        _host = host; _image = image; _rect = rect; _cfg = cfg;
        _stride = image.PixelWidth * 4;
        _px = new byte[_stride * image.PixelHeight];
        image.CopyPixels(_px, _stride, 0);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Title = "PopBar 屏幕翻译";
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _s = ShotNative.ScaleAt(rect.X, rect.Y);
        double w = rect.Width / _s, h = rect.Height / _s;
        Left = rect.X / _s; Top = rect.Y / _s;
        SizeToContent = SizeToContent.WidthAndHeight;

        var img = new Image { Source = image, Width = w, Height = h, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        _layer.Width = w; _layer.Height = h;
        _status.Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x17, 0x1B, 0x24));
        _status.Child = _statusText;
        var frame = new Border { BorderThickness = new Thickness(1), BorderBrush = (Brush?)Application.Current?.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue, IsHitTestVisible = false };
        var root = new Grid { Width = w, Height = h, ClipToBounds = true, Children = { img, _layer, frame, _status } };
        RenderOptions.SetClearTypeHint(_layer, ClearTypeHint.Enabled);
        Content = root;

        SourceInitialized += (_, _) => ShotNative.MakeToolWindow(this);
        Loaded += (_, _) =>
        {
            ShotNative.SetWindowPos(new WindowInteropHelper(this).Handle, ShotNative.HWND_TOPMOST, rect.X, rect.Y, 0, 0, ShotNative.SWP_NOSIZE | ShotNative.SWP_NOACTIVATE);
            Activate();
        };
        Closed += (_, _) => _cts.Cancel();
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Close(); else try { DragMove(); } catch { } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (Shortcut.Matches(_cfg.KeyCompare, e)) { _layer.Visibility = Visibility.Hidden; e.Handled = true; }
            else if (Shortcut.Matches(_cfg.KeyCopy, e)) CopyTranslation();
        };
        KeyUp += (_, e) => { if (Shortcut.IsKeyOf(_cfg.KeyCompare, e)) _layer.Visibility = Visibility.Visible; };
        ContextMenu = BuildMenu();
    }

    public void SetStatus(string text, bool busy, bool fadeOut = false)
    {
        _statusText.Text = text;
        _status.BeginAnimation(OpacityProperty, null);
        _status.Opacity = 1;
        if (fadeOut)
            _status.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromSeconds(2.5) });
    }

    public void ShowTranslation(List<(Rect Bounds, double LineHeight, string Source, string Text)> items)
    {
        _items = items;
        _layer.Children.Clear();
        foreach (var (b, lh, _, text) in items)
        {
            var (bg, fg) = SampleColors(b);
            var box = new Rect(b.X / _s, b.Y / _s, b.Width / _s, b.Height / _s);
            box.Inflate(2, 1);
            var tb = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(fg),
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"), Width = box.Width,
            };
            TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
            // Largest font (capped by the original line height) whose wrapped text fits the paragraph box.
            double size = Math.Clamp(lh / _s * 0.8, 8, 28);
            for (; size > 8; size -= 0.5)
            {
                tb.FontSize = size;
                tb.Measure(new Size(box.Width, double.PositiveInfinity));
                if (tb.DesiredSize.Height <= box.Height + 2) break;
            }
            var plate = new Border { Background = new SolidColorBrush(bg), Width = box.Width, MinHeight = box.Height, Child = tb, CornerRadius = new CornerRadius(2) };
            Canvas.SetLeft(plate, box.X); Canvas.SetTop(plate, box.Y);
            _layer.Children.Add(plate);
        }
    }

    /// <summary>Background = most common colour on the box outline; text = the pixels inside that differ most from it.</summary>
    private (Color Bg, Color Fg) SampleColors(Rect b)
    {
        int w = _image.PixelWidth, h = _image.PixelHeight;
        int x0 = Math.Clamp((int)b.Left - 2, 0, w - 1), y0 = Math.Clamp((int)b.Top - 2, 0, h - 1);
        int x1 = Math.Clamp((int)b.Right + 2, 0, w - 1), y1 = Math.Clamp((int)b.Bottom + 2, 0, h - 1);
        Color Px(int x, int y) { int i = y * _stride + x * 4; return Color.FromRgb(_px[i + 2], _px[i + 1], _px[i]); }

        var counts = new Dictionary<int, (int N, int R, int G, int B)>();
        void Add(Color c)
        {
            int key = (c.R >> 3) << 10 | (c.G >> 3) << 5 | c.B >> 3;
            counts.TryGetValue(key, out var v);
            counts[key] = (v.N + 1, v.R + c.R, v.G + c.G, v.B + c.B);
        }
        for (int x = x0; x <= x1; x++) { Add(Px(x, y0)); Add(Px(x, y1)); }
        for (int y = y0; y <= y1; y++) { Add(Px(x0, y)); Add(Px(x1, y)); }
        var top = counts.Values.MaxBy(v => v.N);
        var bg = Color.FromRgb((byte)(top.R / top.N), (byte)(top.G / top.N), (byte)(top.B / top.N));

        static double Lum(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        double bl = Lum(bg);
        var inside = new List<Color>();
        int step = Math.Max(1, (int)(b.Width * b.Height / 4000));
        for (int y = (int)b.Top; y < (int)b.Bottom && y < h; y += 1)
            for (int x = Math.Max(0, (int)b.Left); x < (int)b.Right && x < w; x += step)
                inside.Add(Px(x, y));
        var far = inside.OrderByDescending(c => Math.Abs(Lum(c) - bl)).Take(Math.Max(1, inside.Count / 12)).ToList();
        var fg = far.Count == 0 ? Colors.Black : Color.FromRgb((byte)far.Average(c => c.R), (byte)far.Average(c => c.G), (byte)far.Average(c => c.B));
        if (Math.Abs(Lum(fg) - bl) < 90) fg = bl > 128 ? Color.FromRgb(0x11, 0x18, 0x27) : Colors.White;
        return (bg, fg);
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
        Add("copy", "复制译文", Shortcut.Display(_cfg.KeyCopy), CopyTranslation);
        Add("copy", "复制原文", "", () => _host.CopyToClipboard(string.Join("\n", _items.Select(i => i.Source))));
        Add("eye", "显示 / 隐藏译文", Shortcut.Display(_cfg.KeyCompare), () => _layer.Visibility = _layer.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible);
        m.Items.Add(new Separator());
        Add("x", "关闭", "Esc", Close);
        return m;
    }

    private void CopyTranslation() => _host.CopyToClipboard(string.Join("\n", _items.Select(i => i.Text)));
}
