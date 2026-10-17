using System.Windows;

namespace PopBar.Sdk;

/// <summary>
/// A PopBar plugin. Implement this in a class library that references PopBar.Sdk, drop the DLL into
/// <c>%AppData%\PopBar\plugins\&lt;name&gt;\</c>, and PopBar loads it at startup. Built-in plugins use the same contract.
/// </summary>
public interface IPopBarPlugin : IDisposable
{
    /// <summary>Stable unique id, lowercase, no spaces (e.g. "translate"). Action ids are prefixed with it.</summary>
    string Id { get; }
    string Name { get; }
    string Description { get; }
    Version Version { get; }
    /// <summary>
    /// Icon standing for the plugin as a whole (its card in settings, its group of actions). Like every icon PopBar
    /// takes: a built-in name (<see cref="IHostVisuals.IconNames"/>, Lucide names such as "languages"), a name
    /// registered with <see cref="IPluginHost.RegisterIcon"/>, or SVG markup inline. Drawn in one colour that follows the theme.
    /// </summary>
    string Icon => "puzzle";

    /// <summary>
    /// How this plugin's actions sit on the bar: merged into one button or not, shown by default or not. The plugin
    /// decides; PopBar reads it every time the bar is built, so it may follow the plugin's own settings.
    /// </summary>
    BarOptions Bar => BarOptions.Default;

    /// <summary>Called once after loading. Keep the host; register hotkeys / tray items here.</summary>
    void Initialize(IPluginHost host);

    /// <summary>
    /// Actions this plugin contributes to the bar. May change over time (e.g. user-defined targets);
    /// the host re-reads it when the config is synchronized.
    /// </summary>
    IReadOnlyList<PluginAction> GetActions();

    /// <summary>Optional settings UI shown inside PopBar's settings window. Return null for none.</summary>
    FrameworkElement? CreateSettingsView() => null;
}

/// <summary>One button the plugin offers. Full id is "{pluginId}:{Id}".</summary>
public sealed class PluginAction
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>An icon: see <see cref="IPopBarPlugin.Icon"/> (built-in name, registered name or inline SVG).</summary>
    public string Icon { get; init; } = "circle";
    /// <summary>One line shown under the action in settings, e.g. "粘贴到 notepad.exe". Optional.</summary>
    public string? Description { get; init; }
    /// <summary>
    /// Whether this button is switched on when it is first added to the bar. Niche actions can start off. Only counts
    /// while <see cref="BarOptions.ShowByDefault"/> of the plugin is on (that one switches the whole plugin's off).
    /// </summary>
    public bool EnabledByDefault { get; init; } = true;
    /// <summary>
    /// Where the action lands the first time it is added to the bar: right after the action with this id, either a
    /// stock action ("search", "copy") or a full plugin action id ("translate:panel"). When unset, or that action is
    /// not on the bar, it joins its plugin's other actions, else goes last. Only the first placement: after that the
    /// user's order is kept.
    /// </summary>
    public string? PlaceAfter { get; init; }
    /// <summary>Return false to hide the button for this selection. Must be fast (runs on the capture thread).</summary>
    public Func<SelectionContext, bool>? IsApplicable { get; init; }
    /// <summary>Runs on a worker thread. Use <see cref="IPluginHost"/> for anything UI related.</summary>
    public required Func<SelectionContext, CancellationToken, Task> Execute { get; init; }
    /// <summary>
    /// Opens this action's own editor when the user clicks "编辑" on it in the actions list (UI thread). Return true
    /// after saving a change and call <see cref="IPluginHost.RefreshActions"/> so the list picks up the new label.
    /// Without it, "编辑" takes the user to the plugin's settings page instead.
    /// </summary>
    public Func<bool>? Configure { get; init; }
}

/// <summary>How a plugin's actions appear on the bar (<see cref="IPopBarPlugin.Bar"/>).</summary>
public sealed class BarOptions
{
    /// <summary>One button per action, all shown by default.</summary>
    public static BarOptions Default { get; } = new();

    /// <summary>
    /// Merge the plugin's actions into one bar button with this label, which opens a list of them. Only when two or
    /// more of them apply to the selection: an action that applies alone keeps its own button, so actions that come
    /// and go with the selection still show as themselves. Null = one button per action.
    /// </summary>
    public string? MergeAs { get; init; }

    /// <summary>Icon of the merged button; null = the plugin's <see cref="IPopBarPlugin.Icon"/>.</summary>
    public string? MergeIcon { get; init; }

    /// <summary>
    /// Whether the plugin's actions are switched on when they first join the bar: right after the plugin is installed
    /// (and enabled), or when it adds a new action later. False adds them switched off, for the user to turn on on the
    /// "动作" page. <see cref="PluginAction.EnabledByDefault"/> can still keep single actions off while this is on.
    /// </summary>
    public bool ShowByDefault { get; init; } = true;
}

/// <summary>What the user selected and where (<see cref="ScreenX"/> / <see cref="ScreenY"/>: where the mouse was released).</summary>
public sealed record SelectionContext(
    string Text,
    IntPtr SourceWindow,
    string SourceProcess,
    int ScreenX,
    int ScreenY)
{
    public bool HasSourceWindow => SourceWindow != IntPtr.Zero;

    /// <summary>
    /// The selected text on screen (physical pixels): as the app reports it, else estimated from the lines the mouse
    /// touched. Null when unknown, e.g. text that did not come from a selection. Keep your own windows off it; panels
    /// from <see cref="IPluginHost.ShowPanel"/> avoid it by themselves. Since SDK 1.2.
    /// </summary>
    public ScreenRect? Area { get; init; }
}

/// <summary>A rectangle on screen in physical pixels (right / bottom exclusive).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
