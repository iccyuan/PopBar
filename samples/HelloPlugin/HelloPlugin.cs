using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        new PluginAction
        {
            Id = "card",
            Label = "卡片",
            Icon = "square",
            IsApplicable = ctx => ctx.Text.Length > 0,
            // Bare = true: the panel is only a rounded container; this element is the whole thing.
            Execute = (ctx, ct) =>
            {
                var panel = _host.ShowPanel(new PanelOptions { Bare = true, AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY, Width = 260 });
                panel.SetContent(() =>
                {
                    var grid = new Grid { Height = 120 };
                    grid.Background = new LinearGradientBrush(Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x8B, 0x5C, 0xF6), 45);
                    grid.Children.Add(new TextBlock
                    {
                        Text = $"{ctx.Text.Length}", FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    });
                    grid.Children.Add(new TextBlock
                    {
                        Text = "字符 · Esc 关闭", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10),
                    });
                    return grid;
                });
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
