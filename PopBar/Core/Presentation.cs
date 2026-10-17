using PopBar.Actions;
using PopBar.Config;
using static PopBar.Native.NativeMethods;

namespace PopBar.Core;

/// <summary>
/// What the engine needs from "the thing that shows the bar". The WPF PopupWindow implements it; the engine
/// never references a window type, so presentation can be swapped or faked in tests.
/// </summary>
internal interface IPopupPresenter
{
    /// <summary>Show the bar for a selection. UI thread.</summary>
    void ShowFor(Selection selection, IReadOnlyList<ActionDefinition> actions, AppConfig cfg);
    /// <summary>The selection changed while the bar is up (click chain): update it in place, or show it if hidden. UI thread.</summary>
    void UpdateFor(Selection selection, IReadOnlyList<ActionDefinition> actions, AppConfig cfg);
    /// <summary>Hide immediately. UI thread.</summary>
    void HideNow();

    /// <summary>User clicked an action button.</summary>
    event Action<ActionDefinition, Selection>? ActionClicked;
    /// <summary>Bar became visible at this physical screen rect.</summary>
    event Action<RECT>? Shown;
    event Action? Hidden;
}

/// <summary>A plugin's actions merged into one bar button (see BarOptions.MergeAs). Key is unique per plugin.</summary>
internal sealed record BarGroup(string Key, string Label, string Icon);

/// <summary>Open floating panels, queried by the engine for click/selection arbitration.</summary>
internal interface IPanelRegistry
{
    /// <summary>While true the engine ignores mouse selections (e.g. a region-capture overlay is up).</summary>
    bool SuppressCapture { get; }
    /// <summary>A click landed outside the bar; close unpinned panels not under the point. UI thread.</summary>
    void OnOutsideClick(int x, int y);
    /// <summary>Is the screen point inside one of our panels? Any thread.</summary>
    bool ContainsPoint(int x, int y);
}
