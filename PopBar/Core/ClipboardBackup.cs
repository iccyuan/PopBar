using System.Runtime.InteropServices;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>Full clipboard snapshot (every HGLOBAL format) so we can borrow the clipboard and give it back intact.</summary>
internal sealed class ClipboardSnapshot
{
    public readonly List<(uint Format, byte[] Data)> Items = new();
    public bool IsEmpty => Items.Count == 0;
}

internal static class ClipboardBackup
{
    private const long MaxTotalBytes = 64L * 1024 * 1024;

    /// <summary>Serializes every clipboard operation in the process (capture vs. paste-back).</summary>
    public static readonly object Gate = new();

    public static bool Open()
    {
        for (int i = 0; i < 25; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(8);
        }
        Log.Warn("OpenClipboard failed after retries");
        return false;
    }

    private static bool IsSkippedFormat(uint f)
    {
        if (f is CF_BITMAP or CF_METAFILEPICT or CF_ENHMETAFILE or CF_LOCALE or CF_OWNERDISPLAY
            or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE) return true;
        if (f >= CF_PRIVATEFIRST && f <= CF_GDIOBJLAST) return true;
        return false;
    }

    public static ClipboardSnapshot? Snapshot()
    {
        if (!Open()) return null;
        var snap = new ClipboardSnapshot();
        try
        {
            var formats = new List<uint>();
            uint f = 0;
            while ((f = EnumClipboardFormats(f)) != 0) formats.Add(f);
            bool hasUnicode = formats.Contains(CF_UNICODETEXT);
            long total = 0;
            foreach (var fmt in formats)
            {
                if (IsSkippedFormat(fmt)) continue;
                if (hasUnicode && (fmt == CF_TEXT || fmt == CF_OEMTEXT)) continue; // synthesized
                IntPtr h = GetClipboardData(fmt);
                if (h == IntPtr.Zero) continue;
                ulong size = GlobalSize(h).ToUInt64();
                if (size == 0 || size > int.MaxValue) continue;
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) continue;
                try
                {
                    total += (long)size;
                    if (total > MaxTotalBytes) break;
                    var data = new byte[size];
                    Marshal.Copy(p, data, 0, (int)size);
                    snap.Items.Add((fmt, data));
                }
                finally { GlobalUnlock(h); }
            }
        }
        catch (Exception ex) { Log.Error("clipboard snapshot failed", ex); }
        finally { CloseClipboard(); }
        return snap;
    }

    public static void Restore(ClipboardSnapshot? snap)
    {
        if (snap == null) return;
        if (!Open()) return;
        try
        {
            EmptyClipboard();
            foreach (var (fmt, data) in snap.Items)
            {
                IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(ulong)data.Length);
                if (h == IntPtr.Zero) continue;
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) { GlobalFree(h); continue; }
                Marshal.Copy(data, 0, p, data.Length);
                GlobalUnlock(h);
                if (SetClipboardData(fmt, h) == IntPtr.Zero) GlobalFree(h);
            }
        }
        catch (Exception ex) { Log.Error("clipboard restore failed", ex); }
        finally { CloseClipboard(); }
    }

    /// <summary>Read CF_UNICODETEXT; null when absent.</summary>
    public static string? GetText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        if (!Open()) return null;
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static bool SetText(string text)
    {
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(ulong)bytes);
            if (h == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return false; }
            unsafe
            {
                fixed (char* src = text)
                    Buffer.MemoryCopy(src, (void*)p, bytes, text.Length * 2);
                ((char*)p)[text.Length] = '\0';
            }
            GlobalUnlock(h);
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) { GlobalFree(h); return false; }
            return true;
        }
        finally { CloseClipboard(); }
    }
}
