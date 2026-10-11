using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Windows;
using PopBar.Sdk;
using PopBar.Sdk.Ui;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using OcrResult = PopBar.Sdk.OcrResult;
using OcrLine = PopBar.Sdk.OcrLine;

namespace PopBar.Plugins.Builtin;

/// <summary>Screen OCR with the Windows built-in engine: hotkey / tray → drag a region → text goes to the bar + a panel.</summary>
public sealed class OcrPlugin : IPopBarPlugin, IOcrEngine
{
    public sealed class Settings
    {
        public string Hotkey { get; set; } = "Ctrl+Alt+O";
        /// <summary>BCP-47 tag or empty for the user profile languages.</summary>
        public string Language { get; set; } = "";
        public bool ShowPanel { get; set; } = true;
        public bool JoinLines { get; set; } = true;
    }

    private IPluginHost _host = null!;
    private Settings _cfg = new();
    private int _running;

    public string Id => "ocr";
    public string Name => "屏幕 OCR";
    public string Description => "框选屏幕任意区域识别文字（Windows 内置 OCR，离线），识别结果直接弹出操作条。";
    public Version Version => new(0, 2, 0);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
        host.Services.Provide<IOcrEngine>(this);
        host.AddTrayMenuItem("capture", "屏幕 OCR 取词", () => _ = CaptureAsync(), "type");
        if (!string.IsNullOrWhiteSpace(_cfg.Hotkey) && !host.RegisterHotkey("capture", _cfg.Hotkey, () => _ = CaptureAsync()))
            host.Log.Warn($"hotkey {_cfg.Hotkey} unavailable");
    }

    /// <summary>OCR is not selection driven, so it contributes no bar buttons.</summary>
    public IReadOnlyList<PluginAction> GetActions() => Array.Empty<PluginAction>();

    public async Task CaptureAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            _host.Log.Info("capture started");
            var region = await _host.CaptureScreenRegionAsync();
            if (region == null) { _host.Log.Info("capture cancelled"); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { Directory.CreateDirectory(_host.DataDir); File.WriteAllBytes(Path.Combine(_host.DataDir, "last-capture.png"), region.Png); } catch { }
            var text = (await RecognizeAsync(region.Png)).Text;
            _host.Log.Info($"recognized {text.Length} chars from {region.Width}x{region.Height} in {sw.ElapsedMilliseconds}ms");
            if (string.IsNullOrWhiteSpace(text))
            {
                var p = _host.ShowPanel(new PanelOptions { Title = "OCR", Icon = "type", AnchorX = region.X + region.Width / 2, AnchorY = region.Y + region.Height });
                p.SetText("没有识别到文字。");
                p.SetStatus("试试放大区域，或在设置里指定识别语言");
                return;
            }
            var ctx = new SelectionContext(text, IntPtr.Zero, "", region.X + region.Width / 2, region.Y + region.Height + 6);
            if (_cfg.ShowPanel)
            {
                var panel = _host.ShowPanel(new PanelOptions
                {
                    Title = "OCR 识别结果", Icon = "type", AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY + 40, Editable = true,
                });
                panel.SetText(text);
                panel.SetStatus($"{text.Length} 字符 · 可直接编辑");
                panel.AddButton("复制", t => _host.CopyToClipboard(t));
                panel.AddButton("翻译", t => _ = _host.RunActionAsync("translate:panel", ctx with { Text = t, ScreenY = ctx.ScreenY + 60 }));
            }
            _host.ShowPopup(ctx);
        }
        catch (Exception ex) { _host.Log.Error("ocr failed", ex); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    /// <summary>Recognize text; line boxes are mapped back to <paramref name="png"/>'s own pixel coordinates.</summary>
    public async Task<OcrResult> RecognizeAsync(byte[] png, CancellationToken ct = default)
    {
        OcrEngine? engine = null;
        if (!string.IsNullOrWhiteSpace(_cfg.Language))
        {
            try { engine = OcrEngine.TryCreateFromLanguage(new Language(_cfg.Language)); } catch { }
        }
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) throw new InvalidOperationException("系统没有可用的 OCR 语言包（设置 → 时间和语言 → 语言 → 添加语言并安装“光学字符识别”）。");
        _host.Log.Info($"engine language {engine.RecognizerLanguage.LanguageTag}; available: {string.Join(",", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag))}");

        (png, double scale, int pad) = Preprocess(png);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        ct.ThrowIfCancellationRequested();

        var sb = new StringBuilder();
        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            // CJK engines emit one "word" per character: join those without spaces, but keep
            // spaces between Latin words so English inside a Chinese page stays readable.
            string? prev = null;
            var lineText = new StringBuilder();
            foreach (var w in line.Words)
            {
                var t = w.Text;
                if (t.Length == 0) continue;
                if (prev != null && !(IsCjk(prev[^1]) && IsCjk(t[0]))) lineText.Append(' ');
                lineText.Append(t);
                prev = t;
            }
            var s = lineText.ToString();
            var box = line.Words.Select(w => w.BoundingRect)
                .Aggregate(Rect.Empty, (acc, r) => Rect.Union(acc, new Rect(r.X, r.Y, r.Width, r.Height)));
            if (!box.IsEmpty)
                lines.Add(new OcrLine(s, new Rect((box.X - pad) / scale, (box.Y - pad) / scale, box.Width / scale, box.Height / scale)));
            sb.Append(s);
            bool lineIsCjk = s.Count(IsCjk) > s.Length / 3;
            sb.Append(_cfg.JoinLines && !lineIsCjk ? ' ' : '\n');
        }
        return new OcrResult(sb.ToString().Trim(), lines);
    }

    private static bool IsCjk(char c) => (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x3000 && c <= 0x303F) || (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0x3040 && c <= 0x30FF);

    /// <summary>
    /// The Windows engine returns nothing for thin strips of small text. Give it a white margin and
    /// upscale small captures (bicubic) so the text is at least ~20px tall.
    /// </summary>
    private static (byte[] Png, double Scale, int Pad) Preprocess(byte[] png)
    {
        using var src = System.Drawing.Image.FromStream(new MemoryStream(png));
        const int pad = 32;
        const int minHeight = 160;
        double scale = Math.Clamp(Math.Ceiling((double)minHeight / src.Height), 1, 3);
        int w = (int)(src.Width * scale), h = (int)(src.Height * scale);
        if (scale == 1 && w > 2400) return (png, 1, 0); // already big: leave as is (engine limit is 2600px)
        using var dst = new System.Drawing.Bitmap(w + 2 * pad, h + 2 * pad);
        using (var g = System.Drawing.Graphics.FromImage(dst))
        {
            g.Clear(System.Drawing.Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, pad, pad, w, h);
        }
        using var ms = new MemoryStream();
        dst.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return (ms.ToArray(), scale, pad);
    }

    // ------------------------------------------------------------------ settings

    public FrameworkElement? CreateSettingsView()
    {
        // Edit a draft; it only takes effect when the settings window is saved.
        var d = SettingsDraft.Clone(_cfg);
        void Changed() => _host.Settings.Changed();
        _host.Settings.OnApply(() =>
        {
            _cfg = SettingsDraft.Clone(d);
            _host.Storage.Set(_cfg);
            _host.UnregisterHotkey("capture");
            if (_cfg.Hotkey.Length > 0 && !_host.RegisterHotkey("capture", _cfg.Hotkey, () => _ = CaptureAsync()))
                _host.Log.Warn($"hotkey {_cfg.Hotkey} unavailable");
        });
        return Form.Stack(
            Form.Card(
                Form.Row("快捷键", _host.Ui.HotkeyBox(d.Hotkey, v =>
                {
                    d.Hotkey = v; Changed();
                }), "点击后按下组合键；托盘菜单里也有入口"),
                Form.Row("识别语言", Form.TextBox(d.Language, 160, v => { d.Language = v.Trim(); Changed(); }), "留空按系统语言；可填 zh-Hans-CN、en-US、ja 等（需已安装对应语言包）"),
                Form.Row("显示结果面板", Form.Toggle(d.ShowPanel, v => { d.ShowPanel = v; Changed(); }), "关闭后只弹出操作条"),
                Form.Row("英文按行合并为段落", Form.Toggle(d.JoinLines, v => { d.JoinLines = v; Changed(); }))
            ),
            Form.Hint("可用语言：" + string.Join("、", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag))));
    }

    public void Dispose() { }
}
