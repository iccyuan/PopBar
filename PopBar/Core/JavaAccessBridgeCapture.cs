using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>
/// Java Swing / AWT apps (IntelliJ IDEA and the other JetBrains IDEs, DBeaver, ...) expose no UIA text, but the Java
/// Access Bridge reads their focused component's selection. It only answers when the app loaded its half of the bridge
/// (javaaccessbridge.dll), which <see cref="JavaAccessBridgeSetup"/> switches on for Java apps started afterwards;
/// otherwise the chain moves on.
/// </summary>
internal sealed class JabCaptureStrategy : ITextCaptureStrategy
{
    private const int MaxChars = 1_000_000;

    public CaptureMethod Method => CaptureMethod.JavaAccessBridge;

    /// <summary>AWT names its top-level windows SunAwtFrame / SunAwtDialog / ...; nothing else can be a Java window.</summary>
    public bool CanRun(CaptureContext ctx) => GetWindowClass(ctx.ForegroundWindow).StartsWith("SunAwt", StringComparison.Ordinal);

    public CaptureAttempt Capture(CaptureContext ctx)
    {
        var bridge = JavaProcesses.BridgeDllFor(ctx.ForegroundWindow);
        if (bridge == null || !JavaAccessBridge.Start(bridge)) return CaptureAttempt.Unsupported;
        return JavaAccessBridge.ReadSelection(ctx.ForegroundWindow, MaxChars);
    }
}

/// <summary>Which Java processes have the bridge loaded, and where the matching client DLL lives.</summary>
internal static class JavaProcesses
{
    /// <summary>A negative answer is re-checked after this long (the user may enable the bridge and restart the app).</summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<uint, (string? Dll, DateTime At)> Cache = new();
    private static readonly HashSet<uint> Hinted = new();

    /// <summary>Path of WindowsAccessBridge-64.dll to talk to this window's JVM, or null when that JVM has no bridge.</summary>
    public static string? BridgeDllFor(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        if (Cache.TryGetValue(pid, out var c) && (c.Dll != null || DateTime.UtcNow - c.At < NegativeTtl)) return c.Dll;
        var dll = Find(pid);
        Cache[pid] = (dll, DateTime.UtcNow);
        if (dll == null)
            lock (Hinted)
                if (Hinted.Add(pid)) Log.Info($"java window without Access Bridge (pid {pid}): turn on 设置 → 通用 → Java 程序直接取词 and restart the app to read its selection without the clipboard");
        return dll;
    }

    private static string? Find(uint pid)
    {
        string? dir = null;
        for (int attempt = 0; attempt < 2 && dir == null; attempt++) // ERROR_BAD_LENGTH while the process loads modules: retry
        {
            var snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
            if (snap == INVALID_HANDLE_VALUE) continue;
            try
            {
                var me = new MODULEENTRY32W { dwSize = Marshal.SizeOf<MODULEENTRY32W>() };
                for (bool ok = Module32FirstW(snap, ref me); ok; ok = Module32NextW(snap, ref me))
                    if (me.szModule.StartsWith("javaaccessbridge", StringComparison.OrdinalIgnoreCase))
                    {
                        dir = Path.GetDirectoryName(me.szExePath);
                        break;
                    }
            }
            finally { CloseHandle(snap); }
            if (dir == null) break;
        }
        if (dir == null) return null;
        // the client DLL shipped with that JVM, else one installed system-wide by a JRE
        foreach (var candidate in new[]
        {
            Path.Combine(dir, "windowsaccessbridge-64.dll"),
            Path.Combine(Environment.SystemDirectory, "WindowsAccessBridge-64.dll"),
        })
            if (File.Exists(candidate)) return candidate;
        return null;
    }
}

