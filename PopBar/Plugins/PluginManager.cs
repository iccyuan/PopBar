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

    public void Load(IEnumerable<IPopBarPlugin> builtIns)
    {
        foreach (var p in builtIns) Register(p, builtIn: true);
        foreach (var (p, dll) in DiscoverExternal()) Register(p, builtIn: false, dll);
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

    /// <summary>
    /// Isolates plugin dependencies but shares PopBar.Sdk (and WPF) with the host. Not collectible: WPF keeps
    /// references into plugin assemblies (styles, templates, dispatcher callbacks), so unloading would not free them;
    /// a fixed plugin is picked up on the next start.
    /// </summary>
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

    /// <summary>
    /// The fold group a plugin action belongs to on the bar: the whole plugin when the user folded it on the "动作" page
    /// (one button named after the plugin), else the group its plugin declared, else none.
    /// </summary>
    public Core.BarGroup? GroupOf(ActionDefinition def)
    {
        if (!def.IsPlugin || def.Ref == null || !TryGet(def.Ref, out var plugin, out _)) return null;
        if (_store.Current.IsPluginFolded(plugin.Id)) return new Core.BarGroup(plugin.Id + "/*", plugin.Name, PluginIcon(plugin.Id));
        return DeclaredGroupOf(def);
    }

    /// <summary>The fold group a plugin action belongs to, if its plugin declared one (see <see cref="PluginAction.Group"/>).</summary>
    public Core.BarGroup? DeclaredGroupOf(ActionDefinition def)
    {
        if (!def.IsPlugin || def.Ref == null || !TryGet(def.Ref, out var plugin, out var action) || string.IsNullOrWhiteSpace(action.Group)) return null;
        return new Core.BarGroup(plugin.Id + "/" + action.Group, action.Group, action.GroupIcon);
    }

    /// <summary>Icon standing for a plugin as a whole (settings cards, folded bar button).</summary>
    public static string PluginIcon(string id) => id switch
    {
        "translate" => "languages", "ai" => "sparkles", "ocr" => "type", "screenshot" => "crop", "sendto" => "send",
        "qr" => "qr-code", "codec" => "code", "notes" => "sticky-note", "screentranslate" => "scan-text", _ => "puzzle",
    };

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
