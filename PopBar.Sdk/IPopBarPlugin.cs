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
    /// <summary>Return false to hide the button for this selection. Must be fast (runs on the capture thread).</summary>
    public Func<SelectionContext, bool>? IsApplicable { get; init; }
    /// <summary>Runs on a worker thread. Use <see cref="IPluginHost"/> for anything UI related.</summary>
    public required Func<SelectionContext, CancellationToken, Task> Execute { get; init; }
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
