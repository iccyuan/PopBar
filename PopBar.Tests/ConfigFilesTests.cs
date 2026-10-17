using System.IO;
using PopBar.Config;

namespace PopBar.Tests;

public class ConfigFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"popbar-config-{Guid.NewGuid():N}");

    public ConfigFilesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Read(string file) => File.ReadAllText(Path.Combine(_dir, file));

    [Fact]
    public void Settings_are_kept_in_one_file_per_subject()
    {
        var store = new ConfigStore(_dir);
        store.Load();
        store.Update(c => { c.Theme = "dark"; c.Proxy.Port = 7890; c.LlmServices.Add(new LlmConfig { Protocol = "openai", ApiKey = "sk-secret" }); });

        Assert.Contains("\"theme\"", Read("settings.json"));
        Assert.DoesNotContain("\"actions\"", Read("settings.json"));
        Assert.Contains("\"actions\"", Read("actions.json"));
        Assert.Contains("\"llmServices\"", Read("services.json"));
        Assert.DoesNotContain("sk-secret", Read("services.json")); // still encrypted
        Assert.Contains("7890", Read("proxy.json"));

        var again = new ConfigStore(_dir);
        again.Load();
        Assert.False(again.IsFirstRun);
        Assert.Equal("dark", again.Current.Theme);
        Assert.Equal(7890, again.Current.Proxy.Port);
        Assert.Equal("sk-secret", again.Current.LlmServices.Single().ApiKey);
        Assert.Equal(store.Current.Actions.Count, again.Current.Actions.Count);
    }

    [Fact]
    public void Only_changed_files_are_rewritten()
    {
        var store = new ConfigStore(_dir);
        store.Load();
        var actions = Path.Combine(_dir, "actions.json");
        var before = File.GetLastWriteTimeUtc(actions);
        File.SetLastWriteTimeUtc(actions, before.AddHours(-1));
        store.Update(c => c.Theme = "light");
        Assert.Equal(before.AddHours(-1), File.GetLastWriteTimeUtc(actions));
    }

    [Fact]
    public void An_old_single_config_json_is_split_and_kept_as_backup()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{ "configVersion": {{new AppConfig().ConfigVersion}}, "theme": "dark", "llmDefault": "deepseek", "proxy": { "port": 1080 } }""");
        var store = new ConfigStore(_dir);
        store.Load();

        Assert.Equal("dark", store.Current.Theme);
        Assert.False(File.Exists(Path.Combine(_dir, "config.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "config.json.bak")));
        Assert.Contains("deepseek", Read("services.json"));
        Assert.Contains("1080", Read("proxy.json"));
    }
}
