using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>
/// Answers "can the user type where this selection is?" so input-only actions (paste) appear only in editable fields.
/// Cheap signal first (a system caret in the source thread), UI Automation on the focused element second.
/// </summary>
internal sealed class EditableProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);

    private IUIAutomation? _uia;

    public bool IsEditable(IntPtr sourceWindow)
    {
        if (sourceWindow == IntPtr.Zero) return false;

        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        uint tid = GetWindowThreadProcessId(sourceWindow, out _);
        if (GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != IntPtr.Zero) return true;

        try
        {
            _uia ??= CreateUia();
            var el = _uia.GetFocusedElement();
            if (el == null) return false;
            if (el.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern vp)
                return vp.CurrentIsReadOnly == 0;
            return el.CurrentControlType == UIA_ControlTypeIds.UIA_EditControlTypeId;
        }
        catch
        {
            _uia = null; // recreate a broken COM proxy next time
            return false;
        }
    }

    private static IUIAutomation CreateUia()
    {
        var uia = new CUIAutomation8();
        try
        {
            if (uia is IUIAutomation2 u2) { u2.ConnectionTimeout = 400; u2.TransactionTimeout = 800; }
        }
        catch { /* older OS */ }
        return uia;
    }
}
