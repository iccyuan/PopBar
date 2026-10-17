using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PopBar.Sdk;

namespace PopBar.Plugins.Builtin.Screenshot;

/// <summary>Records a screen region to an animated GIF: capture thread → bounded queue → streaming encoder.</summary>
internal sealed class GifRecorder
{
    private readonly IPluginHost _host;
    private readonly Int32Rect _r;
    private readonly ScreenshotPlugin.Settings _cfg;
    private readonly string _path;
    private readonly BlockingCollection<(byte[] Px, long Ms)> _frames = new(boundedCapacity: 48);
    private readonly Stopwatch _clock = new();
    private RegionChrome _chrome = null!;
    private volatile bool _stop, _cancel, _paused;
    private int _captured;

    public GifRecorder(IPluginHost host, Int32Rect r, ScreenshotPlugin.Settings cfg, string dir)
    {
        _host = host; _r = r; _cfg = cfg;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"PopBar_{DateTime.Now:yyyyMMdd_HHmmss}.gif");
    }

    public void Start()
    {
        _chrome = new RegionChrome(_host, _r, "完成", canPause: true);
        _chrome.Done += () => _stop = true;
        _chrome.Cancelled += () => { _cancel = true; _stop = true; };
        _chrome.PauseToggled += () => { _paused = !_paused; if (_paused) _clock.Stop(); else _clock.Start(); _chrome.SetPaused(_paused); };
        _chrome.SetStatus("准备录制…");
        _chrome.Show();
        _clock.Start();

        var encode = Task.Run(Encode);
        new Thread(() => CaptureLoop(encode)) { IsBackground = true, Name = "PopBar GIF capture" }.Start();
    }

    private void CaptureLoop(Task<long> encode)
    {
        int fps = Math.Clamp(_cfg.GifFps, 5, 30);
        long maxMs = Math.Clamp(_cfg.GifMaxSeconds, 5, 600) * 1000L;
        long next = 0;
        while (!_stop && _clock.ElapsedMilliseconds < maxMs)
        {
            long now = _clock.ElapsedMilliseconds;
            if (!_paused && now >= next)
            {
                try { _frames.TryAdd((ShotNative.CaptureBgra(_r, _cfg.GifCursor), now), 200); _captured++; }
                catch (Exception ex) { _host.Log.Error("gif capture", ex); break; }
                next = now + 1000 / fps;
                if (_captured % 3 == 0)
                {
                    var text = $"{now / 60000:00}:{now / 1000 % 60:00}.{now / 100 % 10}  ·  {_captured} 帧";
                    _host.Dispatcher.InvokeAsync(() => _chrome.SetStatus(text));
                }
            }
            Thread.Sleep(5);
        }
        long end = _clock.ElapsedMilliseconds;
        _frames.CompleteAdding();
        _host.Dispatcher.InvokeAsync(() => _chrome.SetStatus(_cancel ? "已取消" : "正在生成 GIF…"));
        long size = encode.GetAwaiter().GetResult();
        _host.Dispatcher.InvokeAsync(() => Finish(end, size));
    }

    private long Encode()
    {
        using var fs = File.Create(_path);
        var enc = new GifEncoder(fs, _r.Width, _r.Height);
        foreach (var (px, ms) in _frames.GetConsumingEnumerable())
            if (!_cancel) enc.AddFrame(px, ms);
        if (!_cancel) enc.Finish(_clock.ElapsedMilliseconds);
        fs.Flush();
        return fs.Length;
    }

    private void Finish(long durationMs, long size)
    {
        _chrome.Close();
        if (_cancel) { try { File.Delete(_path); } catch { } return; }
        _host.Log.Info($"gif {_r.Width}x{_r.Height} {_captured} frames {durationMs}ms {size / 1024}KB");
        ShowResult(_host, _path, _r, $"GIF · {durationMs / 1000.0:0.0} 秒 · {_captured} 帧 · {Size(size)}", null);
    }

    internal static string Size(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

    /// <summary>Result panel shared by GIF and long screenshots: file is on the clipboard (as a file), with open / folder buttons.</summary>
    internal static void ShowResult(IPluginHost host, string path, Int32Rect r, string status, BitmapSource? preview)
    {
        try
        {
            var files = new System.Collections.Specialized.StringCollection { path };
            var data = new DataObject();
            data.SetFileDropList(files);
            if (preview != null) data.SetImage(preview);
            Clipboard.SetDataObject(data, true);
        }
        catch (Exception ex) { host.Log.Error("clipboard file", ex); }

        var panel = host.ShowPanel(new PanelOptions
        {
            Title = preview != null ? "长截图已完成" : "GIF 已保存", Icon = preview != null ? "scroll-capture" : "film",
            AnchorX = r.X + r.Width / 2, AnchorY = r.Y + r.Height, Width = 380, ShowTextArea = preview == null, Pinned = true,
        });
        if (preview != null)
            panel.SetContent(() =>
            {
                var img = new System.Windows.Controls.Image { Source = preview, Stretch = Stretch.Uniform, Width = 340, HorizontalAlignment = HorizontalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                return new System.Windows.Controls.Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = img };
            });
        else panel.SetText(path);
        panel.SetStatus(status + " · 已复制");
        panel.AddButton("打开", _ => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }), "external-link");
        panel.AddButton("所在文件夹", _ => Process.Start("explorer.exe", $"/select,\"{path}\""));
    }
}

