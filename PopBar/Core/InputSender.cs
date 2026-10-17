using System.Runtime.InteropServices;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>Synthesizes Ctrl+C / Ctrl+V and implements the "borrow the clipboard" dance.</summary>
internal static class InputSender
{
    /// <summary>dwExtraInfo marker on every key we inject, so our own hooks can ignore them.</summary>
    public static readonly UIntPtr Tag = new(Sdk.HostInput.InjectedTag); // the convention plugins follow too

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = Tag } },
    };

    public static void SendCtrlCombo(ushort vk)
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, false), Key(vk, false), Key(vk, true), Key(VK_CONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Wait for Shift/Alt/Win to be physically released so Ctrl+C is not turned into Ctrl+Shift+C.</summary>
    public static bool WaitModifiersReleased(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (IsKeyDown(VK_SHIFT) || IsKeyDown(VK_MENU) || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN))
        {
            if (Environment.TickCount64 > deadline) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    /// <summary>
    /// Copy the current selection of the foreground app via Ctrl+C without disturbing the user's clipboard.
    /// Returns null when nothing was copied (sequence number unchanged) or no text format was produced.
    /// </summary>
    public static string? CopySelection(int waitMs = 350)
    {
        lock (ClipboardBackup.Gate)
        {
            if (!WaitModifiersReleased(600)) return null;
            var snap = ClipboardBackup.Snapshot();
            uint seq0 = GetClipboardSequenceNumber();
            SendCtrlCombo(VK_C);

            var deadline = Environment.TickCount64 + waitMs;
            bool changed = false;
            while (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(5);
                if (GetClipboardSequenceNumber() != seq0) { changed = true; break; }
            }
            if (!changed) return null;

            // Some apps write the clipboard in two steps; give them a moment.
            Thread.Sleep(15);
            string? text = null;
            try { text = ClipboardBackup.GetText(); }
            finally { ClipboardBackup.Restore(snap); }
            return text;
        }
    }

    /// <summary>How long a paste may take before the user's clipboard is put back regardless.</summary>
    private const int PasteReadTimeoutMs = 1500;

    /// <summary>Without delayed rendering we cannot tell when the paste happened; wait this long.</summary>
    private const int PasteFallbackMs = 300;

    /// <summary>Replace the selection in the source window with <paramref name="text"/> (clipboard restored afterwards).</summary>
    public static void PasteText(string text, IntPtr sourceHwnd)
    {
        lock (ClipboardBackup.Gate)
        {
            var snap = ClipboardBackup.Snapshot();
            // Delayed rendering tells us when the target reads the text; plain SetText is the fallback.
            bool offered = ClipboardOffer.Offer(text, sourceHwnd, out var read);
            if (!offered && !ClipboardBackup.SetText(text)) return;
            ForceForeground(sourceHwnd);
            Thread.Sleep(40);
            WaitModifiersReleased(600);
            if (offered) ClipboardOffer.PasteSent();
            SendCtrlCombo(VK_V);
            if (!offered) Thread.Sleep(PasteFallbackMs);
            else if (read.Wait(PasteReadTimeoutMs)) Thread.Sleep(30); // let the reader finish and close the clipboard
            else Log.Warn($"paste: target did not read the clipboard within {PasteReadTimeoutMs} ms; restoring anyway");
            ClipboardOffer.Withdraw();
            read.Dispose();
            ClipboardBackup.Restore(snap);
        }
    }

    /// <summary>Plain Ctrl+V with whatever is on the clipboard.</summary>
    public static void SendPaste(IntPtr sourceHwnd)
    {
        ForceForeground(sourceHwnd);
        Thread.Sleep(40);
        WaitModifiersReleased(600);
        SendCtrlCombo(VK_V);
    }

    public static void CopyToClipboard(string text)
    {
        lock (ClipboardBackup.Gate) ClipboardBackup.SetText(text);
    }
}
