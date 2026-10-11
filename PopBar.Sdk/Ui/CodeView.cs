using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PopBar.Sdk.Ui;

/// <summary>
/// Read-only, selectable, syntax-highlighted text for result panels. Colours are mid-tone so they read on both
/// light and dark panels; plain text follows the panel foreground.
/// </summary>
public static class CodeView
{
    private static readonly Brush Key = Frozen(0x3B, 0x82, 0xF6);
    private static readonly Brush Str = Frozen(0x16, 0xA3, 0x4A);
    private static readonly Brush Num = Frozen(0xE0, 0x7A, 0x10);
    private static readonly Brush Lit = Frozen(0xB0, 0x4C, 0xD6);
    private static readonly Brush Comment = Frozen(0x8A, 0x92, 0x9E);

    /// <summary>
    /// JSON with keys, strings, numbers and literals coloured. Also tolerates "// comment" lines (shown muted), so
    /// annotated output such as a decoded JWT highlights too. Invalid input is still shown, just less coloured.
    /// </summary>
    public static FrameworkElement Json(string text)
    {
        var para = new Paragraph { Margin = new Thickness(0) };
        int i = 0, n = text.Length;
        var plain = new StringBuilder();
        void Flush() { if (plain.Length > 0) { para.Inlines.Add(new Run(plain.ToString())); plain.Clear(); } }
        void Add(string s, Brush? fg)
        {
            Flush();
            var r = new Run(s);
            if (fg != null) r.Foreground = fg;
            para.Inlines.Add(r);
        }

        while (i < n)
        {
            char c = text[i];
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i); if (end < 0) end = n;
                Add(text[i..end], Comment);
                i = end;
            }
            else if (c == '"')
            {
                int j = i + 1;
                while (j < n && text[j] != '"' && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                j = Math.Min(j + 1, n);
                // A string followed by ':' is a key.
                int k = j; while (k < n && text[k] is ' ' or '\t') k++;
                Add(text[i..j], k < n && text[k] == ':' ? Key : Str);
                i = j;
            }
            else if (c == '-' || char.IsAsciiDigit(c))
            {
                int j = i + 1;
                while (j < n && (char.IsAsciiDigit(text[j]) || text[j] is '.' or 'e' or 'E' or '+' or '-')) j++;
                Add(text[i..j], Num);
                i = j;
            }
            else if (Word(text, i, "true") || Word(text, i, "false") || Word(text, i, "null"))
            {
                int len = text[i] == 'f' ? 5 : 4;
                Add(text.Substring(i, len), Lit);
                i += len;
            }
            else { plain.Append(c); i++; }
        }
        Flush();

        var doc = new FlowDocument(para)
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 13,
            LineHeight = 19,
        };
        var box = new RichTextBox
        {
            Document = doc, IsReadOnly = true, IsReadOnlyCaretVisible = false,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        // Plain text and selection follow the panel's palette (resource keys of PopBar's panel window).
        box.SetResourceReference(Control.ForegroundProperty, "PFg");
        box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, "PAccent");
        return box;
    }

    private static bool Word(string s, int i, string w) =>
        string.CompareOrdinal(s, i, w, 0, w.Length) == 0
        && (i == 0 || !char.IsLetterOrDigit(s[i - 1]))
        && (i + w.Length >= s.Length || !char.IsLetterOrDigit(s[i + w.Length]));

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