/// <summary>Scrolling ("long") screenshot: wheel the window under the region, stitch each new strip, until it stops moving.</summary>
internal sealed class ScrollCapture
{
    private readonly IPluginHost _host;
    private readonly Int32Rect _r;
    private readonly string _dir;
    private RegionChrome _chrome = null!;
    private volatile bool _stop, _cancel;

    public ScrollCapture(IPluginHost host, Int32Rect r, string dir) { _host = host; _r = r; _dir = dir; }

    public void Start()
    {
        _chrome = new RegionChrome(_host, _r, "完成", canPause: false);
        _chrome.Done += () => _stop = true;
        _chrome.Cancelled += () => { _cancel = true; _stop = true; };
        _chrome.SetStatus("正在滚动拼接，请勿移动鼠标…");
        _chrome.Show();
        new Thread(Run) { IsBackground = true, Name = "PopBar scroll capture" }.Start();
    }

    private void Run()
    {
        ShotNative.GetCursorPos(out var home);
        var st = new Stitcher(_r.Width, _r.Height, excludeRight: Math.Min(_r.Width / 6, 32));
        try
        {
            Thread.Sleep(250); // let the chrome appear and the overlay vanish
            st.Start(ShotNative.CaptureBgra(_r, false));
            int notches = 3, stuck = 0;
            int cx = _r.X + _r.Width / 2, cy = _r.Y + _r.Height / 2;
            while (!_stop && st.Height < Stitcher.MaxHeight)
            {
                ShotNative.SetCursorPos(cx, cy);
                ShotNative.SendWheel(-120 * notches);
                Thread.Sleep(420);
                var frame = Stable();
                int shift = st.Append(frame);
                if (shift <= 0) { if (++stuck >= 3) break; continue; }
                stuck = 0;
                // Aim each step at ~60% of the visible content so consecutive frames still overlap.
                notches = Math.Clamp((int)Math.Round(notches * st.LastContentHeight * 0.6 / shift), 1, 15);
                int h = st.Height;
                _host.Dispatcher.InvokeAsync(() => _chrome.SetStatus($"{_r.Width} × {h}  ·  点「完成」随时结束"));
            }
        }
        catch (Exception ex) { _host.Log.Error("scroll capture", ex); }
        finally { ShotNative.SetCursorPos(home.X, home.Y); }

        if (_cancel) { _host.Dispatcher.InvokeAsync(_chrome.Close); return; }
        var (px, w, h2) = st.Build();
        _host.Dispatcher.InvokeAsync(() => Finish(px, w, h2));
    }

    /// <summary>Wait for smooth scrolling to settle: two equal captures in a row (or give up after a few tries).</summary>
    private byte[] Stable()
    {
        var a = ShotNative.CaptureBgra(_r, false);
        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(120);
            var b = ShotNative.CaptureBgra(_r, false);
            if (a.AsSpan().SequenceEqual(b)) return b;
            a = b;
        }
        return a;
    }

    private void Finish(byte[] px, int w, int h)
    {
        _chrome.Close();
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"PopBar_{DateTime.Now:yyyyMMdd_HHmmss}_long.png");
        long size;
        using (var fs = File.Create(path))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(fs);
            size = fs.Length;
        }
        _host.Log.Info($"long screenshot {w}x{h} {size / 1024}KB");
        GifRecorder.ShowResult(_host, path, _r, $"{w} × {h} · {GifRecorder.Size(size)}", bmp);
    }
}

