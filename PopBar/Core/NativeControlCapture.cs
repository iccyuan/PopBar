using System.Runtime.InteropServices;
using System.Text;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>
/// Reads the selection straight out of well-known Win32 controls with their own messages: Scintilla (Notepad++, SciTE,
/// many code editors - no UIA text support at all), Edit and RichEdit (also covered by UIA's proxies; this is exact,
/// quicker, and still works when UIA times out). No input is synthesized and the clipboard is never touched.
/// </summary>
internal sealed class NativeControlCaptureStrategy : ITextCaptureStrategy
{
    /// <summary>Per-message limit: a hung target must not stall the capture.</summary>
    private const uint TimeoutMs = 250;
    /// <summary>Selections longer than this are not read (the engine drops them anyway); settles without text.</summary>
    private const int MaxChars = 1_000_000;

    /// <summary>Written into out-parameters first: still there afterwards means the control ignored the message.</summary>
    private const int Unset = int.MinValue;

    private const int ES_PASSWORD = 0x0020;
    private const int EM_GETSEL = 0x00B0;
    private const int EM_EXGETSEL = 0x0400 + 52;
    private const int EM_GETSELTEXT = 0x0400 + 62;

    private const int SCI_GETCODEPAGE = 2137;
    private const int SCI_GETSELECTIONSTART = 2143, SCI_GETSELECTIONEND = 2145;
    private const int SCI_POINTXFROMPOSITION = 2164, SCI_POINTYFROMPOSITION = 2165, SCI_LINEFROMPOSITION = 2166;
    private const int SCI_TEXTHEIGHT = 2279;
    private const int SCI_GETSELECTIONS = 2570, SCI_GETSELECTIONNSTART = 2585, SCI_GETSELECTIONNEND = 2587;
    private const int SCI_GETRANGEPOINTER = 2643;

    private enum Kind { None, Scintilla, Edit, RichEdit }

    public CaptureMethod Method => CaptureMethod.Native;

    public bool CanRun(CaptureContext ctx) => KindOf(ctx.FocusWindow) != Kind.None;

    public CaptureAttempt Capture(CaptureContext ctx)
    {
        var hwnd = ctx.FocusWindow;
        return KindOf(hwnd) switch
        {
            Kind.Scintilla => Scintilla(hwnd, ctx),
            Kind.Edit => IsPassword(hwnd) ? CaptureAttempt.NoSelection : Edit(hwnd),
            Kind.RichEdit => IsPassword(hwnd) ? CaptureAttempt.NoSelection : RichEdit(hwnd),
            _ => CaptureAttempt.Unsupported,
        };
    }

