using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace PopBar.Plugins.Builtin;

/// <summary>Send the selection to another application: paste into its window, launch it with arguments, or open a URL scheme.</summary>
public sealed class SendToPlugin : IPopBarPlugin
{
    public sealed class Target
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..6];
        public string Label { get; set; } = "";
        public string Icon { get; set; } = "external-link";
        /// <summary>paste | launch | url</summary>
        public string Mode { get; set; } = "paste";
        /// <summary>paste: process name to find (e.g. notepad.exe). launch: exe path.</summary>
        public string Program { get; set; } = "";
        /// <summary>launch: argument template ({text}); url: URL template ({text} url-encoded).</summary>
        public string Args { get; set; } = "";
        /// <summary>paste: start the program if no window is found.</summary>
        public bool LaunchIfMissing { get; set; } = true;
        public bool Enabled { get; set; } = true;
    }

    public sealed class Settings
    {
        public List<Target> Targets { get; set; } = new();
        /// <summary>Two or more enabled targets share one bar button that expands into the list.</summary>
        public bool Fold { get; set; } = true;
    }

    private IPluginHost _host = null!;
    private Settings _cfg = new();

    public string Id => "sendto";
    public string Name => "发送到应用";
    public string Description => "把选中内容粘贴进指定程序的窗口、作为参数启动程序，或打开自定义 URL 协议。";
    public Version Version => new(0, 2, 0);

    public static List<Target> DefaultTargets() => new()
    {
        // The stock target is a generic "send to" whose program defaults to Notepad; users repoint it in settings.
        new() { Id = "notepad", Label = "发送到", Icon = "send", Mode = "paste", Program = "notepad.exe" },
        new() { Id = "vscode", Label = "VS Code", Icon = "terminal", Mode = "paste", Program = "Code.exe", LaunchIfMissing = false, Enabled = false },
        new() { Id = "obsidian", Label = "Obsidian", Icon = "bookmark", Mode = "url", Args = "obsidian://new?content={text}", Enabled = false },
    };

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
        if (_cfg.Targets.Count == 0) { _cfg.Targets = DefaultTargets(); host.Storage.Set(_cfg); }
        // Older installs named the stock target after Notepad itself.
        if (_cfg.Targets.FirstOrDefault(t => t.Id == "notepad" && t.Label == "记事本") is { } legacy)
        {
            legacy.Label = "发送到";
            if (legacy.Icon == "type") legacy.Icon = "send";
            host.Storage.Set(_cfg);
        }
    }

    public IReadOnlyList<PluginAction> GetActions()
    {
        var on = _cfg.Targets.Where(t => t.Enabled).ToList();
        bool folded = _cfg.Fold && on.Count > 1;
        return on.Select(t => new PluginAction
        {
            // Inside the folded list a target named like the group itself ("发送到") says nothing: name its program.
            Id = t.Id, Label = folded && t.Label == GroupName ? ProgramName(t) : t.Label, Icon = t.Icon,
            Description = Describe(t),
            Group = _cfg.Fold ? GroupName : null, GroupIcon = "send",
            Execute = (ctx, _) => { Send(t, ctx); return Task.CompletedTask; },
        }).ToList();
    }

    private const string GroupName = "发送到";

    private static string ProgramName(Target t)
    {
        if (t.Mode == "url") return t.Args.Split(':')[0];
        var name = System.IO.Path.GetFileNameWithoutExtension(t.Program);
        return name.Equals("notepad", StringComparison.OrdinalIgnoreCase) ? "记事本" : name;
    }

    private static string Describe(Target t) => t.Mode switch
    {
        "launch" => "启动 " + System.IO.Path.GetFileName(t.Program) + " 并传入选中内容",
        "url" => "打开 " + t.Args,
        _ => "粘贴到 " + t.Program,
    };

    private void Send(Target t, SelectionContext ctx)
    {
        var text = ctx.Text;
        switch (t.Mode)
        {
            case "launch":
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ExpandEnvironmentVariables(t.Program),
                    Arguments = t.Args.Replace("{text}", text.Replace("\"", "\\\"")),
                    UseShellExecute = true,
                });
                break;

            case "url":
                Process.Start(new ProcessStartInfo(t.Args.Replace("{text}", Uri.EscapeDataString(text))) { UseShellExecute = true });
                break;

            default: // paste
            {
                var hwnd = _host.FindWindowByProcess(t.Program);
                if (hwnd == IntPtr.Zero)
                {
                    if (!t.LaunchIfMissing) { _host.Log.Warn($"{t.Program} not running"); return; }
                    var p = Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(t.Program)) { UseShellExecute = true });
                    for (int i = 0; i < 40 && hwnd == IntPtr.Zero; i++)
                    {
                        Thread.Sleep(100);
                        hwnd = _host.FindWindowByProcess(System.IO.Path.GetFileName(t.Program));
                    }
                    if (hwnd == IntPtr.Zero) return;
                    Thread.Sleep(300); // let the new window take input
                }
                _host.ActivateWindow(hwnd);
                Thread.Sleep(80);
                _host.PasteText(text, hwnd);
                break;
            }
        }
    }

    // ------------------------------------------------------------------ settings

    public FrameworkElement? CreateSettingsView()
    {
        // Edit a draft; it only takes effect when the settings window is saved.
        var d = SettingsDraft.Clone(_cfg);
        void Changed() => _host.Settings.Changed();
        _host.Settings.OnApply(() =>
        {
            _cfg = SettingsDraft.Clone(d);
            _host.Storage.Set(_cfg);
            _host.RefreshActions();
        });
        var ui = _host.Ui;
        var list = new StackPanel();

        bool Edit(Target t, bool isNew)
        {
            var draft = new Target { Id = t.Id, Label = t.Label, Icon = t.Icon, Mode = t.Mode, Program = t.Program, Args = t.Args, LaunchIfMissing = t.LaunchIfMissing, Enabled = t.Enabled };
            var programBox = Form.TextBox(draft.Program, 240, v => draft.Program = v.Trim());
            var programRow = new StackPanel { Orientation = Orientation.Horizontal };
            programRow.Children.Add(programBox);
            var pick = ui.AppPicker(exe => { draft.Program = exe; programBox.Text = exe; }, 36);
            pick.Margin = new Thickness(6, 0, 0, 0); pick.ToolTip = "从运行中的程序选择";
            programRow.Children.Add(pick);
            var browse = ui.FileBrowse("浏览…", "程序 (*.exe)|*.exe", path => { draft.Program = path; programBox.Text = path; });
            browse.Margin = new Thickness(6, 0, 0, 0);
            programRow.Children.Add(browse);
            var content = Form.Stack(
                Form.Row("名称", Form.TextBox(draft.Label, 240, v => draft.Label = v)),
                Form.Row("图标", ui.IconPicker(draft.Icon, v => draft.Icon = v)),
                Form.Row("方式", Form.Combo(new[] { ("paste", "粘贴到程序窗口"), ("launch", "带参数启动程序"), ("url", "打开 URL 协议") }, draft.Mode, v => draft.Mode = v, 200)),
                Form.Row("程序", programRow, "粘贴方式填进程名（notepad.exe）；启动方式填可执行文件路径"),
                Form.Row("参数 / URL", Form.TextBox(draft.Args, 320, v => draft.Args = v), "{text} = 选中内容"),
                Form.Row("找不到窗口时启动", Form.Toggle(draft.LaunchIfMissing, v => draft.LaunchIfMissing = v)));
            if (!ui.EditDialog(isNew ? "添加发送目标" : "编辑发送目标", content, 600)) return false;
            if (string.IsNullOrWhiteSpace(draft.Label)) return false;
            t.Label = draft.Label.Trim(); t.Icon = draft.Icon; t.Mode = draft.Mode; t.Program = draft.Program; t.Args = draft.Args; t.LaunchIfMissing = draft.LaunchIfMissing;
            return true;
        }

        void Rebuild()
        {
            list.Children.Clear();
            foreach (var t in d.Targets)
            {
                var target = t;
                var sub = target.Mode switch { "launch" => "启动 " + target.Program, "url" => target.Args, _ => "粘贴到 " + target.Program };
                list.Children.Add(Form.ListRow(target.Label, sub, target.Enabled,
                    on => { target.Enabled = on; Changed(); },
                    () => { if (Edit(target, false)) { Changed(); Rebuild(); } },
                    () => { d.Targets.Remove(target); Changed(); Rebuild(); },
                    ui, target.Icon));
            }
        }
        Rebuild();

        return Form.Stack(
            Form.Card(Form.Row("合并为一个按钮", Form.Toggle(d.Fold, v => { d.Fold = v; Changed(); }),
                "开启多个目标时，操作条上只占一个「发送到」按钮，点击后展开选择目标")),
            list,
            Form.Buttons(Form.Button("＋ 添加目标", () => { var t = new Target { Label = "" }; if (Edit(t, true)) { d.Targets.Add(t); Changed(); Rebuild(); } }, accent: true)),
            Form.Hint("可以同时开启多个目标；合并时按钮出现在第一个目标的位置，展开后的顺序与「动作」页一致。"));
    }

    public void Dispose() { }
}
