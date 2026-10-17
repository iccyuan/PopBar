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
    /// <param name="Source">The DLL a third-party plugin came from; null for built-ins.</param>
    public sealed record Loaded(IPopBarPlugin Plugin, PluginHost Host, bool BuiltIn, string? Error, string? Source = null);

    /// <summary>A third-party DLL that could not be loaded at all (no plugin object to show), with the reason.</summary>
    public sealed record LoadFailure(string Path, string Error);

    private readonly ConfigStore _store;
    private readonly Func<string, PluginHost> _hostFactory;
    private readonly List<Loaded> _plugins = new();
    private readonly Dictionary<string, (IPopBarPlugin Plugin, PluginAction Action)> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public IReadOnlyList<Loaded> Plugins => _plugins;

    /// <summary>Third-party DLLs that failed to load; shown on the plugin page so the reason is not only in the log.</summary>
    public IReadOnlyList<LoadFailure> LoadFailures => _failures;
    private readonly List<LoadFailure> _failures = new();

    public PluginManager(ConfigStore store, Func<string, PluginHost> hostFactory)
    {
        _store = store;
        _hostFactory = hostFactory;
    }

    /// <param name="external">Also load third-party plugins from the plugins folder (off in tests).</param>
    public void Load(IEnumerable<IPopBarPlugin> builtIns, bool external = true)
    {
        foreach (var p in builtIns) Register(p, builtIn: true);
        if (external) foreach (var (p, dll) in DiscoverExternal()) Register(p, builtIn: false, dll);
        RefreshActions();
        SyncConfig();
        Log.Info($"plugins loaded: {string.Join(", ", _plugins.Select(p => p.Plugin.Id + (p.Error != null ? "(error)" : "")))}");
    }

    private void Register(IPopBarPlugin plugin, bool builtIn, string? source = null)
    {
        var host = _hostFactory(plugin.Id);
        string? error = null;
        try { plugin.Initialize(host); }
        catch (Exception ex) { error = ex.Message; Log.Error($"plugin {plugin.Id} init failed", ex); }
        _plugins.Add(new Loaded(plugin, host, builtIn, error, source));
    }

    // ------------------------------------------------------------------ discovery

    private IEnumerable<(IPopBarPlugin Plugin, string Dll)> DiscoverExternal()
    {
        var dir = PluginStorage.PluginsDir;
        if (!Directory.Exists(dir)) yield break;
        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (Path.GetFileName(sub).StartsWith('.')) continue; // staged installs (.pending)
            foreach (var p in DiscoverIn(sub)) yield return p;
        }
    }

    /// <summary>
    /// Load a plugin folder installed while PopBar runs: its plugins start at once (actions join the bar as they
    /// declare, see <see cref="BarOptions.ShowByDefault"/>) without a restart. Call on the UI thread.
    /// </summary>
    public IReadOnlyList<Loaded> LoadInstalled(string folder)
    {
        int before = _plugins.Count;
        foreach (var (p, dll) in DiscoverIn(folder))
        {
            if (_plugins.Any(l => string.Equals(l.Plugin.Id, p.Id, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Warn($"plugin id {p.Id} from {dll} is already loaded; skipped");
                _failures.Add(new LoadFailure(dll, $"插件 id「{p.Id}」与已加载的插件重复"));
                continue;
            }
            Register(p, builtIn: false, dll);
        }
        RefreshActions();
        var added = _plugins.Skip(before).ToList();
        SyncConfig();
        // An update replaces an older version: the buttons of actions it no longer has leave the bar. Not for a plugin
        // that failed to start, whose actions are just missing for now.
        foreach (var l in added.Where(l => l.Error == null)) SyncConfig(l.Plugin.Id);
        ActionsSynced?.Invoke();
        return added;
    }

    /// <summary>
    /// Stop and drop the plugins loaded from <paramref name="folder"/>: dispose them and undo what they registered
    /// (hotkeys, tray entries, shared services, model providers, settings drafts). With <paramref name="dropActions"/>
    /// their buttons leave the bar too (uninstall); without, they stay for the new version to take over (update).
    /// Call on the UI thread. Returns the ids of the plugins removed.
    /// </summary>
    public List<string> Unload(string folder, bool dropActions)
    {
        var full = Path.GetFullPath(folder).TrimEnd('\\');
        bool From(string? source) => source != null && string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), full, StringComparison.OrdinalIgnoreCase);
        var gone = _plugins.Where(l => !l.BuiltIn && From(l.Source)).ToList();
        foreach (var l in gone)
        {
            try { l.Plugin.Dispose(); }
            catch (Exception ex) { Log.Error($"plugin {l.Plugin.Id} dispose", ex); }
            l.Host.Release(l.Plugin.GetType().Assembly);
            _plugins.Remove(l);
            Log.Info($"plugin unloaded: {l.Plugin.Id}");
        }
        _failures.RemoveAll(f => From(f.Path));
        var ids = gone.Select(l => l.Plugin.Id).ToList();
        RefreshActions();
        if (dropActions && ids.Count > 0)
            _store.Update(c => c.Actions.RemoveAll(a => a.IsPlugin && ids.Any(id => (a.Ref ?? "").StartsWith(id + ":", StringComparison.OrdinalIgnoreCase))));
        ActionsSynced?.Invoke();
        return ids;
    }

    private IEnumerable<(IPopBarPlugin Plugin, string Dll)> DiscoverIn(string sub)
    {
        foreach (var dll in Directory.GetFiles(sub, "*.dll"))
        {
            IPopBarPlugin[] found;
            try { found = LoadFrom(dll).ToArray(); }
            catch (BadImageFormatException) { continue; } // a native dependency shipped next to the plugin, not an assembly
            catch (Exception ex)
            {
                // GetTypes' own message only says "see LoaderExceptions": surface the first real cause.
                var cause = ex is ReflectionTypeLoadException rtl ? rtl.LoaderExceptions.FirstOrDefault(e => e != null) ?? ex : ex;
                Log.Warn($"plugin dll {dll}: {cause.Message}");
                _failures.Add(new LoadFailure(dll, cause.Message));
                continue;
            }
            foreach (var p in found) yield return (p, dll);
        }
    }

    private static IEnumerable<IPopBarPlugin> LoadFrom(string dll)
    {
        var alc = new PluginLoadContext(dll);
        var asm = PluginLoadContext.LoadUnlocked(alc, dll);
        foreach (var t in asm.GetTypes())
        {
            if (t.IsAbstract || !typeof(IPopBarPlugin).IsAssignableFrom(t)) continue;
            if (Activator.CreateInstance(t) is IPopBarPlugin p) yield return p;
        }
    }

    /// <summary>
    /// Isolates plugin dependencies but shares PopBar.Sdk (and WPF) with the host. Assemblies are read into memory
    /// rather than mapped from disk, so their files are never locked: a plugin can be updated or removed while PopBar
    /// runs (<see cref="Unload"/>). Not collectible: WPF keeps references into plugin assemblies (styles, templates,
    /// dispatcher callbacks), so an unloaded plugin's code stays in memory until exit, unused.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public PluginLoadContext(string path) : base(isCollectible: false) => _resolver = new AssemblyDependencyResolver(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "PopBar.Sdk") return null; // use the host's copy
            var p = _resolver.ResolveAssemblyToPath(name);
            return p != null ? LoadUnlocked(this, p) : null;
        }

        public static Assembly LoadUnlocked(AssemblyLoadContext alc, string path)
        {
            using var dll = new MemoryStream(File.ReadAllBytes(path));
            var pdbPath = Path.ChangeExtension(path, ".pdb");
            using var pdb = File.Exists(pdbPath) ? new MemoryStream(File.ReadAllBytes(pdbPath)) : null; // line numbers in plugin stack traces
            return alc.LoadFromStream(dll, pdb);
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
        Dictionary<string, bool> showByDefault;
        lock (_gate)
        {
            offered = _actions.ToDictionary(kv => kv.Key, kv => kv.Value.Action, StringComparer.OrdinalIgnoreCase);
            showByDefault = _actions.ToDictionary(kv => kv.Key, kv => BarOf(kv.Value.Plugin).ShowByDefault, StringComparer.OrdinalIgnoreCase);
        }
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
                var def = new ActionDefinition { Id = id.Replace(':', '-'), Type = ActionTypes.Plugin, Ref = id, Label = act.Label, Icon = act.Icon, Enabled = showByDefault[id] && act.EnabledByDefault };
                string owner = id[..id.IndexOf(':')];
                // Where the plugin asked for it, else next to its siblings (a second send-to target by the first), else last.
                int after = string.IsNullOrWhiteSpace(act.PlaceAfter) ? -1 : c.Actions.FindIndex(a =>
                    string.Equals(a.Id, act.PlaceAfter, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Ref, act.PlaceAfter, StringComparison.OrdinalIgnoreCase));
                if (after < 0) after = c.Actions.FindLastIndex(a => a.IsPlugin && (a.Ref ?? "").StartsWith(owner + ":", StringComparison.OrdinalIgnoreCase));
                if (after >= 0) c.Actions.Insert(after + 1, def);
                else c.Actions.Add(def);
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

    /// <summary>The merged bar button a plugin action belongs to, if its plugin merges its actions (see <see cref="BarOptions.MergeAs"/>).</summary>
    public Core.BarGroup? GroupOf(ActionDefinition def)
    {
        if (!def.IsPlugin || def.Ref == null || !TryGet(def.Ref, out var plugin, out _)) return null;
        var bar = BarOf(plugin);
        if (string.IsNullOrWhiteSpace(bar.MergeAs)) return null;
        return new Core.BarGroup(plugin.Id, bar.MergeAs, string.IsNullOrWhiteSpace(bar.MergeIcon) ? IconOf(plugin.Id) : bar.MergeIcon);
    }

    /// <summary>The plugin's <see cref="IPopBarPlugin.Bar"/>; the defaults when it throws.</summary>
    private static BarOptions BarOf(IPopBarPlugin plugin)
    {
        try { return plugin.Bar ?? BarOptions.Default; }
        catch (Exception ex) { Log.Warn($"plugin {plugin.Id} Bar threw: {ex.Message}"); return BarOptions.Default; }
    }

    /// <summary>The icon a plugin declared for itself (<see cref="IPopBarPlugin.Icon"/>); "puzzle" for unknown ids.</summary>
    public string IconOf(string pluginId)
    {
        var p = _plugins.FirstOrDefault(l => string.Equals(l.Plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase))?.Plugin;
        try { return p?.Icon is { Length: > 0 } icon ? icon : "puzzle"; }
        catch (Exception ex) { Log.Warn($"plugin {pluginId} Icon threw: {ex.Message}"); return "puzzle"; }
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
            try { l.Plugin.Dispose(); }
            catch (Exception ex) { Log.Error($"plugin {l.Plugin.Id} dispose", ex); }
        }
    }
}
