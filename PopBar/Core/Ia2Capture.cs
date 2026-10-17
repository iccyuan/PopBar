using System.Runtime.InteropServices;
using System.Text;
using Accessibility;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>
/// IAccessible2 text: Firefox / Thunderbird, LibreOffice, Qt 5 apps and older Chromium / Electron, which expose rich
/// text through IA2 rather than UIA. The focused MSAA object is found through accFocus, its IAccessibleText reports the
/// selection, and embedded objects (U+FFFC: paragraphs, links... of a web document) are expanded into their own text.
/// </summary>
internal sealed class Ia2CaptureStrategy : ITextCaptureStrategy
{
    private const int MaxChars = 1_000_000;
    /// <summary>Limits for walking a document's embedded objects, so a huge page cannot stall the capture.</summary>
    private const int MaxDepth = 12, MaxNodes = 4000;
    private const int MaxFocusHops = 32;
    private const char Embed = '￼';
    private const int IA2_COORDTYPE_SCREEN_RELATIVE = 0;

    private const int ROLE_SYSTEM_DOCUMENT = 15, ROLE_SYSTEM_LINK = 30, ROLE_SYSTEM_GRAPHIC = 40, ROLE_SYSTEM_STATICTEXT = 41, ROLE_SYSTEM_TEXT = 42;
    private const int STATE_SYSTEM_PROTECTED = 0x20000000;
    /// <summary>Gecko reports roles without an MSAA equivalent as the element's tag name; these flow inside a line.</summary>
    private static readonly HashSet<string> InlineTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "b", "bdi", "bdo", "cite", "code", "data", "del", "dfn", "em", "i", "ins", "kbd", "label", "mark",
        "q", "s", "samp", "small", "span", "strong", "sub", "sup", "time", "u", "var",
    };

    private static Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private static Guid IID_IAccessible2 = new("E89F726E-C4F4-4c19-BB19-B647D7FA8478");
    private static Guid IID_IAccessibleText = new("24FD2FFB-3AAD-4a08-8335-A3AD89C0FB4B");
    private static Guid IID_IAccessibleHypertext = new("6B4F8BBF-F1F2-418a-B35E-A195BC4103B9");

    public CaptureMethod Method => CaptureMethod.IAccessible2;
    public bool CanRun(CaptureContext ctx) => ctx.FocusWindow != IntPtr.Zero;

    public CaptureAttempt Capture(CaptureContext ctx)
    {
        if (AccessibleObjectFromWindow(ctx.FocusWindow, OBJID_CLIENT, ref IID_IAccessible, out var root) != 0 || root is not IAccessible acc)
            return CaptureAttempt.Unsupported;
        acc = Focused(acc);
        var text = TextOf(acc);
        if (text == null) return CaptureAttempt.Unsupported;
        if (HasState(acc, STATE_SYSTEM_PROTECTED)) return CaptureAttempt.NoSelection; // password field

        var walk = new Walk();
        walk.Collect(text, 0);
        // "No selection" is final only where the selection lives on the focused object itself: a document or a text field.
        bool settled = Role(acc) is ROLE_SYSTEM_DOCUMENT or ROLE_SYSTEM_TEXT;
        var s = walk.Result();
        if (s.Length > MaxChars) return CaptureAttempt.NoSelection;
        return CaptureAttempt.From(s, TextBounds.Plausible(walk.Bounds(), ctx), settled);
    }

    /// <summary>Follow accFocus down to the deepest focused object (it stops at an object that reports itself or a simple child).</summary>
    private static IAccessible Focused(IAccessible acc)
    {
        for (int i = 0; i < MaxFocusHops; i++)
        {
            object? f;
            try { f = acc.accFocus; }
            catch { break; }
            if (f is IAccessible child && !ReferenceEquals(child, acc)) acc = child;
            else break;
        }
        return acc;
    }

    private static int? Role(IAccessible acc)
    {
        try { return acc.get_accRole(0) is int r ? r : null; }
        catch { return null; }
    }

    private static bool HasState(IAccessible acc, int state)
    {
        try { return acc.get_accState(0) is int s && (s & state) != 0; }
        catch { return false; }
    }

    /// <summary>Whether an embedded object starts a block of its own (so its text goes on separate lines).</summary>
    private static bool IsBlock(object obj)
    {
        if (obj is not IAccessible acc) return true;
        object? role;
        try { role = acc.get_accRole(0); }
        catch { return true; }
        return role switch
        {
            int r => r is not (ROLE_SYSTEM_LINK or ROLE_SYSTEM_GRAPHIC or ROLE_SYSTEM_STATICTEXT or ROLE_SYSTEM_TEXT),
            string tag => !InlineTags.Contains(tag),
            _ => true,
        };
    }

    /// <summary>The object's IAccessibleText (through IAccessible2's service), or null when it has no IA2 text.</summary>
    private static IAccessibleText? TextOf(object obj)
    {
        IntPtr ia2 = IntPtr.Zero;
        try
        {
            if (obj is IOleServiceProvider sp && sp.QueryService(ref IID_IAccessible, ref IID_IAccessible2, out ia2) == 0 && ia2 != IntPtr.Zero)
                return QueryInterface<IAccessibleText>(ia2, IID_IAccessibleText);
            // IA2 objects are queried directly as well (some implementations answer QI but not QueryService)
            var unk = Marshal.GetIUnknownForObject(obj);
            try { return QueryInterface<IAccessibleText>(unk, IID_IAccessibleText); }
            finally { Marshal.Release(unk); }
        }
        catch { return null; }
        finally { if (ia2 != IntPtr.Zero) Marshal.Release(ia2); }
    }

    private static T? QueryInterface<T>(IntPtr unk, Guid iid) where T : class
    {
        if (Marshal.QueryInterface(unk, in iid, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.GetObjectForIUnknown(p) as T; }
        finally { Marshal.Release(p); }
    }

    /// <summary>Gathers the selected text across nested hypertext objects, remembering the first and last leaf characters for placement.</summary>
    private sealed class Walk
    {
        private readonly StringBuilder _sb = new();
        private bool _breakPending;
        private int _nodes;
        private (IAccessibleText Text, int Offset)? _first, _last;

        public string Result() => _sb.ToString().Trim('\r', '\n');

        /// <summary>The selected parts of <paramref name="text"/>; an embedded child inside a range is expanded by its own selection or whole.</summary>
        public void Collect(IAccessibleText text, int depth)
        {
            if (text.get_nSelections(out int n) != 0 || n <= 0) return;
            for (int i = 0; i < n; i++)
                if (text.get_selection(i, out int s, out int e) == 0 && e > s) Expand(text, s, e, depth, bySelection: true);
        }

        private void Expand(IAccessibleText text, int start, int end, int depth, bool bySelection)
        {
            if (++_nodes > MaxNodes || _sb.Length > MaxChars) return;
            if (text.get_text(start, end, out var s) != 0 || string.IsNullOrEmpty(s)) return;
            var hyper = depth < MaxDepth && s.IndexOf(Embed) >= 0 ? (text as IAccessibleHypertext) : null;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != Embed) { Append(s[i], text, start + i); continue; }
                if (hyper == null) continue;
                if (hyper.get_hyperlinkIndex(start + i, out int li) != 0 || li < 0) continue;
                if (hyper.get_hyperlink(li, out var link) != 0 || link == null) continue;
                var child = TextOf(link);
                if (child == null) continue;
                bool block = IsBlock(link);
                if (block) Break();
                // An embed at the edge of a selection is only partly selected and reports its share itself; one inside is whole.
                if (bySelection && child.get_nSelections(out int cn) == 0 && cn > 0) Collect(child, depth + 1);
                else if (child.get_nCharacters(out int len) == 0 && len > 0) Expand(child, 0, len, depth + 1, bySelection: false);
                if (block) _breakPending = true;
            }
        }

        private void Append(char c, IAccessibleText owner, int offset)
        {
            if (_breakPending) { Break(); _breakPending = false; }
            _sb.Append(c);
            _first ??= (owner, offset);
            if (!char.IsWhiteSpace(c)) _last = (owner, offset);
        }

        private void Break()
        {
            if (_sb.Length > 0 && _sb[^1] != '\n') _sb.Append("\r\n");
        }

        public RECT? Bounds()
        {
            RECT? Extent((IAccessibleText Text, int Offset)? at)
            {
                if (at is not { } a) return null;
                try
                {
                    if (a.Text.get_characterExtents(a.Offset, IA2_COORDTYPE_SCREEN_RELATIVE, out int x, out int y, out int w, out int h) != 0 || h <= 0) return null;
                    return new RECT { Left = x, Top = y, Right = x + Math.Max(w, 1), Bottom = y + h };
                }
                catch { return null; }
            }
            return TextBounds.Union(Extent(_first), Extent(_last));
        }
    }
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleServiceProvider
{
    [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

/// <summary>IAccessible2's IAccessibleText (AccessibleText.idl). Only the members used here are typed; the rest keep their vtable slots.</summary>
[ComImport, Guid("24FD2FFB-3AAD-4a08-8335-A3AD89C0FB4B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAccessibleText
{
    void _addSelection();
    void _get_attributes();
    void _get_caretOffset();
    [PreserveSig] int get_characterExtents(int offset, int coordType, out int x, out int y, out int width, out int height);
    [PreserveSig] int get_nSelections(out int nSelections);
    void _get_offsetAtPoint();
    [PreserveSig] int get_selection(int selectionIndex, out int startOffset, out int endOffset);
    [PreserveSig] int get_text(int startOffset, int endOffset, [MarshalAs(UnmanagedType.BStr)] out string text);
    void _get_textBeforeOffset();
    void _get_textAfterOffset();
    void _get_textAtOffset();
    void _removeSelection();
    void _setCaretOffset();
    void _setSelection();
    [PreserveSig] int get_nCharacters(out int nCharacters);
    void _scrollSubstringTo();
    void _scrollSubstringToPoint();
    void _get_newText();
    void _get_oldText();
}

/// <summary>IAccessibleHypertext (AccessibleHypertext.idl): IAccessibleText's slots, then the embedded-object lookups.</summary>
[ComImport, Guid("6B4F8BBF-F1F2-418a-B35E-A195BC4103B9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAccessibleHypertext
{
    void _addSelection();
    void _get_attributes();
    void _get_caretOffset();
    void _get_characterExtents();
    void _get_nSelections();
    void _get_offsetAtPoint();
    void _get_selection();
    void _get_text();
    void _get_textBeforeOffset();
    void _get_textAfterOffset();
    void _get_textAtOffset();
    void _removeSelection();
    void _setCaretOffset();
    void _setSelection();
    void _get_nCharacters();
    void _scrollSubstringTo();
    void _scrollSubstringToPoint();
    void _get_newText();
    void _get_oldText();
    void _get_nHyperlinks();
    [PreserveSig] int get_hyperlink(int index, [MarshalAs(UnmanagedType.IUnknown)] out object? hyperlink);
    [PreserveSig] int get_hyperlinkIndex(int charIndex, out int hyperlinkIndex);
}
