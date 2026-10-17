using System.Diagnostics;
using PopBar.Core;

namespace PopBar.Actions;

/// <summary>
/// Executes an action against the captured selection on the thread pool, never on the UI thread and never on the
/// engine's capture thread: a slow action (an AI reply streaming for half a minute, a program being launched) must not
/// hold up the next selection. Clipboard-borrowing actions serialize themselves on <see cref="ClipboardBackup.Gate"/>.
/// </summary>
internal sealed class ActionRunner(Plugins.PluginManager plugins)
{
    /// <summary>Start the action and return at once; failures are logged.</summary>
    public void Start(ActionDefinition a, Selection sel) => _ = Task.Run(() => RunAsync(a, sel));

    private async Task RunAsync(ActionDefinition a, Selection sel)
    {
        string text = sel.Text;
        IntPtr sourceHwnd = sel.SourceWindow;
        try
        {
            switch ((a.Type ?? "").ToLowerInvariant())
            {
                case ActionTypes.Plugin:
                    if (a.Ref != null) await plugins.ExecuteAsync(a.Ref, sel.ToContext());
                    break;

                case ActionTypes.Copy:
                    InputSender.CopyToClipboard(text);
                    break;

                case ActionTypes.Url:
                    if (string.IsNullOrWhiteSpace(a.Url)) return;
                    OpenExternal(Expand(a.Url, text));
                    break;

                case ActionTypes.Open:
                    if (Matchers.IsEmail(text)) OpenExternal("mailto:" + text.Trim());
                    else OpenExternal(Matchers.NormalizeUrl(text));
                    break;

                case ActionTypes.Transform:
                {
                    var result = TransformOps.Apply(a.Op, text);
                    if (!string.IsNullOrEmpty(a.Template))
                        result = a.Template.Replace("{text}", result);
                    InputSender.PasteText(result, sourceHwnd);
                    break;
                }

                case ActionTypes.Paste:
                    InputSender.SendPaste(sourceHwnd);
                    break;

                case ActionTypes.Command:
                    if (string.IsNullOrWhiteSpace(a.Command)) return;
                    RunCommand(a.Command, a.Args, text);
                    break;

                default:
                    Log.Warn("unknown action type " + a.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"action '{a.Label}' failed", ex);
        }
    }

    private static string Expand(string template, string text) =>
        template.Replace("{text}", Uri.EscapeDataString(text.Trim()))
                .Replace("{raw}", text);

    private static void OpenExternal(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// <summary>
    /// The argument template is split into arguments first and {text} substituted inside each one, so the
    /// selection can never add, split or break out of an argument; .NET quotes each for the child process.
    /// The selection is also in the environment as POPBAR_TEXT, the safe way for .bat / cmd scripts to read it
    /// (cmd re-parses its command line, so no quoting survives special characters there).
    /// </summary>
    private static void RunCommand(string command, string? args, string text)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ExpandEnvironmentVariables(command),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in SplitArgs(args ?? "")) psi.ArgumentList.Add(arg.Replace("{text}", text));
        psi.Environment["POPBAR_TEXT"] = text;
        Process.Start(psi);
    }

    /// <summary>
    /// Split a command-line style string the way the C runtime does: whitespace separates, double quotes group,
    /// 2n backslashes before a quote give n backslashes, 2n+1 give n plus a literal quote, "" inside quotes is a quote.
    /// </summary>
    internal static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false, any = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\')
            {
                int n = 0;
                while (i < s.Length && s[i] == '\\') { n++; i++; }
                if (i < s.Length && s[i] == '"')
                {
                    cur.Append('\\', n / 2);
                    if (n % 2 == 1) cur.Append('"'); // escaped quote, consumed here
                    else i--;                        // the quote groups; the next round handles it
                }
                else { cur.Append('\\', n); i--; }
                any = true;
            }
            else if (c == '"')
            {
                if (inQuotes && i + 1 < s.Length && s[i + 1] == '"') { cur.Append('"'); i++; }
                else inQuotes = !inQuotes;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any) { list.Add(cur.ToString()); cur.Clear(); any = false; }
            }
            else { cur.Append(c); any = true; }
        }
        if (any) list.Add(cur.ToString());
        return list;
    }
}
