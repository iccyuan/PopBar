using System.Windows;
using System.Windows.Media;

namespace PopBar.Sdk;

/// <summary>
/// Ready-made controls and lookups that plugins can use in their settings views so they look and behave
/// exactly like PopBar's own pages (key capture, hotkey capture, running-app picker, icons, file browse).
/// All members must be called on the UI thread.
/// </summary>
public interface IHostUi
{
    /// <summary>A button that records a single key (e.g. "ctrl", "alt", "F8") when clicked, then calls <paramref name="onChanged"/>.</summary>
    FrameworkElement KeyBox(string? current, Action<string> onChanged);

    /// <summary>A button that records a key chord ("Ctrl+Alt+O") suitable for <see cref="IPluginHost.RegisterHotkey"/>.</summary>
    FrameworkElement HotkeyBox(string? current, Action<string> onChanged);

    /// <summary>Drop-down listing processes that currently own a visible window; calls back with the exe name.</summary>
    FrameworkElement AppPicker(Action<string> onPicked, double width = 300);

    /// <summary>Button opening a file dialog; calls back with the chosen path.</summary>
    FrameworkElement FileBrowse(string label, string filter, Action<string> onPicked);

    /// <summary>Crisp line icon from PopBar's icon set (same control the bar uses).</summary>
    FrameworkElement Icon(string name, double size = 16, Brush? brush = null);

    /// <summary>Drop-down of all available icon names with previews; calls back with the chosen name.</summary>
    FrameworkElement IconPicker(string? current, Action<string> onChanged, double width = 200);

    /// <summary>Names accepted by <see cref="Icon"/> and by <c>PluginAction.Icon</c>.</summary>
    IReadOnlyList<string> IconNames { get; }

    /// <summary>Processes that currently own a visible top-level window.</summary>
    IReadOnlyList<RunningApp> RunningApps();

    /// <summary>
    /// Modal editor dialog styled like PopBar's own (title, content, 保存 / 取消). Returns true when the user saved.
    /// Build the content with <see cref="Ui.Form"/> helpers; mutate your model inside the callbacks and persist on true.
    /// </summary>
    bool EditDialog(string title, FrameworkElement content, double width = 540);
}

public sealed record RunningApp(string Exe, string Title);
