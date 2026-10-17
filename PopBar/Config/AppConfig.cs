using System.Text.Json.Serialization;
using PopBar.Actions;

namespace PopBar.Config;

/// <summary>User configuration, persisted as JSON in %AppData%\PopBar\config.json.</summary>
public sealed class AppConfig
{
    /// <summary>Bumped when defaults change; ConfigStore migrates older files on load.</summary>
    public int ConfigVersion { get; set; } = 7;

    public bool Enabled { get; set; } = true;
    public bool AutoStart { get; set; } = false;
    /// <summary>
    /// What a left click on the tray icon does: "settings" (default), "menu" (the right-click menu), or the id of a
    /// plugin tray item such as "screenshot:capture" / "ocr:capture". Double click always opens settings.
    /// </summary>
    public string TrayClick { get; set; } = "settings";

    /// <summary>Most plugin entries the tray menu lists; enable / pause, settings and exit are always there besides.</summary>
    public const int MaxTrayMenuItems = 8;
    /// <summary>
    /// Plugin tray entries shown in the right-click menu, in order (ids as for <see cref="TrayClick"/>). Null until the user
    /// picks: then the first <see cref="MaxTrayMenuItems"/> in registration order. Ids of missing plugins are skipped.
    /// </summary>
    public List<string>? TrayMenuItems { get; set; }

    /// <summary>"key" (default) = bar appears only after pressing <see cref="ShowKey"/> (held during the selection or tapped within a few seconds); "auto" = right after a selection.</summary>
    public string ShowMode { get; set; } = "key";
    /// <summary>ctrl | alt | win — the key that reveals the bar in "key" mode.</summary>
    public string ShowKey { get; set; } = "ctrl";

    public bool TriggerOnDrag { get; set; } = true;
    public bool TriggerOnDoubleClick { get; set; } = true;
    public bool TriggerOnShiftClick { get; set; } = true;

    /// <summary>"ibeam" = only when the cursor is an I-beam or on double click, "always", "off".</summary>
    public string ClipboardFallback { get; set; } = "ibeam";

    /// <summary>Delay between mouse-up and reading the selection (lets the app finish selecting).</summary>
    public int ShowDelayMs { get; set; } = 60;
    public int MaxTextLength { get; set; } = 10000;

    /// <summary>"system" (default), "dark" or "light".</summary>
    public string Theme { get; set; } = "system";
    public bool ShowLabels { get; set; } = true;
    /// <summary>Bar buttons shown at once; the rest fold behind a "更多" button. 0 = no limit.</summary>
    public int MaxBarItems { get; set; } = 7;
    /// <summary>Opacity (40-100 %) of the tint over the blurred backdrop of the bar and panels.</summary>
    public int BlurOpacity { get; set; } = 88;

    /// <summary>Process names (e.g. "game.exe") where PopBar never shows.</summary>
    public List<string> ExcludedApps { get; set; } = new();

    /// <summary>Process names where the Ctrl+C fallback is never used (terminals: Ctrl+C would interrupt).</summary>
    public List<string> NoClipboardApps { get; set; } = new()
    {
        "WindowsTerminal.exe", "conhost.exe", "OpenConsole.exe", "mintty.exe", "putty.exe",
        "mstsc.exe", "vmware-vmx.exe", "VirtualBoxVM.exe", "alacritty.exe", "wezterm-gui.exe",
    };

    public List<ActionDefinition> Actions { get; set; } = DefaultActions.Create();

    /// <summary>Plugin ids the user switched off.</summary>
    public List<string> DisabledPlugins { get; set; } = new();

    /// <summary>Model services shared by AI-powered plugins, one per provider (<see cref="LlmConfig.Protocol"/> is the key).</summary>
    public List<LlmConfig> LlmServices { get; set; } = new();
    /// <summary>Provider id of the service plugins use unless they pick another; empty = the first of <see cref="LlmServices"/>.</summary>
    public string LlmDefault { get; set; } = "";
    /// <summary>Pre-v7 single service; moved into <see cref="LlmServices"/> on load and left null.</summary>
    public LlmConfig? Llm { get; set; }

