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
    }

    private IPluginHost _host = null!;
    private Settings _cfg = new();

    public string Id => "sendto";
    public string Name => "发送到应用";
    public string Description => "把选中内容粘贴进指定程序的窗口、作为参数启动程序，或打开自定义 URL 协议。";
    public Version Version => new(0, 2, 0);

    public static List<Target> DefaultTargets() => new()
    {
        new() { Id = "notepad", Label = "记事本", Icon = "type", Mode = "paste", Program = "notepad.exe" },
        new() { Id = "vscode", Label = "VS Code", Icon = "terminal", Mode = "paste", Program = "Code.exe", LaunchIfMissing = false, Enabled = false },
        new() { Id = "obsidian", Label = "Obsidian", Icon = "bookmark", Mode = "url", Args = "obsidian://new?content={text}", Enabled = false },
    };

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
        if (_cfg.Targets.Count == 0) { _cfg.Targets = DefaultTargets(); host.Storage.Set(_cfg); }
    }

    public IReadOnlyList<PluginAction> GetActions() => _cfg.Targets.Where(t => t.Enabled).Select(t => new PluginAction
    {
        Id = t.Id, Label = t.Label, Icon = t.Icon,
        Execute = (ctx, _) => { Send(t, ctx); return Task.CompletedTask; },
    }).ToList();

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
        void Save() => _host.Storage.Set(_cfg);
        var list = new StackPanel();

        void Rebuild()
        {
            list.Children.Clear();
            foreach (var t in _cfg.Targets)
            {
                var target = t;
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.Children.Add(Form.TextBox(target.Label, 160, v => { target.Label = v; Save(); }));
                var toggle = Form.Toggle(target.Enabled, v => { target.Enabled = v; Save(); });
                toggle.Margin = new Thickness(12, 0, 12, 0);
                Grid.SetColumn(toggle, 1); head.Children.Add(toggle);
                var del = Form.Button("删除", () => { _cfg.Targets.Remove(target); Save(); Rebuild(); });
                del.Margin = new Thickness(0);
                Grid.SetColumn(del, 2); head.Children.Add(del);

                var body = Form.Stack(
                    head,
                    Form.Row("方式", Form.Combo(new[] { ("paste", "粘贴到程序窗口"), ("launch", "带参数启动程序"), ("url", "打开 URL 协议") }, target.Mode, v => { target.Mode = v; Save(); }, 200)),
                    Form.Row("程序", Form.TextBox(target.Program, 300, v => { target.Program = v.Trim(); Save(); }), "粘贴方式填进程名（notepad.exe）；启动方式填可执行文件路径"),
                    Form.Row("参数 / URL", Form.TextBox(target.Args, 300, v => { target.Args = v; Save(); }), "{text} = 选中内容"),
                    Form.Row("找不到窗口时启动程序", Form.Toggle(target.LaunchIfMissing, v => { target.LaunchIfMissing = v; Save(); })));
                body.Margin = new Thickness(0, 8, 0, 4);
                list.Children.Add(Form.Card(body));
            }
        }
        Rebuild();

        return Form.Stack(
            Form.Hint("目标按钮会出现在悬浮条上（新目标需重启 PopBar 后出现，顺序在“动作”页调整）。"),
            list,
            Form.Buttons(Form.Button("＋ 添加目标", () => { _cfg.Targets.Add(new Target { Label = "新目标" }); Save(); Rebuild(); }, accent: true)));
    }

    public void Dispose() { }
}
