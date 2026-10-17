using PopBar.Actions;
using PopBar.Config;

namespace PopBar.Tests;

public class ConfigMigrationTests
{
    [Fact]
    public void Unversioned_file_runs_every_migration()
    {
        const string json = """
        {
          "theme": "dark",
          "showMode": "auto",
          "actions": [
            { "id": "copy", "label": "复制", "type": "copy" },
            { "id": "upper", "label": "大写", "type": "transform" },
            { "id": "translate", "label": "翻译", "type": "url", "url": "https://translate.google.com/?text={text}" },
            { "id": "s", "label": "记事本", "icon": "type", "type": "plugin", "ref": "sendto:notepad" },
            { "id": "q", "label": "二维码", "type": "plugin", "ref": "qr:make", "enabled": true }
          ],
          "llm": { "protocol": "openai", "baseUrl": "https://api.example.com", "model": "m" }
        }
        """;
        var cfg = ConfigStore.Parse(json, out bool migrated);

        Assert.True(migrated);
        Assert.Equal(new AppConfig().ConfigVersion, cfg.ConfigVersion);
        Assert.Equal("system", cfg.Theme);
        Assert.Equal("key", cfg.ShowMode);
        Assert.DoesNotContain(cfg.Actions, a => a.Id is "upper" or "translate");
        var send = Assert.Single(cfg.Actions, a => a.Ref == "sendto:notepad");
        Assert.Equal("发送到", send.Label);
        Assert.Equal("send", send.Icon);
        Assert.False(Assert.Single(cfg.Actions, a => a.Ref == "qr:make").Enabled);
        Assert.Null(cfg.Llm);
        var svc = Assert.Single(cfg.LlmServices);
        Assert.Equal("openai", svc.Protocol);
        Assert.Equal("openai", cfg.LlmDefault);
    }

    [Fact]
    public void Current_file_is_left_alone()
    {
        var cfg = ConfigStore.Parse($$"""{ "configVersion": {{new AppConfig().ConfigVersion}}, "theme": "dark", "showMode": "auto" }""", out bool migrated);
        Assert.False(migrated);
        Assert.Equal("dark", cfg.Theme);
        Assert.Equal("auto", cfg.ShowMode);
        Assert.NotEmpty(cfg.Actions); // an empty action list gets the defaults
    }

    [Fact]
    public void Plain_api_key_asks_for_a_resave()
    {
        var cfg = ConfigStore.Parse($$"""{ "configVersion": {{new AppConfig().ConfigVersion}}, "llmServices": [ { "protocol": "openai", "apiKey": "sk-plain" } ] }""", out bool migrated);
        Assert.True(migrated);
        Assert.Equal("sk-plain", cfg.LlmServices[0].ApiKey);
    }

    [Fact]
    public void V8_drops_builtin_no_clipboard_apps_and_keeps_the_users()
    {
        var cfg = ConfigStore.Parse("""{ "configVersion": 7, "noClipboardApps": [ "WindowsTerminal.exe", "mstsc.exe", "myapp.exe" ] }""", out bool migrated);
        Assert.True(migrated);
        Assert.Equal(new[] { "myapp.exe" }, cfg.NoClipboardApps);
        Assert.Empty(new AppConfig().NoClipboardApps);
    }
}

public class SecretBoxTests
{
    [Fact]
    public void Round_trips_and_marks_the_ciphertext()
    {
        var stored = SecretBox.Protect("sk-secret");
        Assert.True(SecretBox.IsProtected(stored));
        Assert.DoesNotContain("sk-secret", stored);
        Assert.Equal("sk-secret", SecretBox.Unprotect(stored));
    }

    [Fact]
    public void Saved_key_is_encrypted_and_reloads()
    {
        var cfg = new AppConfig();
        cfg.LlmServices.Add(new LlmConfig { Protocol = "openai", ApiKey = "sk-secret" });
        var copy = ConfigStore.Clone(cfg); // the same JSON form the file uses
        Assert.Equal("sk-secret", copy.LlmServices[0].ApiKey);
        Assert.False(copy.LlmServices[0].ApiKeyNeedsProtecting);
        Assert.True(SecretBox.IsProtected(copy.LlmServices[0].ApiKeyStored));
    }

    [Fact]
    public void Unchanged_key_serializes_identically()
    {
        // The settings window compares JSON snapshots to show "unsaved changes": re-encrypting must not alter them.
        var c = new LlmConfig { ApiKey = "sk-secret" };
        Assert.Equal(c.ApiKeyStored, c.ApiKeyStored);
    }

    [Fact]
    public void Garbage_ciphertext_reads_as_empty() =>
        Assert.Equal("", SecretBox.Unprotect("dpapi:not-base64!"));
}

public class MergeEditsTests
{
    [Fact]
    public void Only_fields_edited_in_the_window_overwrite_the_store()
    {
        var loaded = new AppConfig();                  // what the window opened with
        var edited = ConfigStore.Clone(loaded);
        edited.TriggerOnShiftClick = !loaded.TriggerOnShiftClick; // the one change made in the window

        var target = ConfigStore.Clone(loaded);        // meanwhile something else wrote the store
        target.TrayClick = "menu";
        target.DisabledPlugins.Add("ai");

        UI.SettingsWindow.MergeEdits(target, edited, loaded);

        Assert.Equal(edited.TriggerOnShiftClick, target.TriggerOnShiftClick);
        Assert.Equal("menu", target.TrayClick);
        Assert.Contains("ai", target.DisabledPlugins);
    }

    [Fact]
    public void Edited_lists_replace_wholesale()
    {
        var loaded = new AppConfig();
        var edited = ConfigStore.Clone(loaded);
        edited.Actions.RemoveAt(0);
        var target = ConfigStore.Clone(loaded);

        UI.SettingsWindow.MergeEdits(target, edited, loaded);

        Assert.Equal(edited.Actions.Select(a => a.Id), target.Actions.Select(a => a.Id));
    }
}
