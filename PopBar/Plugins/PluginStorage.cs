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

    /// <summary>
    /// Parent of the plugins' data folders (<see cref="IPluginHost.DataDir"/>). Kept apart from the install folders,
    /// which are named after the file or repository a plugin came from and so often match its id: an update replaces
    /// its install folder wholesale and would take the data with it. The leading dot keeps discovery out of it.
    /// </summary>
    public static string DataRoot => Path.Combine(PluginsDir, ".data");

    /// <summary>
    /// A plugin's data folder; data left in the old place (plugins\{id}\, before <see cref="DataRoot"/>) moves over the
    /// first time. Only a folder without DLLs is data: one with DLLs is an install folder that merely shares the name.
    /// </summary>
    public static string DataDirOf(string pluginId) => DataDirOf(PluginsDir, pluginId);

    /// <summary><see cref="DataDirOf(string)"/> under another plugins folder (tests).</summary>
    internal static string DataDirOf(string pluginsDir, string pluginId)
    {
        var root = Path.Combine(pluginsDir, ".data");
        var dir = Path.Combine(root, pluginId);
        var old = Path.Combine(pluginsDir, pluginId);
        try
        {
            if (!Directory.Exists(dir) && Directory.Exists(old) && Directory.GetFiles(old, "*.dll", SearchOption.AllDirectories).Length == 0)
            {
                Directory.CreateDirectory(root);
                Directory.Move(old, dir);
                Log.Info($"plugin data of {pluginId} moved to {dir}");
            }
        }
        catch (Exception ex) { Log.Warn($"plugin data of {pluginId} not moved: {ex.Message}"); }
        return dir;
    }

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
