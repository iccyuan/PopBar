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
    /// <summary>Icon standing for the plugin as a whole (its card in settings, its group of actions). PopBar icon name.</summary>
    string Icon => "puzzle";

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
    /// <summary>Icon name from PopBar's built-in icon set (lucide names such as "languages", "sparkles").</summary>
    public string Icon { get; init; } = "circle";
    /// <summary>One line shown under the action in settings, e.g. "粘贴到 notepad.exe". Optional.</summary>
    public string? Description { get; init; }
    /// <summary>Whether the button is switched on when it is first added to the bar. Niche actions can start off.</summary>
    public bool EnabledByDefault { get; init; } = true;
    /// <summary>
    /// Actions of one plugin that share a group name fold into a single bar button (this label, <see cref="GroupIcon"/>)
    /// that expands in place, whenever two or more of them apply to a selection. Keeps the bar short. Optional.
    /// </summary>
    public string? Group { get; init; }
    public string GroupIcon { get; init; } = "circle";
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

/// <summary>What the user selected and where.</summary>
public sealed record SelectionContext(
    string Text,
    IntPtr SourceWindow,
    string SourceProcess,
    int ScreenX,
    int ScreenY)
{
    public bool HasSourceWindow => SourceWindow != IntPtr.Zero;
}
