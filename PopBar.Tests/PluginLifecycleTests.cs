using System.IO;
using System.Windows.Threading;
using PopBar.Config;
using PopBar.Core;
using PopBar.Plugins;
using PopBar.Sdk;

namespace PopBar.Tests;

/// <summary>Switching a plugin off stops it for real; its data lives apart from the folder it is installed in.</summary>
public sealed class PluginLifecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "popbar-life-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Translator : ITextTranslator
    {
        public Task<TranslationResult> TranslateAsync(string text, string? targetLang = null, CancellationToken ct = default) =>
            Task.FromResult(new TranslationResult(text, "", "", "fake"));
    }

    /// <summary>Provides a service and a tray entry, like the translate plugin; counts its lifecycle calls.</summary>
    public sealed class ProviderPlugin : IPopBarPlugin
    {
        public static int Initialized, Disposed;
        public string Id => "prov";
        public string Name => "prov";
        public string Description => "";
        public Version Version => new(1, 0);
        public void Initialize(IPluginHost host)
        {
            Initialized++;
            host.Services.Provide<ITextTranslator>(new Translator());
            host.AddTrayMenuItem("go", "Go", () => { });
        }
        public IReadOnlyList<PluginAction> GetActions() => [new() { Id = "a", Label = "a", Execute = (_, _) => Task.CompletedTask }];
        public void Dispose() => Disposed++;
    }

    public sealed class OffPlugin : IPopBarPlugin
    {
        public static int Initialized;
        public string Id => "off";
        public string Name => "off";
        public string Description => "";
        public Version Version => new(1, 0);
        public void Initialize(IPluginHost host) => Initialized++;
        public IReadOnlyList<PluginAction> GetActions() => [new() { Id = "b", Label = "b", Execute = (_, _) => Task.CompletedTask }];
        public void Dispose() { }
    }

    /// <summary>PluginHost needs the hotkey message window, which needs an STA thread with a dispatcher.</summary>
    private static void OnSta(Action body)
    {
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { err = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw new Exception("on STA: " + err.Message, err);
    }

    [Fact]
    public void A_plugin_switched_off_is_stopped_and_starts_again_when_switched_on()
    {
        OnSta(() =>
        {
            ProviderPlugin.Initialized = ProviderPlugin.Disposed = OffPlugin.Initialized = 0;
            var store = new ConfigStore(_dir);
            store.Load();
            store.Update(c => c.DisabledPlugins = ["off"]);
            var proxies = new ProxyManager(store);
            var tray = new List<(string Id, TrayItem Item)>();
            var services = new HostServices
            {
                Store = store, Dispatcher = Dispatcher.CurrentDispatcher, Llm = new LlmService(store, proxies), Proxies = proxies,
                Hotkeys = new HotkeyManager(), Panels = new PanelRegistry(),
                AddTrayItem = (id, item) => tray.Add((id, item)),
                RemoveTrayItems = plugin => tray.RemoveAll(i => i.Id.StartsWith(plugin + ":", StringComparison.OrdinalIgnoreCase)),
                ShowPopup = _ => { }, OpenSettings = _ => { }, RunAction = (_, _) => Task.CompletedTask, ResyncActions = _ => { },
            };
            var m = new PluginManager(store, id => new PluginHost(id, services));
            m.Load([new ProviderPlugin(), new OffPlugin()], external: false);

            // switched off before start: never initialized, its button is unknown but stays configured
            Assert.Equal(0, OffPlugin.Initialized);
            Assert.True(m.Plugins.Single(p => p.Plugin.Id == "off").Stopped);
            Assert.False(m.TryGet("off:b", out _, out _));

            var first = m.Plugins.Single(p => p.Plugin.Id == "prov").Plugin;
            Assert.NotNull(services.Services.Get<ITextTranslator>());
            Assert.Single(tray);

            m.SetEnabled("prov", false);
            Assert.Equal(1, ProviderPlugin.Disposed);
            Assert.Null(services.Services.Get<ITextTranslator>());   // others see it as absent
            Assert.Empty(tray);
            Assert.False(m.TryGet("prov:a", out _, out _));
            Assert.Contains(store.Current.Actions, a => a.Ref == "prov:a");   // its button waits for it
            Assert.NotSame(first, m.Plugins.Single(p => p.Plugin.Id == "prov").Plugin);

            m.SetEnabled("prov", false);   // idempotent
            Assert.Equal(1, ProviderPlugin.Disposed);

            m.SetEnabled("prov", true);
            Assert.Equal(2, ProviderPlugin.Initialized);   // a fresh instance
            Assert.NotNull(services.Services.Get<ITextTranslator>());
            Assert.Single(tray);
            Assert.True(m.TryGet("prov:a", out _, out _));

            store.Update(c => c.DisabledPlugins = []);
            m.ApplyEnabled(store.Current);
            Assert.Equal(1, OffPlugin.Initialized);
            Assert.True(m.TryGet("off:b", out _, out _));
            m.Dispose();
        });
    }

    [Fact]
    public void Plugin_data_moves_out_of_a_folder_an_update_would_replace()
    {
        var plugins = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(Path.Combine(plugins, "ocr"));
        File.WriteAllText(Path.Combine(plugins, "ocr", "last.png"), "x");
        Directory.CreateDirectory(Path.Combine(plugins, "hello"));
        File.WriteAllText(Path.Combine(plugins, "hello", "Hello.dll"), "x");   // an install folder named like the id

        var ocr = PluginStorage.DataDirOf(plugins, "ocr");
        Assert.Equal(Path.Combine(plugins, ".data", "ocr"), ocr);
        Assert.True(File.Exists(Path.Combine(ocr, "last.png")));
        Assert.False(Directory.Exists(Path.Combine(plugins, "ocr")));

        var hello = PluginStorage.DataDirOf(plugins, "hello");
        Assert.Equal(Path.Combine(plugins, ".data", "hello"), hello);
        Assert.True(File.Exists(Path.Combine(plugins, "hello", "Hello.dll")));   // installed files stay put
        Assert.False(Directory.Exists(hello));
    }
}
