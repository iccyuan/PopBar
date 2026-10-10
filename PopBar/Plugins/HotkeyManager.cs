using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PopBar.Plugins;

/// <summary>Global hotkeys via RegisterHotKey on a message-only window (UI thread).</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<string, (int Num, Action Callback)> _byId = new();
    private int _next = 1;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("PopBar.Hotkeys") { WindowStyle = 0, Width = 0, Height = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _source.AddHook(WndProc);
    }

    /// <summary>Parse "Ctrl+Alt+O" into modifiers + virtual key. Returns false when unparsable.</summary>
    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "super": mods |= MOD_WIN; break;
                default:
                    if (vk != 0) return false;
                    Key key;
                    try { key = (Key)new KeyConverter().ConvertFromInvariantString(raw.Length == 1 ? raw.ToUpperInvariant() : raw)!; }
                    catch { return false; }
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    public bool Register(string id, string gesture, Action callback)
    {
        Unregister(id);
        if (!TryParse(gesture, out var mods, out var vk)) { Log.Warn($"hotkey '{gesture}' unparsable"); return false; }
        int num = _next++;
        if (!RegisterHotKey(_source.Handle, num, mods | MOD_NOREPEAT, vk))
        {
            Log.Warn($"hotkey '{gesture}' registration failed (in use?)");
            return false;
        }
        _byId[id] = (num, callback);
        Log.Info($"hotkey '{gesture}' registered as #{num} (mods=0x{mods:X}, vk=0x{vk:X}) on 0x{_source.Handle.ToInt64():X}");
        return true;
    }

    public void Unregister(string id)
    {
        if (_byId.Remove(id, out var e)) UnregisterHotKey(_source.Handle, e.Num);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int num = wParam.ToInt32();
            Log.Info($"hotkey #{num} pressed");
            foreach (var e in _byId.Values)
                if (e.Num == num) { try { e.Callback(); } catch (Exception ex) { Log.Error("hotkey callback", ex); } handled = true; break; }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var e in _byId.Values) UnregisterHotKey(_source.Handle, e.Num);
        _byId.Clear();
        _source.Dispose();
    }
}
