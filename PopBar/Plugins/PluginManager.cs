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

    /// <summary>Raised (any thread) after <see cref="Resync"/> changed the configured action list.</summary>
    public event Action? ActionsSynced;

    /// <summary>A plugin's action list changed: re-read it and bring the bar in line, dropping its vanished actions.</summary>
    public void Resync(string pluginId)
    {
        RefreshActions();
        SyncConfig(pluginId);
        ActionsSynced?.Invoke();
    }

    /// <summary>
    /// Append config entries for plugin actions the user has not seen yet (keeps user order / enabled flags), and
    /// refresh labels / icons of existing ones. With <paramref name="pruneOwner"/>, that plugin's entries whose action
    /// no longer exists are removed (only the owner knows it deleted them, not just failed to load).
    /// </summary>
    public void SyncConfig(string? pruneOwner = null)
    {
        Dictionary<string, PluginAction> offered;
        lock (_gate) offered = _actions.ToDictionary(kv => kv.Key, kv => kv.Value.Action, StringComparer.OrdinalIgnoreCase);
        bool Owned(ActionDefinition a) => pruneOwner != null && a.IsPlugin && (a.Ref ?? "").StartsWith(pruneOwner + ":", StringComparison.OrdinalIgnoreCase);

        var cfg = _store.Current;
        bool needed = offered.Any(o => !cfg.Actions.Any(a => a.IsPlugin && string.Equals(a.Ref, o.Key, StringComparison.OrdinalIgnoreCase)))
            || cfg.Actions.Any(a => a.IsPlugin && a.Ref != null && offered.TryGetValue(a.Ref, out var pa) && (a.Label != pa.Label || a.Icon != pa.Icon))
            || cfg.Actions.Any(a => Owned(a) && !offered.ContainsKey(a.Ref!));
        if (!needed) return;

        _store.Update(c =>
        {
            c.Actions.RemoveAll(a => Owned(a) && !offered.ContainsKey(a.Ref!));
            foreach (var a in c.Actions)
                if (a.IsPlugin && a.Ref != null && offered.TryGetValue(a.Ref, out var pa)) { a.Label = pa.Label; a.Icon = pa.Icon; }

            foreach (var (id, act) in offered)
            {
                if (c.Actions.Any(a => a.IsPlugin && string.Equals(a.Ref, id, StringComparison.OrdinalIgnoreCase))) continue;
                var def = new ActionDefinition { Id = id.Replace(':', '-'), Type = ActionTypes.Plugin, Ref = id, Label = act.Label, Icon = act.Icon, Enabled = act.EnabledByDefault };
                string owner = id[..id.IndexOf(':')];
                // The translation panel sits right after search, where the stock bar used to have "翻译".
                int search = id == "translate:panel" ? c.Actions.FindIndex(a => a.Id == "search") : -1;
                // Otherwise a new action joins its siblings (e.g. a second send-to target next to the first).
                int sibling = c.Actions.FindLastIndex(a => a.IsPlugin && (a.Ref ?? "").StartsWith(owner + ":", StringComparison.OrdinalIgnoreCase));
                if (search >= 0)
                    c.Actions.Insert(search + 1, def);
                else if (id == "ai:explain" && c.Actions.FindIndex(a => a.Ref == "translate:panel") is var t && t >= 0)
                    c.Actions.Insert(t + 1, def);
                else if (sibling >= 0)
                    c.Actions.Insert(sibling + 1, def);
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
