using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PopBar.Config;

/// <summary>Loads / saves <see cref="AppConfig"/> and notifies listeners when it changes.</summary>
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
    public static string FilePath { get; } = Path.Combine(Dir, "config.json");

    private readonly object _gate = new();
    private AppConfig _current = new();
    private readonly string _path;

    public ConfigStore() : this(FilePath) { }

    /// <summary>A store backed by another file (tests).</summary>
    internal ConfigStore(string path) => _path = path;

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
                if (File.Exists(_path))
                {
                    var cfg = Parse(File.ReadAllText(_path), out bool migrated);
                    _current = cfg;
                    IsFirstRun = false;
                    if (migrated) SaveLocked();
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

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_current, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("config save failed", ex);
        }
    }
}
