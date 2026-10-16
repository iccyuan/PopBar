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
        /// <summary>paste: process name or exe path to find (e.g. notepad.exe). launch: exe path.</summary>
        public string Program { get; set; } = "";
        /// <summary>paste: class of the window picked in settings; preferred when the program has several windows.</summary>
        public string WindowClass { get; set; } = "";
        /// <summary>launch: argument template ({text}); url: URL template ({text} url-encoded).</summary>
        public string Args { get; set; } = "";
        /// <summary>paste: start the program if it is not running.</summary>
        public bool LaunchIfMissing { get; set; } = true;
        /// <summary>
        /// paste: leave the text on the clipboard and only switch to the window, for programs that discard synthetic
        /// input (WeChat 4 drops every injected key and mouse click). null = decide by program name.
        /// </summary>
        public bool? ManualPaste { get; set; }
        public bool Enabled { get; set; } = true;

        /// <summary>Programs known to ignore injected input; the user pastes with Ctrl+V themselves.</summary>
        public bool UsesManualPaste => ManualPaste ?? IgnoresSyntheticInput(Program);

        public static bool IgnoresSyntheticInput(string program)
        {
            var exe = System.IO.Path.GetFileName(program);
            return exe.Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("WeChat.exe", StringComparison.OrdinalIgnoreCase);
        }

        public void CopyFrom(Target o)
        {
            Label = o.Label; Icon = o.Icon; Mode = o.Mode; Program = o.Program; WindowClass = o.WindowClass; Args = o.Args; LaunchIfMissing = o.LaunchIfMissing; ManualPaste = o.ManualPaste; Enabled = o.Enabled;
        }
    }

    public sealed class Settings
    {
        public List<Target> Targets { get; set; } = new();
        /// <summary>Two or more enabled targets share one bar button that expands into the list.</summary>
        public bool Fold { get; set; } = true;
    }

    private IPluginHost _host = null!;
    private Settings _cfg = new();
    // The settings page edits a draft that only lands in _cfg when the window is saved. An edit made from the actions
    // list must reach that draft too, or saving the window afterwards would undo it.
    private Settings? _draft;
    private Action? _rebuildDraftList;

    public string Id => "sendto";
    public string Name => "发送到应用";
    public string Description => "把选中内容粘贴到别的程序、带参数启动程序或打开 URL 协议。";
    public Version Version => new(0, 2, 1);

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
            // Finding, launching and activating the target can take seconds; the engine's worker must not wait for it
            // or the bar stops reacting to selections meanwhile.
            Execute = (ctx, ct) => { _ = Task.Run(() => SendSafe(t, ctx), ct); return Task.CompletedTask; },
            Configure = () => ConfigureTarget(t.Id),
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
        _ => (t.UsesManualPaste ? "复制并切换到 " : "粘贴到 ") + System.IO.Path.GetFileName(t.Program),
    };

    private void SendSafe(Target t, SelectionContext ctx)
    {
        try { Send(t, ctx); }
        catch (Exception ex) { _host.Log.Error($"发送到「{t.Label}」失败", ex); }
    }

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
                var hwnd = FindTarget(t);
                if (hwnd == IntPtr.Zero)
                {
                    var exe = System.IO.Path.GetFileName(t.Program);
                    if (IsRunning(exe))
                    {
                        // Starting the exe again would open a second instance for multi-instance programs (WeChat).
                        _host.Log.Warn($"{exe} 正在运行，但没有找到可以粘贴的窗口");
                        return;
                    }
                    if (!t.LaunchIfMissing) { _host.Log.Warn($"{exe} 未运行"); return; }
                    try { Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(t.Program)) { UseShellExecute = true }); }
                    catch (Exception ex)
                    {
                        _host.Log.Warn($"无法启动 {t.Program}：{ex.Message}。请在「发送到应用」里用「拾取窗口」选择程序，或填写 exe 的完整路径");
                        return;
                    }
                    for (int i = 0; i < 40 && hwnd == IntPtr.Zero; i++)
                    {
                        Thread.Sleep(100);
                        hwnd = FindTarget(t);
                    }
                    if (hwnd == IntPtr.Zero) { _host.Log.Warn($"{exe} 启动后 4 秒内没有出现窗口"); return; }
                    Thread.Sleep(300); // let the new window take input
                }
                bool manual = t.UsesManualPaste;
                _host.Log.Info($"发送到「{t.Label}」：窗口 0x{hwnd:X}（{System.IO.Path.GetFileName(t.Program)}），{text.Length} 字{(manual ? "，复制到剪贴板后切换过去" : "")}");
                if (manual)
                {
                    // The program throws injected keystrokes away, so the text stays on the clipboard for a real Ctrl+V.
                    _host.CopyToClipboard(text);
                    _host.ActivateWindow(hwnd);
                    break;
                }
                _host.ActivateWindow(hwnd);
                Thread.Sleep(80);
                _host.PasteText(text, hwnd);
                break;
            }
        }
    }

    private IntPtr FindTarget(Target t) => _host.FindWindowByProcess(t.Program, t.WindowClass.Length > 0 ? t.WindowClass : null);

    private static bool IsRunning(string exe)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(exe);
        if (stem.Length == 0) return false;
        var procs = Process.GetProcessesByName(stem);
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>What to call a picked program: its own description ("微信"), else the exe stem.</summary>
    private static string AppLabel(PickedWindow w)
    {
        try
        {
            var d = FileVersionInfo.GetVersionInfo(w.Path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(d)) return d;
        }
        catch { }
        return System.IO.Path.GetFileNameWithoutExtension(w.Exe);
    }

    // ------------------------------------------------------------------ settings

    /// <summary>"编辑" on the target's row in the actions list: edit it in place and save right away.</summary>
    private bool ConfigureTarget(string id)
    {
        var t = _cfg.Targets.FirstOrDefault(x => x.Id == id);
        if (t == null || !EditTarget(t, false)) return false;
        _host.Storage.Set(_cfg);
        if (_draft?.Targets.FirstOrDefault(x => x.Id == id) is { } dt) { dt.CopyFrom(t); _rebuildDraftList?.Invoke(); }
        _host.RefreshActions();
        return true;
    }

    /// <summary>Modal editor for one target; on 保存 the edits are written into <paramref name="t"/>.</summary>
    private bool EditTarget(Target t, bool isNew)
    {
        var ui = _host.Ui;
        var draft = new Target { Id = t.Id };
        draft.CopyFrom(t);
        var labelBox = Form.TextBox(draft.Label, 240, v => draft.Label = v);
        string picked = draft.Program;
        var programBox = Form.TextBox(draft.Program, 220, v =>
        {
            draft.Program = v.Trim();
            if (draft.Program != picked) draft.WindowClass = ""; // typed by hand: the picked window no longer applies
        });
        var programRow = new StackPanel { Orientation = Orientation.Horizontal };
        programRow.Children.Add(programBox);
        var pickWindow = ui.WindowPicker(w =>
        {
            picked = w.Path;
            programBox.Text = w.Path;
            draft.Program = w.Path; draft.WindowClass = w.WindowClass;
            if (string.IsNullOrWhiteSpace(labelBox.Text)) labelBox.Text = AppLabel(w);
        });
        pickWindow.Margin = new Thickness(6, 0, 0, 0);
        programRow.Children.Add(pickWindow);
        var pick = ui.AppPicker(exe => { programBox.Text = exe; draft.Program = exe; draft.WindowClass = ""; }, 36);
        pick.Margin = new Thickness(6, 0, 0, 0); pick.ToolTip = "从运行中的程序选择";
        programRow.Children.Add(pick);
        var browse = ui.FileBrowse("浏览…", "程序 (*.exe)|*.exe", path => { programBox.Text = path; draft.Program = path; draft.WindowClass = ""; });
        browse.Margin = new Thickness(6, 0, 0, 0);
        programRow.Children.Add(browse);
        var content = Form.Stack(
            Form.Row("名称", labelBox),
            Form.Row("图标", ui.IconPicker(draft.Icon, v => draft.Icon = v)),
            Form.Row("方式", Form.Combo(new[] { ("paste", "粘贴到程序窗口"), ("launch", "带参数启动程序"), ("url", "打开 URL 协议") }, draft.Mode, v => draft.Mode = v, 200)),
            Form.Row("程序", programRow, "粘贴：用「拾取窗口」点选目标窗口最准确，也可填进程名或 exe 路径；启动：填 exe 路径"),
            Form.Row("参数 / URL", Form.TextBox(draft.Args, 320, v => draft.Args = v), "{text} = 选中内容"),
            Form.Row("未运行时启动", Form.Toggle(draft.LaunchIfMissing, v => draft.LaunchIfMissing = v)),
            Form.Row("只复制并切换，不模拟粘贴", Form.Toggle(draft.UsesManualPaste, v => draft.ManualPaste = v),
                "有的程序会丢弃模拟的按键（微信 4 就是），粘贴永远不会生效。开启后文本留在剪贴板里，切到窗口后自己按 Ctrl+V。选微信时自动开启。"));
        if (!ui.EditDialog(isNew ? "添加发送目标" : "编辑发送目标", content, 640)) return false;
        if (string.IsNullOrWhiteSpace(draft.Label)) return false;
        draft.Label = draft.Label.Trim();
        t.CopyFrom(draft);
        return true;
    }

    public FrameworkElement? CreateSettingsView()
    {
        // Edit a draft; it only takes effect when the settings window is saved.
        var d = SettingsDraft.Clone(_cfg);
        _draft = d;
        void Changed() => _host.Settings.Changed();
        _host.Settings.OnApply(() =>
        {
            _cfg = SettingsDraft.Clone(d);
            _host.Storage.Set(_cfg);
            _host.RefreshActions();
        });
        var ui = _host.Ui;
        var list = new StackPanel();

        void Rebuild()
        {
            list.Children.Clear();
            foreach (var t in d.Targets)
            {
                var target = t;
                var sub = target.Mode switch { "launch" => "启动 " + target.Program, "url" => target.Args, _ => Describe(target) };
                list.Children.Add(Form.ListRow(target.Label, sub, target.Enabled,
                    on => { target.Enabled = on; Changed(); },
                    () => { if (EditTarget(target, false)) { Changed(); Rebuild(); } },
                    () => { d.Targets.Remove(target); Changed(); Rebuild(); },
                    ui, target.Icon));
            }
        }
        _rebuildDraftList = Rebuild;
        Rebuild();

        return Form.Stack(
            Form.Card(Form.Row("合并为一个按钮", Form.Toggle(d.Fold, v => { d.Fold = v; Changed(); }),
                "开启多个目标时，操作条上只占一个「发送到」按钮，点击后展开选择目标")),
            list,
            Form.Buttons(Form.Button("＋ 添加目标", () => { var t = new Target { Label = "" }; if (EditTarget(t, true)) { d.Targets.Add(t); Changed(); Rebuild(); } }, accent: true)),
            Form.Hint("合并时按钮在第一个目标的位置。"));
    }

    public void Dispose() { }
}