/// <summary>
/// Stitches successive captures of a scrolling region. Rows are compared by hash; rows identical at the same
/// position in two frames are sticky header / footer and are kept only once. The scrollbar strip on the right is
/// ignored. Uniform (blank) rows carry no information, so the shift is chosen on rows with content.
/// </summary>
internal sealed class Stitcher
{
    public const int MaxHeight = 30000;

    private readonly int _w, _h, _stride, _cmpBytes;
    private byte[] _canvas = Array.Empty<byte>();
    private int _rows;
    private byte[] _prev = Array.Empty<byte>();
    private ulong[] _prevHash = Array.Empty<ulong>();
    private bool[] _prevInfo = Array.Empty<bool>();

    public int Height => _rows;
    public int LastContentHeight { get; private set; }

    public Stitcher(int w, int h, int excludeRight)
    {
        _w = w; _h = h; _stride = w * 4; _cmpBytes = Math.Max(4, (w - excludeRight) * 4);
        LastContentHeight = h;
    }

    public void Start(byte[] frame)
    {
        _canvas = new byte[_stride * _h * 4];
        AppendRows(frame, 0, _h);
        (_prev, _prevHash, _prevInfo) = (frame, Hashes(frame), Info(frame));
    }

    /// <summary>Adds the new part of <paramref name="cur"/>; returns the scroll distance in rows, 0 = no movement, -1 = no reliable match.</summary>
    public int Append(byte[] cur)
    {
        var hash = Hashes(cur);
        var info = Info(cur);
        int top = 0;
        while (top < _h && hash[top] == _prevHash[top]) top++;
        if (top == _h) return 0;
        int foot = 0;
        while (foot < _h - top && hash[_h - 1 - foot] == _prevHash[_h - 1 - foot]) foot++;

        int lo = top, hi = _h - foot, span = hi - lo;
        LastContentHeight = span;
        int best = -1; double bestScore = 0;
        for (int s = 1; s < span - 4; s++)
        {
            int match = 0, total = 0;
            for (int i = 0; i < span - s; i++)
            {
                if (!_prevInfo[lo + s + i]) continue;
                total++;
                if (_prevHash[lo + s + i] == hash[lo + i]) match++;
            }
            if (total < 6) continue;
            double score = (double)match / total;
            if (score > bestScore + 1e-9) { bestScore = score; best = s; }
            if (score >= 0.999) break; // smallest exact shift wins
        }
        // No trustworthy overlap: keep comparing against the last good frame rather than leave a gap.
        if (best < 0 || bestScore < 0.9) return -1;

        _rows -= foot;                              // drop the previous frame's footer...
        AppendRows(cur, hi - best, best);           // ...append the newly revealed strip...
        AppendRows(cur, hi, foot);                  // ...and put the footer back at the end.
        (_prev, _prevHash, _prevInfo) = (cur, hash, info);
        return best;
    }

    public (byte[] Px, int W, int H) Build()
    {
        var px = new byte[_rows * _stride];
        Buffer.BlockCopy(_canvas, 0, px, 0, px.Length);
        return (px, _w, _rows);
    }

    private void AppendRows(byte[] src, int fromRow, int count)
    {
        if (count <= 0) return;
        int need = (_rows + count) * _stride;
        if (need > _canvas.Length) Array.Resize(ref _canvas, Math.Max(need, _canvas.Length * 2));
        Buffer.BlockCopy(src, fromRow * _stride, _canvas, _rows * _stride, count * _stride);
        _rows += count;
    }

    private ulong[] Hashes(byte[] f)
    {
        var h = new ulong[_h];
        for (int y = 0; y < _h; y++)
        {
            ulong x = 1469598103934665603UL;
            var row = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(f.AsSpan(y * _stride, _cmpBytes & ~7));
            foreach (var v in row) { x ^= v; x *= 1099511628211UL; }
            h[y] = x;
        }
        return h;
    }

    private bool[] Info(byte[] f)
    {
        var r = new bool[_h];
        for (int y = 0; y < _h; y++)
        {
            int o = y * _stride;
            uint first = BitConverter.ToUInt32(f, o);
            for (int x = 8; x < _cmpBytes; x += 8)
                if (BitConverter.ToUInt32(f, o + x) != first) { r[y] = true; break; }
        }
        return r;
    }
}
