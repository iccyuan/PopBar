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

    /// <summary>True when no config file existed at startup (first run).</summary>
    public bool IsFirstRun { get; private set; }

    public event Action<AppConfig>? Changed;

    /// <summary>Snapshot of the current config. Treat as read-only; use <see cref="Update"/> to change.</summary>
    public AppConfig Current { get { lock (_gate) return _current; } }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    // Files written before ConfigVersion existed deserialize as 0 and get migrated below.
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                    if (!json.Contains("\"configVersion\"", StringComparison.OrdinalIgnoreCase)) cfg.ConfigVersion = 0;
                    if (cfg.Actions.Count == 0) cfg.Actions = Actions.DefaultActions.Create();
                    bool migrated = false;
                    if (cfg.ConfigVersion < 2)
                    {
                        // v0.2: theme follows the system by default; the stock upper/lower buttons were dropped.
                        if (cfg.Theme == "dark") cfg.Theme = "system";
                        cfg.Actions.RemoveAll(a => a.Type == Actions.ActionTypes.Transform && a.Id is "upper" or "lower");
                        cfg.ConfigVersion = 2;
                        migrated = true;
                    }
                    if (cfg.ConfigVersion < 3)
                    {
                        // v0.3: the translate plugin's panel replaces the stock "open Google Translate" URL action.
                        cfg.Actions.RemoveAll(a => a.Id == "translate" && a.Type == Actions.ActionTypes.Url
                            && (a.Url ?? "").Contains("translate.google.", StringComparison.OrdinalIgnoreCase));
                        cfg.ConfigVersion = 3;
                        migrated = true;
                    }
                    if (cfg.ConfigVersion < 4)
                    {
                        // v0.3: the stock send-to target is a generic "发送到" (Notepad by default), not "记事本".
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
                        // v0.3: QR and sticky-note buttons start switched off (they briefly shipped on).
                        foreach (var a in cfg.Actions.Where(a => a.Ref is "qr:make" or "notes:pin")) a.Enabled = false;
                        cfg.ConfigVersion = 5;
                        migrated = true;
                    }
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

    /// <summary>Apply a mutation, persist, and broadcast.</summary>
    public void Update(Action<AppConfig> mutate)
    {
        AppConfig snapshot;
        lock (_gate)
        {
            mutate(_current);
            SaveLocked();
            snapshot = _current;
        }
        Changed?.Invoke(snapshot);
    }

    /// <summary>Replace the whole config (used by the settings window).</summary>
    public void Replace(AppConfig cfg)
    {
        lock (_gate)
        {
            _current = cfg;
            SaveLocked();
        }
        Changed?.Invoke(cfg);
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_current, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("config save failed", ex);
        }
    }
}
