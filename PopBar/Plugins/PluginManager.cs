using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using PopBar.Actions;
using PopBar.Config;
using PopBar.Sdk;

namespace PopBar.Plugins;

/// <summary>Loads built-in and external plugins, keeps their actions in sync with the config, and executes them.</summary>
internal sealed class PluginManager : IDisposable
{
    public sealed record Loaded(IPopBarPlugin Plugin, PluginHost Host, bool BuiltIn, string? Error);

    private readonly ConfigStore _store;
    private readonly Func<string, PluginHost> _hostFactory;
    private readonly List<Loaded> _plugins = new();
    private readonly Dictionary<string, (IPopBarPlugin Plugin, PluginAction Action)> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public IReadOnlyList<Loaded> Plugins => _plugins;

    public PluginManager(ConfigStore store, Func<string, PluginHost> hostFactory)
    {
        _store = store;
        _hostFactory = hostFactory;
    }

    public void Load(IEnumerable<IPopBarPlugin> builtIns)
    {
        foreach (var p in builtIns) Register(p, builtIn: true);
        foreach (var p in DiscoverExternal()) Register(p, builtIn: false);
        RefreshActions();
        SyncConfig();
        Log.Info($"plugins loaded: {string.Join(", ", _plugins.Select(p => p.Plugin.Id + (p.Error != null ? "(error)" : "")))}");
    }

    private void Register(IPopBarPlugin plugin, bool builtIn)
    {
        var host = _hostFactory(plugin.Id);
        string? error = null;
        try { plugin.Initialize(host); }
        catch (Exception ex) { error = ex.Message; Log.Error($"plugin {plugin.Id} init failed", ex); }
        _plugins.Add(new Loaded(plugin, host, builtIn, error));
    }

    // ------------------------------------------------------------------ discovery

    private static IEnumerable<IPopBarPlugin> DiscoverExternal()
    {
        var dir = PluginStorage.PluginsDir;
        if (!Directory.Exists(dir)) yield break;
        foreach (var sub in Directory.GetDirectories(dir))
        {
            foreach (var dll in Directory.GetFiles(sub, "*.dll"))
            {
                IPopBarPlugin[] found;
                try { found = LoadFrom(dll).ToArray(); }
                catch (Exception ex) { Log.Warn($"plugin dll {dll}: {ex.Message}"); continue; }
                foreach (var p in found) yield return p;
            }
        }
    }

    private static IEnumerable<IPopBarPlugin> LoadFrom(string dll)
    {
        var alc = new PluginLoadContext(dll);
        var asm = alc.LoadFromAssemblyPath(dll);
        foreach (var t in asm.GetTypes())
        {
            if (t.IsAbstract || !typeof(IPopBarPlugin).IsAssignableFrom(t)) continue;
            if (Activator.CreateInstance(t) is IPopBarPlugin p) yield return p;
        }
    }

    /// <summary>Isolates plugin dependencies but shares PopBar.Sdk (and WPF) with the host.</summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public PluginLoadContext(string path) : base(isCollectible: false) => _resolver = new AssemblyDependencyResolver(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "PopBar.Sdk") return null; // use the host's copy
            var p = _resolver.ResolveAssemblyToPath(name);
            return p != null ? LoadFromAssemblyPath(p) : null;
        }
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Re-read every plugin's action list (call after plugin settings change).</summary>
    public void RefreshActions()
    {
        lock (_gate)
        {
            _actions.Clear();
            foreach (var l in _plugins)
            {
                if (l.Error != null) continue;
                IReadOnlyList<PluginAction> acts;
                try { acts = l.Plugin.GetActions(); }
                catch (Exception ex) { Log.Error($"plugin {l.Plugin.Id} GetActions", ex); continue; }
                foreach (var a in acts) _actions[$"{l.Plugin.Id}:{a.Id}"] = (l.Plugin, a);
            }
        }
    }

    /// <summary>Append config entries for plugin actions the user has not seen yet (keeps user order / labels / enabled flags).</summary>
    public void SyncConfig()
    {
        List<(string Id, IPopBarPlugin P, PluginAction A)> snapshot;
        lock (_gate) snapshot = _actions.Select(kv => (kv.Key, kv.Value.Plugin, kv.Value.Action)).ToList();
        var cfg = _store.Current;
        var missing = snapshot.Where(s => !cfg.Actions.Any(a => a.IsPlugin && string.Equals(a.Ref, s.Id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count == 0) return;
        _store.Update(c =>
        {
            foreach (var m in missing)
            {
                var def = new ActionDefinition { Id = m.Id.Replace(':', '-'), Type = ActionTypes.Plugin, Ref = m.Id, Label = m.A.Label, Icon = m.A.Icon, Enabled = true };
                // The translation panel sits right after search, where the stock bar used to have "翻译".
                int search = m.Id == "translate:panel" ? c.Actions.FindIndex(a => a.Id == "search") : -1;
                if (search >= 0)
                    c.Actions.Insert(search + 1, def);
                else if (m.Id == "ai:explain" && c.Actions.FindIndex(a => a.Ref == "translate:panel") is var t && t >= 0)
                    c.Actions.Insert(t + 1, def);
                else
                    c.Actions.Add(def);
            }
        });
    }

    public bool TryGet(string actionId, out IPopBarPlugin plugin, out PluginAction action)
    {
        lock (_gate)
        {
            if (_actions.TryGetValue(actionId, out var e)) { plugin = e.Plugin; action = e.Action; return true; }
        }
        plugin = null!; action = null!;
        return false;
    }

    /// <summary>Does this plugin action exist, is its plugin enabled, and does it apply to the selection?</summary>
    public bool IsApplicable(ActionDefinition def, SelectionContext ctx, AppConfig cfg)
    {
        if (def.Ref == null || !TryGet(def.Ref, out var plugin, out var action)) return false;
        if (!cfg.IsPluginEnabled(plugin.Id)) return false;
        try { return action.IsApplicable?.Invoke(ctx) ?? true; }
        catch (Exception ex) { Log.Warn($"{def.Ref} IsApplicable threw: {ex.Message}"); return false; }
    }

    public async Task ExecuteAsync(string actionId, SelectionContext ctx, CancellationToken ct = default)
    {
        if (!TryGet(actionId, out var plugin, out var action)) { Log.Warn("unknown plugin action " + actionId); return; }
        try { await action.Execute(ctx, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"plugin action {actionId} failed", ex); }
    }

    public void Dispose()
    {
        foreach (var l in _plugins)
        {
            try { l.Plugin.Dispose(); } catch { }
        }
    }
}
