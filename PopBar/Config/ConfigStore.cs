using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PopBar.Config;

/// <summary>
/// Loads / saves <see cref="AppConfig"/> and notifies listeners when it changes. In memory it is one object; on disk it
/// is split by subject so each file stays small and readable: <c>actions.json</c>, <c>services.json</c> (AI services,
/// keys encrypted), <c>proxy.json</c>, and <c>settings.json</c> for everything else. Only files whose content changed
/// are written. A single <c>config.json</c> from older versions is split on first load and kept as <c>config.json.bak</c>.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PopBar");
    /// <summary>The main settings file (the others sit next to it).</summary>
    public static string FilePath { get; } = Path.Combine(Dir, MainFile);

    /// <summary>The file holding everything not given to a <see cref="Parts"/> file.</summary>
    internal const string MainFile = "settings.json";
    /// <summary>The single file of versions before the split.</summary>
    internal const string LegacyFile = "config.json";
    /// <summary>Which top-level properties (as named in JSON) live in which file.</summary>
    internal static readonly (string File, string[] Keys)[] Parts =
    [
        ("actions.json", ["actions"]),
        ("services.json", ["llmServices", "llmDefault", "llm"]),
        ("proxy.json", ["proxy"]),
    ];

    private readonly object _gate = new();
    private AppConfig _current = new();
    private readonly string _dir;
    /// <summary>What each file was last read or written with, so unchanged files are not rewritten.</summary>
    private readonly Dictionary<string, string> _onDisk = new(StringComparer.OrdinalIgnoreCase);

    public ConfigStore() : this(Dir) { }

    /// <summary>A store in another folder (tests).</summary>
    internal ConfigStore(string dir) => _dir = dir;

    /// <summary>True when no config file existed at startup (first run).</summary>
    public bool IsFirstRun { get; private set; }

    public event Action<AppConfig>? Changed;

    /// <summary>
    /// The current config. Every change installs a new object (copy-on-write), so a reference read here never changes
    /// underneath its reader on another thread (the hook and capture threads iterate its lists without a lock). Never
    /// mutate it; use <see cref="Update"/>.
    /// </summary>
    public AppConfig Current => Volatile.Read(ref _current);

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                var files = Parts.Select(p => p.File).Prepend(MainFile).Where(f => File.Exists(Path.Combine(_dir, f))).ToList();
                var legacy = Path.Combine(_dir, LegacyFile);
                string? json = null;
                bool split = false;
                if (files.Count > 0)
                {
                    // one object again: every file's properties side by side
                    var merged = new JsonObject();
                    foreach (var f in files)
                    {
                        var text = File.ReadAllText(Path.Combine(_dir, f));
                        _onDisk[f] = text;
                        if (JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o)
                            foreach (var (key, value) in o.ToList()) { o.Remove(key); merged[key] = value; }
                    }
                    json = merged.ToJsonString();
                }
                else if (File.Exists(legacy))
                {
                    json = File.ReadAllText(legacy);
                    split = true;
                }
                if (json != null)
                {
                    _current = Parse(json, out bool migrated);
                    IsFirstRun = false;
                    if (migrated || split) SaveLocked();
                    if (split)
                    {
                        File.Move(legacy, legacy + ".bak", overwrite: true);
                        Log.Info("config.json split into " + string.Join(", ", Parts.Select(p => p.File).Prepend(MainFile)) + "; the old file is config.json.bak");
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("config load failed, using defaults", ex);
            }
            _current = new AppConfig();
            IsFirstRun = true;
            SaveLocked();
        }
    }

    /// <summary>Read a config file's text and bring it up to the current schema; <paramref name="migrated"/> = it should be re-saved.</summary>
    internal static AppConfig Parse(string json, out bool migrated)
    {
        // Files written before ConfigVersion existed deserialize as 0 and get migrated below.
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        if (!json.Contains("\"configVersion\"", StringComparison.OrdinalIgnoreCase)) cfg.ConfigVersion = 0;
        if (cfg.Actions.Count == 0) cfg.Actions = Actions.DefaultActions.Create();
        migrated = false;
        if (cfg.ConfigVersion < 2)
        {
            // config v2: theme follows the system by default; the stock upper/lower buttons were dropped.
            if (cfg.Theme == "dark") cfg.Theme = "system";
            cfg.Actions.RemoveAll(a => a.Type == Actions.ActionTypes.Transform && a.Id is "upper" or "lower");
            cfg.ConfigVersion = 2;
            migrated = true;
        }
        if (cfg.ConfigVersion < 3)
        {
            // config v3: the translate plugin's panel replaces the stock "open Google Translate" URL action.
            cfg.Actions.RemoveAll(a => a.Id == "translate" && a.Type == Actions.ActionTypes.Url
                && (a.Url ?? "").Contains("translate.google.", StringComparison.OrdinalIgnoreCase));
            cfg.ConfigVersion = 3;
            migrated = true;
        }
        if (cfg.ConfigVersion < 4)
        {
            // config v4: the stock send-to target is a generic "发送到" (Notepad by default), not "记事本".
            foreach (var a in cfg.Actions.Where(a => a.Ref == "sendto:notepad" && a.Label == "记事本"))
            {
                a.Label = "发送到";
                if (a.Icon == "type") a.Icon = "send";
            }
            cfg.ConfigVersion = 4;
            migrated = true;
        }
        if (cfg.ConfigVersion < 5)
        {
            // config v5: QR and sticky-note buttons start switched off (they briefly shipped on).
            foreach (var a in cfg.Actions.Where(a => a.Ref is "qr:make" or "notes:pin")) a.Enabled = false;
            cfg.ConfigVersion = 5;
            migrated = true;
        }
        if (cfg.ConfigVersion < 6)
        {
            // config v6: the bar waits for the reveal key by default, and the theme follows the system again.
            cfg.ShowMode = "key";
            cfg.Theme = "system";
            cfg.ConfigVersion = 6;
            migrated = true;
        }
        if (cfg.ConfigVersion < 7)
        {
            // config v7: several model services (one per provider) with a default, instead of a single "llm" block.
            if (cfg.Llm != null && cfg.LlmServices.Count == 0)
            {
                cfg.LlmServices.Add(cfg.Llm);
                cfg.LlmDefault = cfg.Llm.Protocol;
            }
            cfg.Llm = null;
            cfg.ConfigVersion = 7;
            migrated = true;
        }
        if (cfg.ConfigVersion < 8)
        {
            // config v8: terminals / remote desktops / VMs are skipped by the code itself; the list keeps only the user's own.
            cfg.NoClipboardApps.RemoveAll(p => Core.ClipboardCaptureStrategy.BuiltinNoClipboardApps.Contains(p));
            cfg.ConfigVersion = 8;
            migrated = true;
        }
        if (cfg.ConfigVersion < 9)
        {
            // config v9: the bar is revealed by tapping Shift; Ctrl (the old default) begins every Ctrl+C.
            if (string.IsNullOrWhiteSpace(cfg.ShowKey) || string.Equals(cfg.ShowKey, "ctrl", StringComparison.OrdinalIgnoreCase)) cfg.ShowKey = "shift";
            cfg.ConfigVersion = 9;
            migrated = true;
        }
        if (cfg.ConfigVersion < 10)
        {
            // config v10: Alt is the default reveal key (Shift begins capital letters and Shift+arrow selections).
            if (string.Equals(cfg.ShowKey, "shift", StringComparison.OrdinalIgnoreCase)) cfg.ShowKey = "alt";
            cfg.ConfigVersion = 10;
            migrated = true;
        }
        if (cfg.ConfigVersion < 11)
        {
            // config v11: the proxy is type / host / port / login instead of one address to type.
            if (!string.IsNullOrWhiteSpace(cfg.Proxy.Address) && Core.ProxyManager.TryParseAddress(cfg.Proxy.Address, out var uri, out var login, out _))
            {
                cfg.Proxy.Scheme = uri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase) ? ProxySchemes.Socks5 : ProxySchemes.Http;
                cfg.Proxy.Host = uri.Host;
                cfg.Proxy.Port = uri.Port;
                cfg.Proxy.User = login?.UserName ?? "";
                cfg.Proxy.Password = login?.Password ?? "";
            }
            cfg.Proxy.Address = null;
            cfg.ConfigVersion = 11;
            migrated = true;
        }
        // Keys from files written before they were encrypted: re-save once so the plain text leaves the disk.
        if (cfg.LlmServices.Any(l => l.ApiKeyNeedsProtecting)) migrated = true;
        return cfg;
    }

    /// <summary>
    /// Apply a mutation to a private copy, persist it and swap it in, then broadcast. Writers are serialized; readers
    /// holding the previous object keep a consistent (if older) view. The callback must not keep the object it is given.
    /// </summary>
    public void Update(Action<AppConfig> mutate)
    {
        AppConfig next;
        lock (_gate)
        {
            next = Clone(_current);
            mutate(next);
            Volatile.Write(ref _current, next);
            SaveLocked();
        }
        Changed?.Invoke(next);
    }

    /// <summary>Deep copy through the same JSON form the file uses, so a copy is exactly what a reload would give.</summary>
    public static AppConfig Clone(AppConfig c) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(c, JsonOptions), JsonOptions) ?? new AppConfig();

    /// <summary>Write each file whose share of the config changed (each through a temp file, so a crash never leaves half a file).</summary>
    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var (file, json) in Split(_current))
            {
                if (_onDisk.TryGetValue(file, out var old) && old == json) continue;
                var path = Path.Combine(_dir, file);
                File.WriteAllText(path + ".tmp", json);
                File.Move(path + ".tmp", path, overwrite: true);
                _onDisk[file] = json;
            }
        }
        catch (Exception ex)
        {
            Log.Error("config save failed", ex);
        }
    }

    /// <summary>The config as file name → that file's JSON: the <see cref="Parts"/> properties to their files, the rest to <see cref="MainFile"/>.</summary>
    internal static IEnumerable<(string File, string Json)> Split(AppConfig c)
    {
        var all = JsonSerializer.SerializeToNode(c, JsonOptions)!.AsObject();
        var writer = new JsonSerializerOptions { WriteIndented = true, Encoder = JsonOptions.Encoder };
        foreach (var (file, keys) in Parts)
        {
            var part = new JsonObject();
            foreach (var key in keys)
                if (all.Remove(key, out var value)) part[key] = value;
            yield return (file, part.ToJsonString(writer));
        }
        yield return (MainFile, all.ToJsonString(writer));
    }
}
