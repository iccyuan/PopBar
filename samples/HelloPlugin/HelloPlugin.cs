using System.Windows;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace HelloPlugin;

/// <summary>Example plugin: one button that shows word/char counts in a panel, plus a tiny settings view.</summary>
public sealed class HelloPlugin : IPopBarPlugin
{
    public sealed class Settings
    {
        public bool CountSpaces { get; set; } = true;
    }

    private IPluginHost _host = null!;
    private Settings _cfg = new();

    public string Id => "hello";
    public string Name => "Hello 示例插件";
    public string Description => "统计选中文本的字数，演示插件接口的最小用法。";
    public Version Version => new(1, 0, 0);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cfg = host.Storage.Get<Settings>();
        host.Log.Info("hello plugin loaded");
    }

    public IReadOnlyList<PluginAction> GetActions() => new[]
    {
        new PluginAction
        {
            Id = "count",
            Label = "字数",
            Icon = "hash",
            IsApplicable = ctx => ctx.Text.Length > 0,
            Execute = (ctx, ct) =>
            {
                var text = ctx.Text;
                int chars = _cfg.CountSpaces ? text.Length : text.Count(c => !char.IsWhiteSpace(c));
                int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                var panel = _host.ShowPanel(new PanelOptions { Title = "字数统计", Icon = "hash", AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY, Source = text, Width = 320 });
                panel.SetText($"字符：{chars}\n单词：{words}\n行数：{text.Split('\n').Length}");
                panel.SetStatus("Hello plugin");
                return Task.CompletedTask;
            },
        },
    };

    public FrameworkElement? CreateSettingsView()
    {
        // Edit a draft; it only takes effect when the settings window is saved.
        var d = SettingsDraft.Clone(_cfg);
        void Changed() => _host.Settings.Changed();
        _host.Settings.OnApply(() =>
        {
            _cfg = SettingsDraft.Clone(d);
            _host.Storage.Set(_cfg);
        });
        return Form.Card(
            Form.Row("字符数包含空白", Form.Toggle(d.CountSpaces, v => { d.CountSpaces = v; Changed(); })));
    }

    public void Dispose() { }
}
