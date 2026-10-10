using System.Diagnostics;
using PopBar.Core;

namespace PopBar.Actions;

/// <summary>Executes an action against the captured selection. Runs on a worker thread, never on the UI thread.</summary>
internal static class ActionRunner
{
    /// <summary>Set by App at startup; plugin-typed actions are routed here.</summary>
    public static Plugins.PluginManager? Plugins { get; set; }

    public static void Run(ActionDefinition a, Selection sel)
    {
        string text = sel.Text;
        IntPtr sourceHwnd = sel.SourceWindow;
        try
        {
            switch ((a.Type ?? "").ToLowerInvariant())
            {
                case ActionTypes.Plugin:
                    if (a.Ref != null && Plugins != null)
                        Plugins.ExecuteAsync(a.Ref, sel.ToContext()).GetAwaiter().GetResult();
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
