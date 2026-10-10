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
                    _current = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                    if (_current.Actions.Count == 0) _current.Actions = Actions.DefaultActions.Create();
                    IsFirstRun = false;
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
