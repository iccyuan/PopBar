using System.IO;
using System.Text.Json;
using PopBar.Sdk;

namespace PopBar.Plugins;

/// <summary>JSON file per plugin under %AppData%\PopBar\plugins\{id}.json.</summary>
internal sealed class PluginStorage : IPluginStorage
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string PluginsDir { get; } = Path.Combine(Config.ConfigStore.Dir, "plugins");

    private readonly string _path;
    private readonly object _gate = new();

    public PluginStorage(string pluginId)
    {
        _path = Path.Combine(PluginsDir, pluginId + ".json");
    }

    public T Get<T>() where T : class, new()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Json) ?? new T();
            }
            catch (Exception ex) { Log.Warn($"plugin storage {_path} unreadable: {ex.Message}"); }
            return new T();
        }
    }

    public void Set<T>(T value) where T : class
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(PluginsDir);
                File.WriteAllText(_path, JsonSerializer.Serialize(value, Json));
            }
            catch (Exception ex) { Log.Error($"plugin storage {_path} write failed", ex); }
        }
    }
}
