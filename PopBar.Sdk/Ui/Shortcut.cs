using System.Windows.Input;

namespace PopBar.Sdk.Ui;

/// <summary>
/// In-window keyboard shortcuts stored as user-editable gestures ("Ctrl+S", "Space", "C") — the same format that
/// <see cref="IHostInputs.HotkeyBox"/> records. Use it so every shortcut a plugin window handles can be customised.
/// </summary>
public static class Shortcut
{
    /// <summary>True when <paramref name="e"/> is exactly <paramref name="gesture"/> (same modifiers, same key). Empty gesture never matches.</summary>
    public static bool Matches(string? gesture, KeyEventArgs e)
    {
        if (!TryParse(gesture, out var mods, out var key)) return false;
        var actual = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        return actual == key && Keyboard.Modifiers == mods;
    }

    /// <summary>True when <paramref name="e"/> releases the key part of <paramref name="gesture"/> (for hold-to-show shortcuts).</summary>
    public static bool IsKeyOf(string? gesture, KeyEventArgs e)
    {
        if (!TryParse(gesture, out _, out var key)) return false;
        var actual = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        return actual == key;
    }

    /// <summary>Human-readable form for tooltips ("Ctrl+S"); empty when unset.</summary>
    public static string Display(string? gesture) => string.IsNullOrWhiteSpace(gesture) ? "" : gesture.Replace("Return", "Enter");

    /// <summary>"Label  Ctrl+S", or just the label when the shortcut is unset.</summary>
    public static string Tip(string label, string? gesture) => string.IsNullOrWhiteSpace(gesture) ? label : $"{label}  {Display(gesture)}";

    public static bool TryParse(string? gesture, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None; key = Key.None;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default:
                    try { key = (Key)new KeyConverter().ConvertFromInvariantString(raw.Length == 1 ? raw.ToUpperInvariant() : raw)!; }
                    catch { return false; }
                    break;
            }
        }
        return key != Key.None;
    }
}
