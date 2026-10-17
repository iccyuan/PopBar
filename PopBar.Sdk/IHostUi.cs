using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PopBar.Sdk;

/// <summary>
/// Everything PopBar lends plugins for building UI that looks and behaves like its own, grouped by what it is for:
/// <see cref="IHostDialogs"/>, <see cref="IHostInputs"/>, <see cref="IHostVisuals"/> and <see cref="ILlmUi"/>.
/// Take the narrowest of those your code needs; <see cref="IPluginHost.Ui"/> hands out all of them at once.
/// All members must be called on the UI thread.
/// </summary>
public interface IHostUi : IHostDialogs, IHostInputs, IHostVisuals, ILlmUi
{
}

/// <summary>Modal dialogs styled like PopBar's own. They return null / false when the user cancels.</summary>
public interface IHostDialogs
{
    /// <summary>Open-file dialog. <paramref name="filter"/> uses the Win32 form "PNG 图片 (*.png)|*.png".</summary>
    string? PickFile(string filter, string? title = null, string? initialDir = null);

    /// <summary>Folder dialog.</summary>
    string? PickFolder(string? initialDir = null, string? title = null);

    /// <summary>
    /// Save-file dialog. The chosen path carries the extension of the selected filter entry. Pass the window the
    /// dialog belongs to as <paramref name="owner"/> when calling from your own window (e.g. an overlay), else null.
    /// </summary>
    string? SaveFile(string filter, string defaultName, string? initialDir = null, Window? owner = null);

    /// <summary>Yes / no question styled like PopBar's own prompts. True when confirmed.</summary>
    bool Confirm(string message, string? title = null);

    /// <summary>Simple notice.</summary>
    void Alert(string message, string? title = null);

    /// <summary>
    /// Modal editor dialog (title, content, 保存 / 取消). Returns true when the user saved.
    /// Build the content with <see cref="Ui.Form"/> helpers; mutate your model inside the callbacks and persist on true.
    /// </summary>
    bool EditDialog(string title, FrameworkElement content, double width = 540);
}

/// <summary>Input controls for settings views: keys, programs, windows, files.</summary>
public interface IHostInputs
{
    /// <summary>A button that records a single key (e.g. "ctrl", "alt", "F8") when clicked, then calls <paramref name="onChanged"/>.</summary>
    FrameworkElement KeyBox(string? current, Action<string> onChanged);

    /// <summary>A button that records a key chord ("Ctrl+Alt+O") suitable for <see cref="IPluginHost.RegisterHotkey"/>.</summary>
    FrameworkElement HotkeyBox(string? current, Action<string> onChanged);

    /// <summary>Drop-down listing processes that currently own a visible window; calls back with the exe name.</summary>
    FrameworkElement AppPicker(Action<string> onPicked, double width = 300);

    /// <summary>
    /// Button that dims the screen and lets the user click on any window; calls back with that window's program and
    /// class (Esc cancels). More precise than <see cref="AppPicker"/> when programs are hard to tell apart by name.
    /// </summary>
    FrameworkElement WindowPicker(Action<PickedWindow> onPicked, string label = "拾取窗口…");

    /// <summary>Button opening a file dialog; calls back with the chosen path.</summary>
    FrameworkElement FileBrowse(string label, string filter, Action<string> onPicked);

    /// <summary>Button opening a folder dialog; <paramref name="initial"/> supplies the folder to start in.</summary>
    FrameworkElement FolderBrowse(string label, Func<string?> initial, Action<string> onPicked);

    /// <summary>Programs the user would call running: one per exe with an application window on screen or hidden in the tray. Shell hosts and helper windows are filtered out; the label is the program's own description.</summary>
    IReadOnlyList<RunningApp> RunningApps();
}

/// <summary>PopBar's icon set and visual helpers.</summary>
public interface IHostVisuals
{
    /// <summary>Crisp line icon from PopBar's icon set (same control the bar uses).</summary>
    FrameworkElement Icon(string name, double size = 16, Brush? brush = null);

    /// <summary>Drop-down of all available icon names with previews; calls back with the chosen name.</summary>
    FrameworkElement IconPicker(string? current, Action<string> onChanged, double width = 200);

    /// <summary>Names accepted by <see cref="Icon"/>, by <c>PluginAction.Icon</c> and by <c>IPopBarPlugin.Icon</c>.</summary>
    IReadOnlyList<string> IconNames { get; }

    /// <summary>
    /// Gaussian-blurred copy of <paramref name="source"/> (at reduced resolution, same framing) for frosted chrome over
    /// an image you already show, such as a toolbar on a screenshot overlay: paint it with an ImageBrush whose Viewbox
    /// is the chrome's rect relative to the image, under the "PopupTintBrush" resource. Null if blurring failed.
    /// </summary>
    BitmapSource? BlurImage(BitmapSource source);
}

/// <summary>Pickers for the model services configured on PopBar's "服务" page (see <see cref="ILlmService"/>).</summary>
public interface ILlmUi
{
    /// <summary>
    /// Editable drop-down for an <see cref="LlmRequest.Model"/> override at the default service: type an id, or open it
    /// to pick from the models the service lists. Untouched it shows the service's own model; cleared it reports "".
    /// </summary>
    FrameworkElement ModelPicker(string? current, Action<string> onChanged, double width = 240) => ModelPicker(null, current, onChanged, width);

    /// <summary>
    /// <see cref="ModelPicker(string?, Action{string}, double)"/> for the service of <paramref name="providerId"/>
    /// (an <see cref="LlmRequest.Provider"/> value; null / empty = the default service). Rebuild it when the provider changes.
    /// </summary>
    FrameworkElement ModelPicker(string? providerId, string? current, Action<string> onChanged, double width = 240);

    /// <summary>
    /// Drop-down for an <see cref="LlmRequest.Provider"/> override: the services configured on the "服务" page, with the
    /// service-page default selected while <paramref name="current"/> is empty.
    /// </summary>
    FrameworkElement ProviderPicker(string? current, Action<string> onChanged, double width = 240);
}

public sealed record RunningApp(string Exe, string Title);

/// <summary>A window chosen with <see cref="IHostInputs.WindowPicker"/>: its exe name, full path, title and Win32 window class.</summary>
public sealed record PickedWindow(string Exe, string Path, string Title, string WindowClass);
