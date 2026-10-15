using System.Text.Json.Serialization;
using PopBar.Actions;

namespace PopBar.Config;

/// <summary>User configuration, persisted as JSON in %AppData%\PopBar\config.json.</summary>
public sealed class AppConfig
{
    /// <summary>Bumped when defaults change; ConfigStore migrates older files on load.</summary>
    public int ConfigVersion { get; set; } = 6;

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
    public string Protocol { get; set; } = Core.Llm.LlmProviderIds.Anthropic;
    /// <summary>Optional for anthropic (defaults to api.anthropic.com); required for openai (e.g. http://localhost:4000/v1).</summary>
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>"深度思考": think before answering, for requests that leave <see cref="Sdk.LlmRequest.Thinking"/> unset.</summary>
    public bool Thinking { get; set; }
    /// <summary>Thinking depth (<see cref="Sdk.LlmEffort"/>) used while <see cref="Thinking"/> is on.</summary>
    public string Effort { get; set; } = Sdk.LlmEffort.Medium;
}
