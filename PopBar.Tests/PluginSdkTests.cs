using System.IO;
using PopBar.Config;
using PopBar.Plugins;
using PopBar.Sdk;

namespace PopBar.Tests;

/// <summary>SDK compatibility, settings drafts, action failures and dev folders: the plugin author's side of the host.</summary>
public sealed class PluginSdkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "popbar-sdk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("1.0", "1.0", true)]
    [InlineData("1.0", "1.3", true)]    // older plugin, newer PopBar: fine
    [InlineData("1.4", "1.3", false)]   // uses API this PopBar lacks
    [InlineData("2.0", "1.3", false)]
    [InlineData("1.2", "2.0", false)]   // built before a breaking change
    [InlineData("0.2", "1.0", true)]    // before the SDK had its own version
    public void Plugins_run_on_the_same_major_with_no_newer_minor(string built, string host, bool ok) =>
        Assert.Equal(ok, PluginCompat.Problem(Version.Parse(built), Version.Parse(host)) == null);

    [Fact]
    public void The_sdk_version_a_dll_was_built_against_is_read_from_its_metadata()
    {
        Assert.Equal(PluginCompat.Host, PluginCompat.SdkOf(typeof(PluginSdkTests).Assembly.Location));   // this test assembly references the SDK
        Assert.Null(PluginCompat.SdkOf(typeof(object).Assembly.Location));
        Assert.Equal(1, PluginCompat.Host.Major);   // its own API version, not the app's
    }

    private sealed class Draft { public bool On { get; set; } public List<string> Items { get; set; } = []; }

    [Fact]
    public void A_draft_set_back_to_how_it_started_is_no_longer_unsaved()
    {
        var hub = new SettingsHub();
        var session = new SettingsSession(hub, "p");
        var d = new Draft { Items = ["a"] };
        int applied = 0;
        session.OnApply(d, () => applied++);

        d.On = true; session.Changed();
        Assert.True(hub.AnyPending);
        d.On = false; session.Changed();
        Assert.False(hub.AnyPending);

        d.Items.Add("b"); session.Changed();
        hub.ApplyAll();
        Assert.Equal(1, applied);
        Assert.False(hub.AnyPending);
        d.Items.Remove("b"); session.Changed();   // compared with what was saved, not with the first state
        Assert.True(hub.AnyPending);
    }

    [Fact]
    public void Untracked_drafts_stay_unsaved_after_any_change()
    {
        var hub = new SettingsHub();
        var session = new SettingsSession(hub, "p");
        session.OnApply(() => { });
        session.Changed();
        Assert.True(hub.AnyPending);   // the host cannot tell a revert without the draft
    }

    private sealed class Throwing : IPopBarPlugin
    {
        public string Id => "boom";
        public string Name => "boom";
        public string Description => "";
        public Version Version => new(1, 0);
        public void Initialize(IPluginHost host) { }
        public IReadOnlyList<PluginAction> GetActions() =>
            [new() { Id = "go", Label = "炸", Execute = (_, _) => throw new InvalidOperationException("坏了") }];
        public void Dispose() { }
    }

    private ConfigStore Store()
    {
        var store = new ConfigStore(_dir);
        store.Load();
        return store;
    }

    [Fact]
    public async Task A_failing_action_is_reported_for_the_user()
    {
        var m = new PluginManager(Store(), _ => null!);
        m.Load([new Throwing()], external: false);
        (PluginAction Action, Exception Error)? seen = null;
        m.ActionFailed += (a, _, ex) => seen = (a, ex);
        await m.ExecuteAsync("boom:go", new SelectionContext("x", IntPtr.Zero, "", 0, 0));
        Assert.Equal("炸", seen?.Action.Label);
        Assert.Equal("坏了", seen?.Error.Message);
    }

    /// <summary>The Hello sample's build output, as a plugin author's build folder.</summary>
    private static string HelloBuild()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "PopBar.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var dll = Directory.GetFiles(Path.Combine(root!, "plugins", "Hello", "bin"), "HelloPlugin.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        Assert.True(dll != null, "build plugins/Hello first");
        return Path.GetDirectoryName(dll)!;
    }

    [Fact]
    public void A_dev_folder_loads_and_reloads_without_installing()
    {
        var folder = HelloBuild();
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                var store = Store();
                var proxies = new Core.ProxyManager(store);
                var services = new HostServices
                {
                    Store = store, Dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher, Llm = new Core.LlmService(store, proxies),
                    Proxies = proxies, Hotkeys = new HotkeyManager(), Panels = new PanelRegistry(),
                    AddTrayItem = (_, _) => { }, RemoveTrayItems = _ => { }, ShowPopup = _ => { }, OpenSettings = _ => { },
                    RunAction = (_, _) => Task.CompletedTask, ResyncActions = _ => { },
                };
                var m = new PluginManager(store, id => new PluginHost(id, services));
                m.Load([], external: false);

                var first = m.LoadDev(folder);
                Assert.Equal("hello", Assert.Single(first).Plugin.Id);
                Assert.Null(first[0].Error);
                Assert.True(m.IsDev(first[0]));
                Assert.True(m.TryGet("hello:count", out _, out _));

                var again = m.LoadDev(folder);   // after a rebuild: the old one goes, the new one comes
                Assert.Single(again);
                Assert.Single(m.Plugins, p => p.Plugin.Id == "hello");
                Assert.NotSame(first[0].Plugin, again[0].Plugin);
                m.Dispose();
            }
            catch (Exception ex) { err = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw new Exception("on STA: " + err.Message, err);
    }
}
