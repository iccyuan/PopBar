using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PopBar.UI;

/// <summary>
/// Click, then press a key: records either a single key ("ctrl", "F8", "CapsLock") or a chord ("Ctrl+Alt+O").
/// Used by the settings window and exposed to plugins through <see cref="Sdk.IHostUi"/>.
/// </summary>
public sealed class KeyCaptureBox : Button
{
    public enum CaptureMode { Single, Chord }

    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(nameof(Gesture), typeof(string), typeof(KeyCaptureBox),
        new PropertyMetadata("", (d, _) => ((KeyCaptureBox)d).Refresh()));
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(CaptureMode), typeof(KeyCaptureBox),
        new PropertyMetadata(CaptureMode.Single));

    /// <summary>Single mode: "ctrl" | "alt" | "shift" | "win" | WPF Key name. Chord mode: "Ctrl+Alt+O".</summary>
    public string Gesture { get => (string)GetValue(GestureProperty); set => SetValue(GestureProperty, value); }
    public CaptureMode Mode { get => (CaptureMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    /// <summary>Raised after the user recorded a new gesture.</summary>
    public event Action<string>? GestureChanged;

    private bool _capturing;

    public KeyCaptureBox()
    {
        // Implicit styles only match the exact type; opt into the app's Button look explicitly.
        SetResourceReference(StyleProperty, typeof(Button));
        MinWidth = 90;
        ToolTip = "点击后按下想用的键，Esc 取消";
        Click += (_, _) => Begin();
        LostKeyboardFocus += (_, _) => { if (_capturing) { _capturing = false; Refresh(); } };
        Refresh();
    }

    private void Begin()
    {
        _capturing = true;
        Content = "按下按键…";
        Focus();
    }

    private void Refresh() => Content = _capturing ? "按下按键…" : DisplayName(Gesture);

    public static string DisplayName(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return "未设置";
        return gesture.ToLowerInvariant() switch
        {
            "ctrl" => "Ctrl", "alt" => "Alt", "shift" => "Shift", "win" => "Win",
            _ => gesture,
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey, // Chinese IME swallows letter keys otherwise
            _ => e.Key,
        };
        if (key == Key.Escape) { _capturing = false; Refresh(); return; }

        bool isModifier = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
        string result;
        if (Mode == CaptureMode.Single)
        {
            result = key switch
            {
                Key.LeftCtrl or Key.RightCtrl => "ctrl",
                Key.LeftAlt or Key.RightAlt => "alt",
                Key.LeftShift or Key.RightShift => "shift",
                Key.LWin or Key.RWin => "win",
                _ => key.ToString(),
            };
        }
        else
        {
            if (isModifier) return; // wait for the non-modifier key that completes the chord
            var parts = new List<string>();
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(KeyName(key));
            result = string.Join("+", parts);
        }
        _capturing = false;
        Gesture = result;
        Refresh();
        GestureChanged?.Invoke(result);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.A and <= Key.Z => key.ToString(),
        _ => key.ToString(),
    };
}
