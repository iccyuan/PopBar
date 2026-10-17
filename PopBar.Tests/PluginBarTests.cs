using System.IO;
using PopBar.Config;
using PopBar.Plugins;
using PopBar.Sdk;

namespace PopBar.Tests;

/// <summary>How plugin actions join the bar: placement, default visibility and merging, as the plugins declare them.</summary>
public sealed class PluginBarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "popbar-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigStore _store;

    public PluginBarTests()
    {
        _store = new ConfigStore(Path.Combine(_dir, "config.json"));
        _store.Load(); // first run: defaults, written to the temp file only
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakePlugin(string id, BarOptions bar, params PluginAction[] actions) : IPopBarPlugin
    {
        public string Id => id;
        public string Name => id;
        public string Description => "";
        public Version Version => new(1, 0);
        public BarOptions Bar => bar;
        public void Initialize(IPluginHost host) { }
        public IReadOnlyList<PluginAction> GetActions() => actions;
        public void Dispose() { }
    }

    private static PluginAction Act(string id, string? after = null, bool on = true) =>
        new() { Id = id, Label = id, PlaceAfter = after, EnabledByDefault = on, Execute = (_, _) => Task.CompletedTask };

    private PluginManager Load(params IPopBarPlugin[] plugins)
    {
        var m = new PluginManager(_store, _ => null!);
        m.Load(plugins, external: false);
        return m;
    }

    private List<string> Ids => _store.Current.Actions.Select(a => a.Ref ?? a.Id).ToList();

    [Fact]
    public void New_actions_go_where_the_plugin_asked_else_last()
    {
        Load(new FakePlugin("p", BarOptions.Default, Act("a", after: "copy"), Act("b"), Act("c", after: "no-such-action")));
        var ids = Ids;
        Assert.Equal(ids.IndexOf("copy") + 1, ids.IndexOf("p:a"));
        Assert.Equal(ids.IndexOf("p:a") + 1, ids.IndexOf("p:b"));   // next to its sibling
        Assert.Equal(ids.IndexOf("p:b") + 1, ids.IndexOf("p:c"));   // unknown anchor: with its siblings
    }

    [Fact]
    public void Plugin_decides_whether_its_actions_start_on_the_bar()
    {
        Load(new FakePlugin("shown", BarOptions.Default, Act("x"), Act("off", on: false)),
             new FakePlugin("hidden", new BarOptions { ShowByDefault = false }, Act("y")));
        var acts = _store.Current.Actions;
        Assert.True(acts.Single(a => a.Ref == "shown:x").Enabled);
        Assert.False(acts.Single(a => a.Ref == "shown:off").Enabled);
        Assert.False(acts.Single(a => a.Ref == "hidden:y").Enabled);
    }

    [Fact]
    public void Merge_is_declared_per_plugin()
    {
        var m = Load(new FakePlugin("merged", new BarOptions { MergeAs = "工具" }, Act("a"), Act("b")),
                     new FakePlugin("plain", BarOptions.Default, Act("c")));
        var acts = _store.Current.Actions;
        var g = m.GroupOf(acts.Single(a => a.Ref == "merged:a"));
        Assert.NotNull(g);
        Assert.Equal("工具", g!.Label);
        Assert.Equal("puzzle", g.Icon); // no MergeIcon: the plugin's own icon
        Assert.Equal(g, m.GroupOf(acts.Single(a => a.Ref == "merged:b")));
        Assert.Null(m.GroupOf(acts.Single(a => a.Ref == "plain:c")));
    }

    [Fact]
    public void Existing_order_and_switches_are_kept()
    {
        Load(new FakePlugin("p", BarOptions.Default, Act("a")));
        _store.Update(c =>
        {
            var a = c.Actions.Single(x => x.Ref == "p:a");
            a.Enabled = false;
            c.Actions.Remove(a);
            c.Actions.Insert(0, a);
        });
        Load(new FakePlugin("p", BarOptions.Default, Act("a", after: "copy"))); // a restart
        var first = _store.Current.Actions[0];
        Assert.Equal("p:a", first.Ref);
        Assert.False(first.Enabled);
    }
}