/// <summary>
/// The client side of the Java Access Bridge. Its DLL talks to the JVMs through window messages, so it lives on a
/// thread of its own that pumps them; every call is marshalled onto that thread.
/// </summary>
internal static class JavaAccessBridge
{
    private const int MaxStringSize = 1024, ShortStringSize = 256;
    // AccessibleContextInfo: name, description [1024], role, role_en_US, states, states_en_US [256] (wchar_t), then
    // indexInParent, childrenCount, x, y, width, height (jint), then accessibleComponent / Action / Selection / Text / Interfaces (BOOL)
    private const int ContextInfoSize = (2 * MaxStringSize + 4 * ShortStringSize) * 2 + 6 * 4 + 5 * 4;
    private const int RoleEnOffset = (2 * MaxStringSize + ShortStringSize) * 2;
    private const int AccessibleTextOffset = (2 * MaxStringSize + 4 * ShortStringSize) * 2 + 6 * 4 + 3 * 4;
    // AccessibleTextSelectionInfo: selectionStartIndex, selectionEndIndex (jint), selectedText [1024]
    private const int SelectionInfoSize = 8 + MaxStringSize * 2;
    /// <summary>Characters per GetAccessibleTextRange call (its length argument is a short).</summary>
    private const int RangeChunk = 4096;
    /// <summary>JVMs answer the bridge's discovery broadcast asynchronously; right after start a window may not be known yet.</summary>
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(1500);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WindowsRunFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IsJavaWindowFn(IntPtr hwnd);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ContextWithFocusFn(IntPtr hwnd, out int vmId, out long ac);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ContextInfoFn(int vmId, long ac, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SelectionInfoFn(int vmId, long at, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TextRangeFn(int vmId, long at, int start, int end, IntPtr text, short len);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseFn(int vmId, long obj);

    private static readonly object Gate = new();
    private static Dispatcher? _thread;
    private static bool _failed;
    private static DateTime _startedAt;
    private static IsJavaWindowFn? _isJavaWindow;
    private static ContextWithFocusFn? _withFocus;
    private static ContextInfoFn? _contextInfo;
    private static SelectionInfoFn? _selectionInfo;
    private static TextRangeFn? _textRange;
    private static ReleaseFn? _release;

    /// <summary>Load the client DLL and start the bridge (once per run); false when it cannot be loaded.</summary>
    public static bool Start(string dll)
    {
        lock (Gate)
        {
            if (_thread != null) return true;
            if (_failed) return false;
            Dispatcher? dispatcher = null;
            using var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true, Name = "PopBar.JavaAccessBridge" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.Wait();
            bool ok = dispatcher!.Invoke(() => Bind(dll));
            if (!ok)
            {
                _failed = true;
                dispatcher.InvokeShutdown();
                return false;
            }
            _thread = dispatcher;
            _startedAt = DateTime.UtcNow;
            Log.Info($"java access bridge started: {dll}");
            return true;
        }
    }

    private static bool Bind(string dll)
    {
        var h = LoadLibraryExW(dll, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (h == IntPtr.Zero) { Log.Warn($"java access bridge: cannot load {dll} ({Marshal.GetLastWin32Error()})"); return false; }
        T? Fn<T>(string name) where T : Delegate
        {
            var p = GetProcAddress(h, name);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }
        var run = Fn<WindowsRunFn>("Windows_run");
        _isJavaWindow = Fn<IsJavaWindowFn>("isJavaWindow");
        _withFocus = Fn<ContextWithFocusFn>("getAccessibleContextWithFocus");
        _contextInfo = Fn<ContextInfoFn>("getAccessibleContextInfo");
        _selectionInfo = Fn<SelectionInfoFn>("getAccessibleTextSelectionInfo");
        _textRange = Fn<TextRangeFn>("getAccessibleTextRange");
        _release = Fn<ReleaseFn>("releaseJavaObject");
        if (run == null || _isJavaWindow == null || _withFocus == null || _contextInfo == null || _selectionInfo == null || _textRange == null || _release == null)
        {
            Log.Warn($"java access bridge: {dll} lacks the expected exports");
            return false;
        }
        run();
        return true;
    }

    private static T Call<T>(Func<T> f) => _thread!.Invoke(f, DispatcherPriority.Send, CancellationToken.None, CallTimeout);

    public static CaptureAttempt ReadSelection(IntPtr hwnd, int maxChars)
    {
        if (_thread == null) return CaptureAttempt.Unsupported;
        // Just started: give the JVMs' replies to the discovery broadcast a moment to arrive (they need this thread's pump).
        while (!Call(() => _isJavaWindow!(hwnd) != 0))
        {
            if (DateTime.UtcNow - _startedAt > DiscoveryWindow) return CaptureAttempt.Unsupported;
            Thread.Sleep(50);
        }
        return Call(() => ReadOnBridgeThread(hwnd, maxChars));
    }

    private static CaptureAttempt ReadOnBridgeThread(IntPtr hwnd, int maxChars)
    {
        if (_withFocus!(hwnd, out int vm, out long ac) == 0 || ac == 0) return CaptureAttempt.Unsupported;
        var info = Marshal.AllocHGlobal(Math.Max(ContextInfoSize, SelectionInfoSize));
        try
        {
            if (_contextInfo!(vm, ac, info) == 0) return CaptureAttempt.Unsupported;
            var role = Marshal.PtrToStringUni(info + RoleEnOffset, ShortStringSize).Split('\0')[0];
            if (role.Contains("password", StringComparison.OrdinalIgnoreCase)) return CaptureAttempt.NoSelection;
            if (Marshal.ReadInt32(info, AccessibleTextOffset) == 0) return CaptureAttempt.Unsupported;

            if (_selectionInfo!(vm, ac, info) == 0) return CaptureAttempt.Unsupported;
            int start = Marshal.ReadInt32(info, 0), end = Marshal.ReadInt32(info, 4);
            if (start < 0 || end <= start) return CaptureAttempt.NoSelection;
            if (end - start > maxChars) return CaptureAttempt.NoSelection;
            // the info carries the text itself up to MaxStringSize - 1 characters; longer selections are fetched in chunks
            if (end - start < MaxStringSize - 1)
                return CaptureAttempt.From(Marshal.PtrToStringUni(info + 8, MaxStringSize).Split('\0')[0], null, settled: true);
            var sb = new System.Text.StringBuilder(end - start);
            var buf = Marshal.AllocHGlobal((RangeChunk + 1) * 2);
            try
            {
                for (int s = start; s < end; s += RangeChunk)
                {
                    int count = Math.Min(RangeChunk, end - s);
                    Marshal.WriteInt16(buf, 0, 0);
                    // the range is inclusive; the length is the buffer's, in characters
                    if (_textRange!(vm, ac, s, s + count - 1, buf, (short)(RangeChunk + 1)) == 0) return CaptureAttempt.Unsupported;
                    sb.Append(Marshal.PtrToStringUni(buf, count).Split('\0')[0]);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return CaptureAttempt.From(sb.ToString(), null, settled: true);
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            _release!(vm, ac);
        }
    }
}