    /// <summary>The service for a provider id (null / empty = the default one), or null when it is not set up.</summary>
    public LlmConfig? LlmServiceFor(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) providerId = LlmDefaultId;
        return LlmServices.FirstOrDefault(s => string.Equals(s.Protocol, providerId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Provider id of the default service: <see cref="LlmDefault"/> when it names a set-up service, else the first one, else anthropic.</summary>
    [JsonIgnore]
    public string LlmDefaultId =>
        LlmServices.Any(s => string.Equals(s.Protocol, LlmDefault, StringComparison.OrdinalIgnoreCase)) ? LlmDefault
        : LlmServices.FirstOrDefault()?.Protocol ?? Core.Llm.LlmProviderIds.Anthropic;

    public bool IsPluginEnabled(string id) => !DisabledPlugins.Contains(id, StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool ClipboardFallbackOff => string.Equals(ClipboardFallback, "off", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore]
    public bool ClipboardFallbackAlways => string.Equals(ClipboardFallback, "always", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One model service: a provider ("anthropic" = the official SDK, "openai" = any /chat/completions endpoint, ...) and its connection.</summary>
public sealed class LlmConfig
{
    /// <summary>Provider id (<see cref="Sdk.ILlmProvider.Id"/>); unique within <see cref="AppConfig.LlmServices"/>.</summary>
    public string Protocol { get; set; } = Core.Llm.LlmProviderIds.Anthropic;
    /// <summary>Optional for anthropic (defaults to api.anthropic.com); required for openai (e.g. http://localhost:4000/v1).</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>The key in plain text, in memory only; the file holds <see cref="ApiKeyStored"/>.</summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// <see cref="ApiKey"/> as written to config.json: encrypted for the current Windows user (DPAPI), so another account
    /// or a copied file cannot read it. A plain value from an older file is still read, and encrypted on the next save.
    /// DPAPI output differs on every call, so the cipher is kept while the key is unchanged: serializing the same config
    /// twice must give the same text (the settings window compares snapshots to tell whether anything was edited).
    /// </summary>
    [JsonPropertyName("apiKey")]
    public string ApiKeyStored
    {
        get
        {
            if (_cipherOf != ApiKey) { _cipher = SecretBox.Protect(ApiKey); _cipherOf = ApiKey; }
            return _cipher;
        }
        set
        {
            ApiKey = SecretBox.Unprotect(value ?? "");
            // Keep an already encrypted value as is; a legacy plain one is encrypted the next time it is written.
            if (SecretBox.IsProtected(value)) { _cipher = value!; _cipherOf = ApiKey; } else _cipherOf = null;
        }
    }
    private string _cipher = "";
    private string? _cipherOf = "";

    /// <summary>A key is on disk unencrypted (a file from before keys were encrypted).</summary>
    [JsonIgnore]
    public bool ApiKeyNeedsProtecting => ApiKey.Length > 0 && _cipherOf == null;
    /// <summary>Default model; empty = the provider's suggestion.</summary>
    public string Model { get; set; } = "";
    /// <summary>"深度思考": think before answering, for requests that leave <see cref="Sdk.LlmRequest.Thinking"/> unset.</summary>
    public bool Thinking { get; set; }
    /// <summary>Thinking depth (<see cref="Sdk.LlmEffort"/>) used while <see cref="Thinking"/> is on.</summary>
    public string Effort { get; set; } = Sdk.LlmEffort.Medium;
    /// <summary>Model ids the service returned the last time its list was fetched; shown in the model pickers until the next fetch.</summary>
    public List<string> Models { get; set; } = new();
}

/// <summary>DPAPI (current user) wrapping for secrets kept in config files: "dpapi:" + base64.</summary>
internal static class SecretBox
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("PopBar.LlmConfig.ApiKey");

    public static bool IsProtected(string? value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        try
        {
            var bytes = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(plain), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            // Never lose the key: without DPAPI (should not happen on Windows) it stays readable rather than vanishing.
            Log.Warn("api key encryption failed, stored as is: " + ex.Message);
            return plain;
        }
    }

    public static string Unprotect(string stored)
    {
        if (!IsProtected(stored)) return stored;
        try
        {
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            // Another user's or another machine's file: the key has to be entered again.
            Log.Warn("api key could not be decrypted (config from another account?): " + ex.Message);
            return "";
        }
    }
}
