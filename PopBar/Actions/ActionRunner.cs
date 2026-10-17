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

    private static void RunCommand(string command, string? args, string text)
    {
        var escaped = text.Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ExpandEnvironmentVariables(command),
            Arguments = (args ?? "").Replace("{text}", escaped),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);
    }
}
