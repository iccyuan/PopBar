using System.Text.Json.Serialization;
using PopBar.Actions;

namespace PopBar.Config;

/// <summary>User configuration, persisted as JSON in %AppData%\PopBar\config.json.</summary>
public sealed class AppConfig
{
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
    public string Theme { get; set; } = "dark";
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

    [JsonIgnore]
    public bool ClipboardFallbackOff => string.Equals(ClipboardFallback, "off", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore]
    public bool ClipboardFallbackAlways => string.Equals(ClipboardFallback, "always", StringComparison.OrdinalIgnoreCase);
}
