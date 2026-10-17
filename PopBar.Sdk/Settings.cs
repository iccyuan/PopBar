using System.Text.Json;

namespace PopBar.Sdk;

/// <summary>
/// A plugin's part of the settings window. Settings views edit a draft (see <see cref="SettingsDraft"/>) and call
/// <see cref="Changed"/>; nothing takes effect until the user presses Save, which runs the <see cref="OnApply"/>
/// callback. Cancel simply drops the draft (the view is rebuilt from the live settings).
/// </summary>
public interface ISettingsSession
{
    /// <summary>The draft changed: the window shows "unsaved changes".</summary>
    void Changed();
    /// <summary>What Save does for this plugin: copy the draft into the live settings, persist, re-register hotkeys... UI thread. Latest registration wins.</summary>
    void OnApply(Action apply);
    /// <summary>
    /// <see cref="OnApply(Action)"/> for a <paramref name="draft"/> object: PopBar remembers how it started (as JSON) and
    /// compares on every <see cref="Changed"/>, so setting a value back clears "unsaved changes" again. Prefer this one;
    /// pass the draft right after cloning it.
    /// </summary>
    void OnApply(object draft, Action apply);
}

public static class SettingsDraft
{
    /// <summary>Deep copy through JSON, so a settings view can edit freely without touching the live object.</summary>
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
