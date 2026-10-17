namespace PopBar.Sdk;

/// <summary>A plugin's tray entry (see <see cref="IPluginHost.AddTrayItem"/>), and where PopBar may offer it.</summary>
public sealed record TrayItem
{
    /// <summary>Unique within the plugin; PopBar prefixes the plugin id ("screenshot:capture").</summary>
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Action Callback { get; init; }
    /// <summary>An icon: see <see cref="IPopBarPlugin.Icon"/>.</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// May be listed in the tray's right-click menu. False keeps it out of the menu and of the menu editor, e.g. an
    /// entry meant only for the left click or for <c>--run</c>.
    /// </summary>
    public bool InMenu { get; init; } = true;
    /// <summary>Listed while the user has not customised the menu (only with <see cref="InMenu"/>); false = opt-in.</summary>
    public bool InMenuByDefault { get; init; } = true;
    /// <summary>May be chosen as the tray icon's left-click action in settings.</summary>
    public bool AsClick { get; init; } = true;
}
