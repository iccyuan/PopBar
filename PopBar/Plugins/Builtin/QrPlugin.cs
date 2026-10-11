using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PopBar.Sdk;
using PopBar.Sdk.Ui;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ErrorCorrectionLevel = ZXing.QrCode.Internal.ErrorCorrectionLevel;

namespace PopBar.Plugins.Builtin;

/// <summary>QR codes both ways: turn the selection into a code, or read codes from a screen region / the whole screen.</summary>
public sealed class QrPlugin : IPopBarPlugin
{
    public sealed class Settings
    {
        public string ScanHotkey { get; set; } = "Ctrl+Alt+Q";
    }

    private IPluginHost _host = null!;
    private Settings _cfg = new();
    private int _scanning;

    public string Id => "qr";
    public string Name => "二维码";
    public string Description => "把选中内容生成二维码；框选或整屏扫描识别二维码 / 条形码，识别结果直接弹出操作条。";
    public Version Version => new(0, 1, 0);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
        host.AddTrayMenuItem("scan-region", "识别二维码（框选）", () => _ = ScanRegionAsync());
        host.AddTrayMenuItem("scan-screen", "扫描屏幕二维码", () => _ = ScanScreenAsync());
        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        _host.UnregisterHotkey("scan");
        if (!string.IsNullOrWhiteSpace(_cfg.ScanHotkey) && !_host.RegisterHotkey("scan", _cfg.ScanHotkey, () => _ = ScanRegionAsync()))
            _host.Log.Warn($"hotkey {_cfg.ScanHotkey} unavailable");
    }

    public IReadOnlyList<PluginAction> GetActions() => new[]
    {
        new PluginAction
        {
            Id = "make", Label = "二维码", Icon = "qr-code",
            Description = "把选中内容生成二维码",
            EnabledByDefault = false,
            IsApplicable = ctx => ctx.Text.Trim().Length is > 0 and <= 1200,
            Execute = (ctx, _) => { ShowCode(ctx); return Task.CompletedTask; },
        },
    };

    // ------------------------------------------------------------------ generate

    private void ShowCode(SelectionContext ctx)
    {
        var text = ctx.Text.Trim();
        var panel = _host.ShowPanel(new PanelOptions
        {
            Title = "二维码", Icon = "qr-code", AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY,
            Width = 320, ShowTextArea = false,
        });
        BitmapSource? code = null;
        panel.SetContent(() =>
        {
            code = Render(text, 480);
            var img = new Image { Source = code, Width = 240, Height = 240, Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var plate = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, Child = img };
            var caption = new TextBlock
            {
                Text = text.Length > 80 ? text[..80] + "…" : text, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.7, FontSize = 12, Margin = new Thickness(8, 8, 8, 0),
            };
            return new StackPanel { Children = { plate, caption } };
        });
        panel.SetStatus($"{System.Text.Encoding.UTF8.GetByteCount(text)} 字节 · UTF-8");
        panel.AddButton("复制图片", _ => { if (code != null) Clipboard.SetImage(code); }, "copy");
        panel.AddButton("保存", _ => { if (code != null) Save(code); }, "download");
    }

    /// <summary>Crisp code at about <paramref name="size"/> px (ZXing scales modules by whole pixels).</summary>
    private static BitmapSource Render(string text, int size)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions { Width = size, Height = size, Margin = 1, CharacterSet = "UTF-8", ErrorCorrection = ErrorCorrectionLevel.M },
        };
        var pd = writer.Write(text);
        var bmp = BitmapSource.Create(pd.Width, pd.Height, 96, 96, PixelFormats.Bgra32, null, pd.Pixels, pd.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    private void Save(BitmapSource bmp)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"QR_{DateTime.Now:yyyyMMdd_HHmmss}", DefaultExt = ".png", Filter = "PNG 图片 (*.png)|*.png" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(dlg.FileName);
            enc.Save(fs);
        }
        catch (Exception ex) { _host.Log.Error("save qr", ex); }
    }

    // ------------------------------------------------------------------ scan

    public async Task ScanRegionAsync()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            var region = await _host.CaptureScreenRegionAsync();
            if (region == null) return;
            var (px, w, h) = await Task.Run(() => DecodePng(region.Png));
            Report(await Task.Run(() => Decode(px, w, h)), region.X + region.Width / 2, region.Y + region.Height);
        }
        catch (Exception ex) { _host.Log.Error("qr scan", ex); }
        finally { Interlocked.Exchange(ref _scanning, 0); }
    }

    public async Task ScanScreenAsync()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            var (px, w, h) = await Task.Run(() => CaptureScreen(vs));
            var found = await Task.Run(() => Decode(px, w, h));
            var cur = System.Windows.Forms.Cursor.Position;
            Report(found, cur.X, cur.Y);
        }
        catch (Exception ex) { _host.Log.Error("qr screen scan", ex); }
        finally { Interlocked.Exchange(ref _scanning, 0); }
    }

    private void Report(IReadOnlyList<Result> found, int x, int y)
    {
        if (found.Count == 0)
        {
            var p = _host.ShowPanel(new PanelOptions { Title = "二维码", Icon = "qr-code", AnchorX = x, AnchorY = y });
            p.SetText("没有找到二维码或条形码。");
            p.SetStatus("试试把码放大一些，或框选得更贴近");
            return;
        }
        _host.Log.Info($"decoded {found.Count} code(s)");
        var text = string.Join("\n", found.Select(r => r.Text));
        var panel = _host.ShowPanel(new PanelOptions { Title = found.Count > 1 ? $"识别到 {found.Count} 个码" : "识别结果", Icon = "qr-code", AnchorX = x, AnchorY = y + 40, Editable = true });
        panel.SetText(text);
        panel.SetStatus(string.Join(" · ", found.Select(r => r.BarcodeFormat.ToString()).Distinct()));
        panel.AddButton("复制", t => _host.CopyToClipboard(t), "copy");
        // The standard bar offers open-link / search / translate etc. for the decoded text.
        _host.ShowPopup(new SelectionContext(found[0].Text, IntPtr.Zero, "", x, y + 6));
    }

    private static IReadOnlyList<Result> Decode(byte[] bgra, int w, int h)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true, CharacterSet = "UTF-8" },
        };
        var src = new RGBLuminanceSource(bgra, w, h, RGBLuminanceSource.BitmapFormat.BGRA32);
        return reader.DecodeMultiple(src)?.Where(r => !string.IsNullOrEmpty(r.Text)).GroupBy(r => r.Text).Select(g => g.First()).ToList()
               ?? (IReadOnlyList<Result>)Array.Empty<Result>();
    }

    private static (byte[] Px, int W, int H) DecodePng(byte[] png)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }

    private static (byte[] Px, int W, int H) CaptureScreen(System.Drawing.Rectangle vs)
    {
        using var bmp = new System.Drawing.Bitmap(vs.Width, vs.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(vs.X, vs.Y, 0, 0, bmp.Size);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, vs.Width, vs.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[vs.Width * vs.Height * 4];
            for (int y = 0; y < vs.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * vs.Width * 4, vs.Width * 4);
            return (px, vs.Width, vs.Height);
        }
        finally { bmp.UnlockBits(data); }
    }

    // ------------------------------------------------------------------ settings

    public FrameworkElement? CreateSettingsView() => Form.Stack(
        Form.Card(
            Form.Row("识别快捷键", _host.Ui.HotkeyBox(_cfg.ScanHotkey, v => { _cfg.ScanHotkey = v; _host.Storage.Set(_cfg); RegisterHotkey(); }), "框选屏幕区域识别；托盘菜单里还有「扫描屏幕二维码」，无需框选")),
        Form.Hint("生成：选中文字后点操作条上的「二维码」。识别支持二维码、条形码、Data Matrix 等常见格式。"));

    public void Dispose() { }
}
