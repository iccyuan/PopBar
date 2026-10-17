using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>
/// Global low-level mouse + keyboard hooks on a dedicated thread with its own message loop.
/// Callbacks must be cheap: the engine only does arithmetic here and queues real work elsewhere.
/// </summary>
internal sealed class InputHook : IDisposable
{
    /// <summary>(message, x, y) for button down/up and wheel. Mouse moves are filtered out.</summary>
    public event Action<int, int, int>? Mouse;
    /// <summary>Virtual-key code of a physical (non-injected) key press.</summary>
    public event Action<uint>? KeyDown;
    /// <summary>Virtual-key code of a physical (non-injected) key release.</summary>
    public event Action<uint>? KeyUp;
    /// <summary>Asked first for every physical key event (vk, is key-down): true swallows it, the target app never sees it.</summary>
    public Func<uint, bool, bool>? Swallow { get; set; }

    private readonly HookProc _mouseProc;
    private readonly HookProc _kbdProc;
    private IntPtr _mouseHook, _kbdHook;
    private Thread? _thread;
    private uint _threadId;

    public InputHook()
    {
        _mouseProc = MouseProc;
        _kbdProc = KbdProc;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "PopBar.Hook" };
        _thread.Start();
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        IntPtr mod = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
        _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, mod, 0);
        if (_mouseHook == IntPtr.Zero) Log.Error("SetWindowsHookEx(mouse) failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        if (_kbdHook == IntPtr.Zero) Log.Error("SetWindowsHookEx(keyboard) failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_kbdHook != IntPtr.Zero) UnhookWindowsHookEx(_kbdHook);
    }

    private unsafe IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            if (msg != WM_MOUSEMOVE)
            {
                var s = (MSLLHOOKSTRUCT*)lParam.ToPointer();
                if (s->dwExtraInfo != InputSender.Tag)
                {
                    try { Mouse?.Invoke(msg, s->pt.X, s->pt.Y); }
                    catch (Exception ex) { Log.Error("mouse handler", ex); }
                }
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private unsafe IntPtr KbdProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            if (down || msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                var s = (KBDLLHOOKSTRUCT*)lParam.ToPointer();
                if (s->dwExtraInfo != InputSender.Tag)
                {
                    bool swallow = false;
                    try
                    {
                        swallow = Swallow?.Invoke(s->vkCode, down) == true;
                        (down ? KeyDown : KeyUp)?.Invoke(s->vkCode);
                    }
                    catch (Exception ex) { Log.Error("key handler", ex); }
                    if (swallow) return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_kbdHook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(500);
    }
}
