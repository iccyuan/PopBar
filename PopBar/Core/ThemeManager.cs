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

    private static void ApplyPalette(Application app, bool dark)
    {
        void Set(string key, string color)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            b.Freeze();
            app.Resources[key] = b;
        }
        // With Mica behind the window the chrome becomes translucent; without it everything stays opaque.
        bool blur = Acrylic.SystemBackdropSupported;
        if (dark)
        {
            Set("BgBrush", "#FF15181F");
            Set("WindowBgBrush", blur ? "#00000000" : "#FF15181F");
            Set("NavBrush", blur ? "#3A1B1F28" : "#FF1B1F28");
            Set("CardBrush", blur ? "#B81F2430" : "#FF1F2430");
            Set("BorderBrush", "#FF2D3442");
            Set("TextBrush", "#FFE6E9EF");
            Set("MutedBrush", "#FF9AA3B2");
            Set("AccentBrush", "#FF5B9BFF");
            Set("AccentHoverBrush", "#FF7CB0FF");
            Set("AccentSoftBrush", "#FF243552");
            Set("HoverBrush", "#FF2A3141");
            Set("PressedBrush", "#FF343C4E");
            Set("InputBrush", "#FF161A22");
            Set("InputBorderBrush", "#FF3A4254");
            Set("DangerBrush", "#FFF87171");
            Set("SwitchOffBrush", "#FF3A4254");
            Set("PopupBrush", "#FF232936");
            Set("ScrollThumbBrush", "#FF3F485A");
        }
        else
        {
            Set("BgBrush", "#FFF5F6FA");
            Set("WindowBgBrush", blur ? "#00000000" : "#FFF5F6FA");
            Set("NavBrush", blur ? "#66FFFFFF" : "#FFFFFFFF");
            Set("CardBrush", blur ? "#C4FFFFFF" : "#FFFFFFFF");
            Set("BorderBrush", "#FFE5E7EB");
            Set("TextBrush", "#FF1F2937");
            Set("MutedBrush", "#FF6B7280");
            Set("AccentBrush", "#FF3B82F6");
            Set("AccentHoverBrush", "#FF2563EB");
            Set("AccentSoftBrush", "#FFE8EFFF");
            Set("HoverBrush", "#FFF3F4F6");
            Set("PressedBrush", "#FFE5E7EB");
            Set("InputBrush", "#FFFFFFFF");
            Set("InputBorderBrush", "#FFD1D5DB");
            Set("DangerBrush", "#FFDC2626");
            Set("SwitchOffBrush", "#FFD1D5DB");
            Set("PopupBrush", "#FFFFFFFF");
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
