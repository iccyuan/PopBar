using System.Text.Json.Serialization;
using PopBar.Actions;

namespace PopBar.Config;

/// <summary>User configuration, persisted as JSON in %AppData%\PopBar\config.json.</summary>
public sealed class AppConfig
{
    /// <summary>Bumped when defaults change; ConfigStore migrates older files on load.</summary>
    public int ConfigVersion { get; set; } = 2;

    public bool Enabled { get; set; } = true;
    public bool AutoStart { get; set; } = false;

    public bool TriggerOnDrag { get; set; } = true;
    public bool TriggerOnDoubleClick { get; set; } = true;
    public bool TriggerOnShiftClick { get; set; } = true;

    /// <summary>"ibeam" = only when the cursor is an I-beam or on double click, "always", "off".</summary>
    public string ClipboardFallback { get; set; } = "ibeam";

    /// <summary>Delay between mouse-up and reading the selection (lets the app finish selecting).</summary>
    public int ShowDelayMs { get; set; } = 60;
    public int MaxTextLength { get; set; } = 10000;

    /// <summary>"dark" or "light".</summary>
    public string Theme { get; set; } = "system";
    public bool ShowLabels { get; set; } = true;

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

    /// <summary>Model backend shared by AI-powered plugins.</summary>
    public LlmConfig Llm { get; set; } = new();

    public bool IsPluginEnabled(string id) => !DisabledPlugins.Contains(id, StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool ClipboardFallbackOff => string.Equals(ClipboardFallback, "off", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore]
    public bool ClipboardFallbackAlways => string.Equals(ClipboardFallback, "always", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Model backend settings. Protocol "anthropic" uses the official SDK, "openai" any /chat/completions endpoint.</summary>
public sealed class LlmConfig
{
    public string Protocol { get; set; } = "anthropic";
    /// <summary>Optional for anthropic (defaults to api.anthropic.com); required for openai (e.g. http://localhost:4000/v1).</summary>
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-opus-5-5";
}
