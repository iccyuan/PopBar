using System.Windows.Interop;
using System.Windows.Threading;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>
/// Puts text on the clipboard with delayed rendering, so we learn when the paste target actually reads it
/// instead of guessing with a fixed sleep. The owner window lives on its own thread (it must keep pumping
/// while the caller waits).
/// Clipboard monitors (password managers, clipboard tools) read every change the moment it happens and retry
/// when they get nothing. Once rendered, the data stays and the paste's own read goes unnoticed, so the text is
/// only handed to the target's processes (the window's and its child windows', which covers UWP frames), after
/// the paste key was sent, or to a reader that opened the clipboard without a window and cannot be attributed.
/// Everyone else gets nothing: borrowed text is not the user's copy and should not end up in their tools.
/// </summary>
internal static class ClipboardOffer
{
    private static readonly object Sync = new();
    private static HwndSource? _owner;
    private static string? _text;
    private static HashSet<uint> _targetPids = new();
    private static ManualResetEventSlim? _read;
    private static bool _pasteSent;

    private static readonly uint ExcludeFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint HistoryFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CloudFormat = RegisterClipboardFormat("CanUploadToCloudClipboard");

    /// <summary>Offer <paramref name="text"/> (rendered on demand) for a paste into <paramref name="target"/>; false when it could not be placed.</summary>
    public static bool Offer(string text, IntPtr target, out ManualResetEventSlim read)
    {
        read = new ManualResetEventSlim(false);
        var owner = Owner();
        if (owner == null || !OpenClipboard(owner.Handle)) return false;
        try
        {
            var pids = ProcessesOf(target);
            lock (Sync) { _text = text; _targetPids = pids; _read = read; _pasteSent = false; }
            EmptyClipboard(); // ownership → our window; the previous owner gets WM_DESTROYCLIPBOARD
            SetClipboardData(CF_UNICODETEXT, IntPtr.Zero);
            // Borrowed text is not the user's copy: keep it out of Win+V history, cloud sync and clipboard monitors
            // (those would otherwise also be the first to read it).
            Mark(ExcludeFormat, []);
            Mark(HistoryFormat, BitConverter.GetBytes(0));
            Mark(CloudFormat, BitConverter.GetBytes(0));
            return true;
        }
        catch (Exception ex) { Log.Error("clipboard offer failed", ex); Withdraw(); return false; }
        finally { CloseClipboard(); }
    }

    /// <summary>Call right before sending the paste key: reads from now on may be the paste.</summary>
    public static void PasteSent()
    {
        lock (Sync) _pasteSent = true;
    }

    /// <summary>Forget the offered text (later render requests get nothing); call before restoring the user's clipboard.</summary>
    public static void Withdraw()
    {
        lock (Sync) { _text = null; _read = null; }
    }

    private static void Mark(uint format, byte[] data)
    {
        if (format == 0) return;
        IntPtr h = ClipboardBackup.AllocBytes(data);
        if (h != IntPtr.Zero && SetClipboardData(format, h) == IntPtr.Zero) GlobalFree(h);
    }

    private static HwndSource? Owner()
    {
        lock (Sync)
        {
            if (_owner != null) return _owner;
            using var ready = new ManualResetEventSlim(false);
            var t = new Thread(() =>
            {
                try
                {
                    _owner = new HwndSource(new HwndSourceParameters("PopBar.Clipboard") { WindowStyle = 0, Width = 0, Height = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
                    _owner.AddHook(WndProc);
                }
                catch (Exception ex) { Log.Error("clipboard owner window failed", ex); }
                finally { ready.Set(); }
                if (_owner != null) Dispatcher.Run();
            }) { IsBackground = true, Name = "PopBar clipboard owner" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.Wait(2000);
            return _owner;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_RENDERFORMAT when (uint)wParam == CF_UNICODETEXT:
                // The reader holds the clipboard open; hand over the data, or nothing to anyone but the paste (see above).
                bool sent;
                lock (Sync) sent = _pasteSent;
                if (sent && ReaderIsTarget()) Render();
                handled = true;
                break;
            case WM_RENDERALLFORMATS:
                // Our window is going away while still owning a delayed format: render it for good.
                if (OpenClipboard(hwnd))
                {
                    try { if (GetClipboardOwner() == hwnd) Render(countsAsRead: false); }
                    finally { CloseClipboard(); }
                }
                handled = true;
                break;
            case WM_DESTROYCLIPBOARD:
                Withdraw();
                break;
        }
        return IntPtr.Zero;
    }

    private static void Render(bool countsAsRead = true)
    {
        string? text;
        ManualResetEventSlim? read;
        lock (Sync) { text = _text; read = _read; }
        if (text == null) return;
        IntPtr h = ClipboardBackup.AllocText(text);
        if (h != IntPtr.Zero && SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) GlobalFree(h);
        if (countsAsRead) read?.Set();
    }

    private static bool ReaderIsTarget()
    {
        IntPtr reader = GetOpenClipboardWindow();
        if (reader == IntPtr.Zero) return true; // opened with no window: cannot tell, assume it is the paste
        GetWindowThreadProcessId(reader, out uint pid);
        lock (Sync) return _targetPids.Count == 0 || _targetPids.Contains(pid);
    }

    /// <summary>The processes behind a window: its own and its child windows' (a UWP frame hosts the app's window).</summary>
    private static HashSet<uint> ProcessesOf(IntPtr window)
    {
        var pids = new HashSet<uint>();
        if (window == IntPtr.Zero) return pids;
        GetWindowThreadProcessId(window, out uint pid);
        pids.Add(pid);
        EnumChildWindows(window, (h, _) => { GetWindowThreadProcessId(h, out uint p); pids.Add(p); return true; }, IntPtr.Zero);
        pids.Remove(0);
        return pids;
    }
}