    private static Kind KindOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return Kind.None;
        var cls = GetWindowClass(hwnd);
        if (cls.Contains("Scintilla", StringComparison.OrdinalIgnoreCase)) return Kind.Scintilla;
        // RichEdit20W / RICHEDIT50W / RichEditD2DPT (Notepad on Windows 11), WinForms' WindowsForms10.RichEdit20W..., Delphi's TRichEdit
        if (cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)) return Kind.RichEdit;
        // Edit, WinForms' WindowsForms10.EDIT.app..., Delphi / VB wrappers around the system edit control
        if (string.Equals(cls, "Edit", StringComparison.OrdinalIgnoreCase) || cls.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase)
            || cls is "TEdit" or "TMemo" or "TDBEdit" or "TDBMemo" or "TMaskEdit" or "ThunderTextBox" or "ThunderRT6TextBox") return Kind.Edit;
        return Kind.None;
    }

    private static bool IsPassword(IntPtr hwnd) => ((long)GetWindowLongPtr(hwnd, GWL_STYLE) & ES_PASSWORD) != 0;

    private static bool Send(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, out IntPtr result) =>
        SendMessageTimeout(hwnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, TimeoutMs, out result) != IntPtr.Zero;

    private static long Send(IntPtr hwnd, int msg, long wParam = 0, long lParam = 0) =>
        Send(hwnd, msg, (IntPtr)wParam, (IntPtr)lParam, out var r) ? (long)r : -1;

    // ---------------------------------------------------------------- Scintilla

    /// <summary>
    /// Scintilla hands out a pointer to its own buffer (SCI_GETRANGEPOINTER, the range made contiguous first), so the
    /// bytes are read from the target's memory; one selection or several (multi-caret / rectangular), joined by lines.
    /// </summary>
    private static CaptureAttempt Scintilla(IntPtr hwnd, CaptureContext ctx)
    {
        long n = Send(hwnd, SCI_GETSELECTIONS);
        if (n < 0) return CaptureAttempt.Unsupported;
        var ranges = new List<(long Start, long End)>();
        if (n <= 1)
        {
            long s = Send(hwnd, SCI_GETSELECTIONSTART), e = Send(hwnd, SCI_GETSELECTIONEND);
            if (s < 0 || e < 0) return CaptureAttempt.Unsupported;
            ranges.Add((s, e));
        }
        else
            for (long i = 0; i < n; i++) ranges.Add((Send(hwnd, SCI_GETSELECTIONNSTART, i), Send(hwnd, SCI_GETSELECTIONNEND, i)));
        ranges.RemoveAll(r => r.Start < 0 || r.End <= r.Start);
        if (ranges.Count == 0) return CaptureAttempt.NoSelection;
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (ranges.Sum(r => r.End - r.Start) > MaxChars * 4L) return CaptureAttempt.NoSelection;

        using var proc = RemoteProcess.Open(hwnd, PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION);
        if (proc == null) return CaptureAttempt.Unsupported;
        var enc = ScintillaEncoding(Send(hwnd, SCI_GETCODEPAGE));
        var sb = new StringBuilder();
        foreach (var (s, e) in ranges)
        {
            long ptr = Send(hwnd, SCI_GETRANGEPOINTER, s, e - s);
            if (proc.IsWow64) ptr &= 0xFFFFFFFF;
            if (ptr <= 0) return CaptureAttempt.Unsupported;
            var bytes = proc.Read((IntPtr)ptr, (int)(e - s));
            if (bytes == null) return CaptureAttempt.Unsupported;
            if (sb.Length > 0) sb.Append("\r\n");
            sb.Append(enc.GetString(bytes));
        }
        return CaptureAttempt.From(sb.ToString(), TextBounds.Plausible(ScintillaBounds(hwnd, ranges[0].Start, ranges[^1].End), ctx), settled: true);
    }

    private static Encoding ScintillaEncoding(long codePage)
    {
        if (codePage == 65001) return Encoding.UTF8;
        try { return CodePagesEncodingProvider.Instance.GetEncoding(codePage > 0 ? (int)codePage : (int)GetACP()) ?? Encoding.UTF8; }
        catch { return Encoding.UTF8; }
    }

    /// <summary>From the top of the first selected character's line to the bottom of the last one's, in screen pixels.</summary>
    private static RECT? ScintillaBounds(IntPtr hwnd, long start, long end)
    {
        RECT? Point(long pos)
        {
            long x = Send(hwnd, SCI_POINTXFROMPOSITION, 0, pos), y = Send(hwnd, SCI_POINTYFROMPOSITION, 0, pos);
            long h = Send(hwnd, SCI_TEXTHEIGHT, Send(hwnd, SCI_LINEFROMPOSITION, pos));
            if (h <= 0) return null;
            var p = new POINT { X = (int)x, Y = (int)y };
            if (!ClientToScreen(hwnd, ref p)) return null;
            return new RECT { Left = p.X, Top = p.Y, Right = p.X + 1, Bottom = p.Y + (int)h };
        }
        var b = TextBounds.Union(Point(start), Point(end));
        // lines between the two ends may reach further left or right: span the control's width once there are several
        bool multiLine = Send(hwnd, SCI_LINEFROMPOSITION, start) != Send(hwnd, SCI_LINEFROMPOSITION, end);
        if (b is { } r && multiLine && GetWindowRect(hwnd, out var w)) b = new RECT { Left = w.Left, Top = r.Top, Right = w.Right, Bottom = r.Bottom };
        return b;
    }

    // ---------------------------------------------------------------- Edit

    /// <summary>EM_GETSEL and WM_GETTEXT are marshalled across processes by the system for the edit class.</summary>
    private static unsafe CaptureAttempt Edit(IntPtr hwnd)
    {
        int start = Unset, end = Unset;
        if (!Send(hwnd, EM_GETSEL, (IntPtr)(&start), (IntPtr)(&end), out _) || start == Unset || end == Unset) return CaptureAttempt.Unsupported;
        if (end <= start) return CaptureAttempt.NoSelection;
        if (end - start > MaxChars) return CaptureAttempt.NoSelection;
        long len = Send(hwnd, WM_GETTEXTLENGTH);
        if (len < end) return CaptureAttempt.Unsupported;
        var buf = new char[len + 1];
        if (SendMessageTimeoutText(hwnd, WM_GETTEXT, (IntPtr)buf.Length, buf, SMTO_ABORTIFHUNG, TimeoutMs, out var got) == IntPtr.Zero) return CaptureAttempt.Unsupported;
        if ((long)got < end) return CaptureAttempt.Unsupported;
        return CaptureAttempt.From(new string(buf, start, end - start), null, settled: true);
    }

    // ---------------------------------------------------------------- RichEdit

    /// <summary>
    /// EM_EXGETSEL / EM_GETSELTEXT take pointers the system does not marshal, so both go through memory allocated in
    /// the target process. RichEdit ends paragraphs with a bare CR; those become CRLF.
    /// </summary>
    private static CaptureAttempt RichEdit(IntPtr hwnd)
    {
        using var proc = RemoteProcess.Open(hwnd, PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION);
        if (proc == null) return CaptureAttempt.Unsupported;
        var range = proc.Alloc(8); // CHARRANGE { LONG cpMin, cpMax }
        if (range == IntPtr.Zero) return CaptureAttempt.Unsupported;
        // a sentinel no real range has: a look-alike class that ignores the message must not read as "nothing selected"
        if (!proc.Write(range, [.. BitConverter.GetBytes(Unset), .. BitConverter.GetBytes(Unset)])) return CaptureAttempt.Unsupported;
        if (!Send(hwnd, EM_EXGETSEL, IntPtr.Zero, range, out _)) return CaptureAttempt.Unsupported;
        var cr = proc.Read(range, 8);
        if (cr == null) return CaptureAttempt.Unsupported;
        int min = BitConverter.ToInt32(cr, 0), max = BitConverter.ToInt32(cr, 4);
        if (min == Unset || max == Unset) return CaptureAttempt.Unsupported;
        if (max < 0) max = (int)Math.Min(Send(hwnd, WM_GETTEXTLENGTH), int.MaxValue); // -1 = to the end
        if (max <= min) return CaptureAttempt.NoSelection;
        if (max - min > MaxChars) return CaptureAttempt.NoSelection;

        // EM_GETSELTEXT copies TCHARs: UTF-16 units, or for an ANSI window bytes (up to two per character)
        bool unicode = IsWindowUnicode(hwnd);
        int capacity = (max - min + 1) * 2;
        var buf = proc.Alloc(capacity);
        if (buf == IntPtr.Zero) return CaptureAttempt.Unsupported;
        // a selection was reported, so no text back means the control does not really speak EM_GETSELTEXT
        if (!Send(hwnd, EM_GETSELTEXT, IntPtr.Zero, buf, out var copied) || (long)copied <= 0) return CaptureAttempt.Unsupported;
        int bytes = (int)Math.Min((long)copied * (unicode ? 2 : 1), capacity);
        var raw = proc.Read(buf, bytes);
        if (raw == null) return CaptureAttempt.Unsupported;
        string text = unicode
            ? Encoding.Unicode.GetString(raw)
            : (CodePagesEncodingProvider.Instance.GetEncoding((int)GetACP()) ?? Encoding.UTF8).GetString(raw);
        return CaptureAttempt.From(NormalizeBreaks(text), null, settled: true);
    }

    /// <summary>Bare CR (RichEdit's paragraph mark) to CRLF; existing CRLF pairs stay.</summary>
    internal static string NormalizeBreaks(string s)
    {
        if (s.IndexOf('\r') < 0) return s;
        var sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            sb.Append(s[i]);
            if (s[i] == '\r' && (i + 1 >= s.Length || s[i + 1] != '\n')) sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>A handle to the process owning a window, with scratch memory in it that is freed on dispose.</summary>
internal sealed class RemoteProcess : IDisposable
{
    private readonly IntPtr _h;
    private readonly List<IntPtr> _allocs = new();

    public bool IsWow64 { get; }

    private RemoteProcess(IntPtr h)
    {
        _h = h;
        IsWow64 = IsWow64Process(h, out bool wow) && wow;
    }

    /// <summary>Null when the process cannot be opened with <paramref name="access"/> (e.g. it runs elevated).</summary>
    public static RemoteProcess? Open(IntPtr hwnd, uint access)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        var h = OpenProcess(access, false, pid);
        return h == IntPtr.Zero ? null : new RemoteProcess(h);
    }

    public IntPtr Alloc(int bytes)
    {
        var p = VirtualAllocEx(_h, IntPtr.Zero, (UIntPtr)(uint)Math.Max(bytes, 16), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (p != IntPtr.Zero) _allocs.Add(p);
        return p;
    }

    public bool Write(IntPtr address, byte[] data) =>
        WriteProcessMemory(_h, address, data, (UIntPtr)(uint)data.Length, out var written) && (int)(uint)written == data.Length;

    public byte[]? Read(IntPtr address, int bytes)
    {
        var buf = new byte[bytes];
        if (bytes == 0) return buf;
        return ReadProcessMemory(_h, address, buf, (UIntPtr)(uint)bytes, out var read) && (int)(uint)read == bytes ? buf : null;
    }

    public void Dispose()
    {
        foreach (var p in _allocs) VirtualFreeEx(_h, p, UIntPtr.Zero, MEM_RELEASE);
        _allocs.Clear();
        CloseHandle(_h);
    }
}
