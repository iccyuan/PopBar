using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PopBar.Core;

/// <summary>
/// Resolves "system" | "dark" | "light" to an effective theme, pushes the palette into Application resources
/// (every style uses DynamicResource) and re-applies when Windows switches its app theme.
/// </summary>
internal static class ThemeManager
{
    public static string Setting { get; private set; } = "system";
    /// <summary>"dark" or "light" after resolution.</summary>
    public static string Effective { get; private set; } = "light";
    public static bool IsDark => Effective == "dark";

    public static event Action<string>? Changed;

    /// <summary>Opacity (percent) of the tint layer over the acrylic blur on the bar and panels. 100 = solid.</summary>
    public static int BlurOpacity { get; set; } = 88;

    /// <summary>
    /// WPF overlay colour for a blurred surface. When the composition acrylic is active the tint is painted by
    /// DWM (see <see cref="BlurTintAbgr"/>) and the WPF layer stays transparent; otherwise an opaque card colour.
    /// </summary>
    public static Color BlurTint(bool dark, bool blurActive)
    {
        byte a = blurActive ? (byte)Math.Round(Math.Clamp(BlurOpacity, 40, 100) * 2.55) : (byte)255;
        return dark ? Color.FromArgb(a, 0x1C, 0x21, 0x2B) : Color.FromArgb(a, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>
    /// Menus, drop-downs and tooltips sit right on top of busy content and are only up for a moment, so their tint is
    /// thinner than the bar's: at the same alpha the blur under a small popup reads as a solid card.
    /// </summary>
    public static Color PopupTint(bool dark)
    {
        var c = BlurTint(dark, BlurOpacity < 100);
        return c.A == 255 ? c : Color.FromArgb((byte)Math.Round(c.A * PopupTintRatio), c.R, c.G, c.B);
    }

    private const double PopupTintRatio = 0.72;

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Accent-policy tint (0xAABBGGRR): white-ish in light mode, blue-black in dark mode, alpha from <see cref="BlurOpacity"/>.</summary>
    public static uint BlurTintAbgr(bool dark)
    {
        uint a = (uint)Math.Round(Math.Clamp(BlurOpacity, 40, 100) * 2.55);
        return dark ? (a << 24) | 0x00271F1Bu : (a << 24) | 0x00FFFFFFu;
    }

    private static bool _hooked;

    public static string Resolve(string? setting) => (setting ?? "system").ToLowerInvariant() switch
    {
        "dark" => "dark",
        "light" => "light",
        _ => SystemIsDark() ? "dark" : "light",
    };

    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Apply(string setting)
    {
        Setting = setting;
        var effective = Resolve(setting);
        bool changed = effective != Effective;
        Effective = effective;
        ApplyPalette(Application.Current, IsDark);
        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != UserPreferenceCategory.General) return;
                if (Setting != "system") return;
                Application.Current?.Dispatcher.InvokeAsync(() => Apply(Setting));
            };
        }
        if (changed) Changed?.Invoke(Effective);
    }

    /// <summary>Opaque page colour of a theme (what BgBrush is), for covers during a switch.</summary>
    public static Color PageColor(bool dark) => (Color)ColorConverter.ConvertFromString(dark ? "#FF15181F" : "#FFF5F6FA");

    /// <summary>How long a theme switch takes to cross-fade.</summary>
    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(280);

    /// <summary>
    /// A palette colour that can be animated. Each palette brush binds its Color to one of these: the binding keeps
    /// the brush unfreezable, so Application.Resources cannot seal it on insertion (ResourceDictionary freezes any
    /// Freezable it can), and animating the holder cross-fades the brush in place.
    /// </summary>
    private sealed class ThemeColor : System.Windows.Media.Animation.Animatable
    {
        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(ThemeColor));
        public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
        protected override Freezable CreateInstanceCore() => new ThemeColor();
    }

    private static readonly Dictionary<string, ThemeColor> Colors = new();

    /// <summary>
    /// The palette brushes are created once and kept for the app's lifetime; a theme switch animates each one's
    /// colour, so everything bound to them (DynamicResource or a brush instance captured in code) cross-fades
    /// together instead of snapping, and nothing is left holding a brush from the previous theme.
    /// </summary>
    private static void ApplyPalette(Application app, bool dark)
    {
        void Set(string key, string color)
        {
            var target = (Color)ColorConverter.ConvertFromString(color);
            if (Colors.TryGetValue(key, out var tc))
            {
                if (tc.Color == target) return;
                tc.BeginAnimation(ThemeColor.ColorProperty,
                    new System.Windows.Media.Animation.ColorAnimation(target, FadeDuration) { EasingFunction = new System.Windows.Media.Animation.SineEase() },
                    System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
                return;
            }
            tc = new ThemeColor { Color = target };
            Colors[key] = tc;
            var brush = new SolidColorBrush();
            System.Windows.Data.BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new System.Windows.Data.Binding(nameof(ThemeColor.Color)) { Source = tc });
            app.Resources[key] = brush;
        }
        // With Mica behind the window the chrome becomes translucent; without it everything stays opaque.
        bool blur = Acrylic.SystemBackdropSupported;
        if (dark)
        {
            Set("BgBrush", "#FF15181F");
            Set("WindowBgBrush", blur ? "#00000000" : "#FF15181F");
            Set("NavBrush", blur ? "#8C1B1F28" : "#FF1B1F28");
            Set("ContentBgBrush", blur ? "#CC15181F" : "#FF15181F");   // settings content: calmer than bare acrylic
            Set("CardBrush", blur ? "#B81F2430" : "#FF1F2430");        // cards stay translucent so the blur reads through them
            Set("BorderBrush", "#FF2D3442");
            Set("CardBorderBrush", "#FF272D39");
            Set("TextBrush", "#FFF3F4F6");
            Set("MutedBrush", "#FFA6AFBD");
            Set("SubtleTextBrush", "#FF8F99A8");
            Set("AccentBrush", "#FF5B9BFF");
            Set("AccentHoverBrush", "#FF7CB0FF");
            Set("AccentSoftBrush", "#FF243552");
            Set("HoverBrush", "#FF2A3141");
            Set("PressedBrush", "#FF343C4E");
            Set("InputBrush", "#FF161A22");
            Set("InputBorderBrush", "#FF3A4254");
            Set("DangerBrush", "#FFF87171");
            Set("SuccessBrush", "#FF4ADE80");
            Set("SwitchOffBrush", "#FF3A4254");
            Set("PopupBrush", "#FF232936");
            Set("PopupTintBrush", Hex(PopupTint(true)));
            Set("BarTintBrush", Hex(BlurTint(true, BlurOpacity < 100)));   // persistent chrome on glass (screenshot toolbar)
            Set("PopupHoverBrush", "#1FFFFFFF");      // translucent: a solid block would sit oddly on the frosted surface
            Set("PopupSelectedBrush", "#335B9BFF");
            Set("ScrollThumbBrush", "#FF3F485A");
        }
        else
        {
            Set("BgBrush", "#FFF5F6FA");
            Set("WindowBgBrush", blur ? "#00000000" : "#FFF5F6FA");
            Set("NavBrush", blur ? "#8CFFFFFF" : "#FFFFFFFF");
            Set("ContentBgBrush", blur ? "#CCF5F6FA" : "#FFF5F6FA");
            Set("CardBrush", blur ? "#B8FFFFFF" : "#FFFFFFFF");
            Set("BorderBrush", "#FFE5E7EB");
            Set("CardBorderBrush", "#FFEEF0F3");
            Set("TextBrush", "#FF111827");
            Set("MutedBrush", "#FF4B5563");
            Set("SubtleTextBrush", "#FF808791");
            Set("AccentBrush", "#FF3B82F6");
            Set("AccentHoverBrush", "#FF2563EB");
            Set("AccentSoftBrush", "#FFE8EFFF");
            Set("HoverBrush", "#FFF3F4F6");
            Set("PressedBrush", "#FFE5E7EB");
            Set("InputBrush", "#FFFFFFFF");
            Set("InputBorderBrush", "#FFD1D5DB");
            Set("DangerBrush", "#FFDC2626");
            Set("SuccessBrush", "#FF16A34A");
            Set("SwitchOffBrush", "#FFD1D5DB");
            Set("PopupBrush", "#FFFFFFFF");
            Set("PopupTintBrush", Hex(PopupTint(false)));
            Set("BarTintBrush", Hex(BlurTint(false, BlurOpacity < 100)));
            Set("PopupHoverBrush", "#14000000");
            Set("PopupSelectedBrush", "#1F3B82F6");
            Set("ScrollThumbBrush", "#FFC7CCD6");
        }
    }

    // ------------------------------------------------------------------ dark title bar

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Make the non-client title bar follow the theme (Windows 10 20H1+).</summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        }
        catch { }
    }
}
