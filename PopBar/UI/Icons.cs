using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PopBar.UI;

/// <summary>Stroke icons (Lucide, 24x24 viewbox) as WPF path data, looked up by name.</summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Data = new(StringComparer.OrdinalIgnoreCase)
    {
        ["copy"] = "M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2z M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2",
        ["search"] = "M19 11a8 8 0 1 1 -16 0a8 8 0 1 1 16 0 M21 21l-4.3-4.3",
        ["languages"] = "M5 8l6 6 M4 14l6-6 2-3 M2 5h12 M7 2h1 M22 22l-5-10-5 10 M14 18h6",
        ["external-link"] = "M15 3h6v6 M10 14L21 3 M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6",
        ["link"] = "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71",
        ["send"] = "M14.536 21.686a.5.5 0 0 0 .937-.024l6.5-19a.496.496 0 0 0-.635-.635l-19 6.5a.5.5 0 0 0-.024.937l7.93 3.18a2 2 0 0 1 1.112 1.11z M21.854 2.147l-10.94 10.939",
        ["mail"] = "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M22 7l-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7",
        ["case-upper"] = "M3 15l4-8 4 8 M4 13h6 M15 11h4.5a2 2 0 0 1 0 4h-4.5V7h4a2 2 0 0 1 0 4",
        ["case-lower"] = "M10 12a3 3 0 1 1 -6 0a3 3 0 1 1 6 0 M10 9v6 M20 12a3 3 0 1 1 -6 0a3 3 0 1 1 6 0 M14 7v8",
        ["clipboard"] = "M9 2h6a1 1 0 0 1 1 1v2a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1z M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2",
        ["clipboard-paste"] = "M15 2H9a1 1 0 0 0-1 1v2c0 .6.4 1 1 1h6c.6 0 1-.4 1-1V3c0-.6-.4-1-1-1Z M8 4H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h4 M16 4h2a2 2 0 0 1 2 2v2 M11 14h10 M17 10l4 4-4 4",
        ["terminal"] = "M4 17l6-6-6-6 M12 19h8",
        ["globe"] = "M22 12a10 10 0 1 1 -20 0a10 10 0 1 1 20 0 M12 2a14.5 14.5 0 0 0 0 20 M12 2a14.5 14.5 0 0 1 0 20 M2 12h20",
        ["scissors"] = "M9 6a3 3 0 1 1 -6 0a3 3 0 1 1 6 0 M8.12 8.12L12 12 M20 4L8.12 15.88 M9 18a3 3 0 1 1 -6 0a3 3 0 1 1 6 0 M14.8 14.8L20 20",
        ["sparkles"] = "M9.937 15.5A2 2 0 0 0 8.5 14.063l-6.135-1.582a.5.5 0 0 1 0-.962L8.5 9.936A2 2 0 0 0 9.937 8.5l1.582-6.135a.5.5 0 0 1 .963 0L14.063 8.5A2 2 0 0 0 15.5 9.937l6.135 1.581a.5.5 0 0 1 0 .964L15.5 14.063a2 2 0 0 0-1.437 1.437l-1.582 6.135a.5.5 0 0 1-.963 0z M20 3v4 M22 5h-4 M4 17v2 M5 18H3",
        ["hash"] = "M4 9h16 M4 15h16 M10 3L8 21 M16 3l-2 18",
        ["type"] = "M4 7V4h16v3 M9 20h6 M12 4v16",
        ["bookmark"] = "M19 21l-7-4-7 4V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v16z",
        ["message-square"] = "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z",
        ["bot"] = "M12 8V4H8 M4 8h16a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2z M2 14h2 M20 14h2 M15 13v2 M9 13v2",
        ["calculator"] = "M4 2h16a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z M8 6h8 M16 14v4 M16 10h.01 M12 10h.01 M8 10h.01 M12 14h.01 M8 14h.01 M12 18h.01 M8 18h.01",
        ["book-open"] = "M12 7v14 M3 18a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h5a4 4 0 0 1 4 4 4 4 0 0 1 4-4h5a1 1 0 0 1 1 1v13a1 1 0 0 1-1 1h-6a3 3 0 0 0-3 3 3 3 0 0 0-3-3z",
        ["pencil"] = "M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z M15 5l4 4",
        ["trash"] = "M3 6h18 M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6 M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2",
        ["arrow-up"] = "M12 19V5 M5 12l7-7 7 7",
        ["arrow-down"] = "M12 5v14 M19 12l-7 7-7-7",
        ["plus"] = "M5 12h14 M12 5v14",
        ["settings"] = "M15 12a3 3 0 1 1 -6 0a3 3 0 1 1 6 0 M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.39a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z",
        ["circle"] = "M22 12a10 10 0 1 1 -20 0a10 10 0 1 1 20 0",
        ["star"] = "M11.525 2.295a.53.53 0 0 1 .95 0l2.31 4.679a2.123 2.123 0 0 0 1.595 1.16l5.166.756a.53.53 0 0 1 .294.904l-3.736 3.638a2.123 2.123 0 0 0-.611 1.878l.882 5.14a.53.53 0 0 1-.771.56l-4.618-2.428a2.122 2.122 0 0 0-1.973 0L6.396 21.01a.53.53 0 0 1-.77-.56l.881-5.139a2.122 2.122 0 0 0-.611-1.879L2.16 9.795a.53.53 0 0 1 .294-.906l5.165-.755a2.122 2.122 0 0 0 1.597-1.16z",
        ["quote"] = "M16 3a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2 1 1 0 0 1 1 1v1a2 2 0 0 1-2 2 1 1 0 0 0-1 1v2a1 1 0 0 0 1 1 6 6 0 0 0 6-6V5a2 2 0 0 0-2-2z M5 3a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2 1 1 0 0 1 1 1v1a2 2 0 0 1-2 2 1 1 0 0 0-1 1v2a1 1 0 0 0 1 1 6 6 0 0 0 6-6V5a2 2 0 0 0-2-2z",
        ["x"] = "M18 6L6 18 M6 6l12 12",
        ["puzzle"] = "M19.439 7.85c-.049.322.059.648.289.878l1.568 1.568c.47.47.706 1.087.706 1.704s-.235 1.233-.706 1.704l-1.611 1.611a.98.98 0 0 1-.837.276c-.47-.07-.802-.48-.968-.925a2.501 2.501 0 1 0-3.214 3.214c.446.166.855.497.925.968a.979.979 0 0 1-.276.837l-1.61 1.61a2.404 2.404 0 0 1-1.705.707 2.402 2.402 0 0 1-1.704-.706l-1.568-1.568a1.026 1.026 0 0 0-.877-.29c-.493.074-.84.504-1.02.968a2.5 2.5 0 1 1-3.237-3.237c.464-.18.894-.527.967-1.02a1.026 1.026 0 0 0-.289-.877l-1.568-1.568A2.402 2.402 0 0 1 1.998 12c0-.617.236-1.234.706-1.704L4.23 8.77c.24-.24.581-.353.917-.303.515.077.877.528 1.073 1.01a2.5 2.5 0 1 0 3.259-3.259c-.482-.196-.933-.558-1.01-1.073-.05-.336.062-.676.303-.917l1.525-1.525A2.402 2.402 0 0 1 12 1.998c.617 0 1.234.236 1.704.706l1.568 1.568c.23.23.556.338.877.29.493-.074.84-.504 1.02-.968a2.5 2.5 0 1 1 3.237 3.237c-.464.18-.894.527-.967 1.02Z",
        ["info"] = "M22 12a10 10 0 1 1 -20 0a10 10 0 1 1 20 0 M12 16v-4 M12 8h.01",
        ["zap"] = "M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z",
        ["sliders"] = "M4 21v-7 M4 10V3 M12 21v-9 M12 8V3 M20 21v-5 M20 12V3 M2 14h4 M10 8h4 M18 16h4",
        ["mouse-pointer"] = "M12.586 12.586 19 19 M3.688 3.037a.497.497 0 0 0-.651.651l6.5 15.999a.501.501 0 0 0 .947-.062l1.569-6.083a2 2 0 0 1 1.448-1.479l6.124-1.579a.5.5 0 0 0 .063-.947z",
        ["chevron-down"] = "M6 9l6 6 6-6",
        ["chevron-right"] = "M9 18l6-6-6-6",
        ["chevron-left"] = "M15 18l-6-6 6-6",
        ["power"] = "M12 2v10 M18.4 6.6a9 9 0 1 1-12.77.04",
        ["toggle-right"] = "M2 12a5 5 0 0 1 5-5h10a5 5 0 0 1 0 10H7a5 5 0 0 1-5-5z M17 12a2 2 0 1 1-4 0a2 2 0 0 1 4 0",
        ["toggle-left"] = "M2 12a5 5 0 0 1 5-5h10a5 5 0 0 1 0 10H7a5 5 0 0 1-5-5z M11 12a2 2 0 1 1-4 0a2 2 0 0 1 4 0",
        ["circle-alert"] = "M12 22a10 10 0 1 0 0-20a10 10 0 0 0 0 20z M12 8v4 M12 16h.01",
        ["server"] = "M4 2h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z M4 14h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2z M6 6h.01 M6 18h.01",
        ["pin"] = "M12 17v5 M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z",
        ["grip-vertical"] = "M10 5a1 1 0 1 1 -2 0a1 1 0 1 1 2 0 M10 12a1 1 0 1 1 -2 0a1 1 0 1 1 2 0 M10 19a1 1 0 1 1 -2 0a1 1 0 1 1 2 0 M16 5a1 1 0 1 1 -2 0a1 1 0 1 1 2 0 M16 12a1 1 0 1 1 -2 0a1 1 0 1 1 2 0 M16 19a1 1 0 1 1 -2 0a1 1 0 1 1 2 0",
        ["qr-code"] = "M4 3h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z M17 3h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1h-3a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z M4 16h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-3a1 1 0 0 1 1-1z M21 16h-3a2 2 0 0 0-2 2v3 M21 21v.01 M12 7v3a2 2 0 0 1-2 2H7 M3 12h.01 M12 3h.01 M12 16v.01 M16 12h1 M21 12v.01 M12 21v-1",
        ["code"] = "M16 18l6-6-6-6 M8 6l-6 6 6 6",
        ["braces"] = "M8 3H7a2 2 0 0 0-2 2v5a2 2 0 0 1-2 2 2 2 0 0 1 2 2v5c0 1.1.9 2 2 2h1 M16 21h1a2 2 0 0 0 2-2v-5c0-1.1.9-2 2-2a2 2 0 0 1-2-2V5a2 2 0 0 0-2-2h-1",
        ["key"] = "M15.5 7.5l2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4 M21 2l-9.6 9.6 M13 15.5a5.5 5.5 0 1 1 -11 0a5.5 5.5 0 1 1 11 0",
        ["clock"] = "M22 12a10 10 0 1 1 -20 0a10 10 0 1 1 20 0 M12 6v6l4 2",
        ["sticky-note"] = "M16 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V8z M15 3v4a2 2 0 0 0 2 2h4",
        ["scan-text"] = "M3 7V5a2 2 0 0 1 2-2h2 M17 3h2a2 2 0 0 1 2 2v2 M21 17v2a2 2 0 0 1-2 2h-2 M7 21H5a2 2 0 0 1-2-2v-2 M7 8h8 M7 12h10 M7 16h6",
        ["film"] = "M4 3h16a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z M7 3v18 M3 7.5h4 M3 12h18 M3 16.5h4 M17 3v18 M17 7.5h4 M17 16.5h4",
        ["arrow-down-to-line"] = "M12 17V3 M6 11l6 6 6-6 M19 21H5",
        ["scroll-capture"] = "M6 2h12a2 2 0 0 1 2 2v7 M4 11V4a2 2 0 0 1 2-2 M4 15v5a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-5 M9 12l3 3 3-3 M12 7v8",
        ["pause"] = "M7 4h3v16H7z M14 4h3v16h-3z",
        ["eye"] = "M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7z M15 12a3 3 0 1 1 -6 0a3 3 0 1 1 6 0",
        ["eye-off"] = "M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49 M14.084 14.158a3 3 0 0 1-4.242-4.242 M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143 M2 2l20 20",
        ["square"] ="M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z",
        ["move-up-right"] = "M7 17L17 7 M7 7h10v10",
        ["pen"] = "M12 20h9 M16.376 3.622a1 1 0 0 1 3.002 3.002L7.368 18.635a2 2 0 0 1-.855.506l-2.872.838a.5.5 0 0 1-.62-.62l.838-2.872a2 2 0 0 1 .506-.854z",
        ["grid"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M3 9h18 M3 15h18 M9 3v18 M15 3v18",
        ["undo"] = "M9 14L4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11",
        ["download"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3",
        ["check"] = "M20 6L9 17l-5-5",
        ["crop"] = "M6 2v14a2 2 0 0 0 2 2h14 M18 22V8a2 2 0 0 0-2-2H2",
        ["app-window"] = "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M10 4v4 M2 8h20 M6 4v4",
    };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BitmapSource> BitmapCache = new(StringComparer.Ordinal);

    /// <summary>Rasterise an icon at an exact pixel size (cached). Stroke is scaled from icon units to pixels.</summary>
    public static BitmapSource Render(string? name, int px, Color color, double thicknessUnits = 2.0)
    {
        name = string.IsNullOrWhiteSpace(name) ? "circle" : name;
        var key = $"{name}|{px}|{color}|{thicknessUnits}";
        lock (BitmapCache)
        {
            if (BitmapCache.TryGetValue(key, out var cached)) return cached;
            var geo = Get(name);
            double s = px / 24.0;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var pen = new Pen(brush, thicknessUnits * s) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(s, s));
                dc.DrawGeometry(null, pen, geo);
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            BitmapCache[key] = rtb;
            return rtb;
        }
    }

    public static IEnumerable<string> Names => Data.Keys.OrderBy(k => k, StringComparer.Ordinal);

    // Static accessors for XAML ({x:Static ui:Icons.Pencil}).
    public static Geometry ArrowUp => Get("arrow-up");
    public static Geometry ArrowDown => Get("arrow-down");
    public static Geometry Pencil => Get("pencil");
    public static Geometry Trash => Get("trash");
    public static Geometry Plus => Get("plus");
    public static Geometry MousePointer => Get("mouse-pointer");
    public static Geometry SlidersGeometry => Get("sliders");
    public static Geometry ZapGeometry => Get("zap");
    public static Geometry PuzzleGeometry => Get("puzzle");
    public static Geometry InfoGeometry => Get("info");
    public static Geometry AppWindowGeometry => Get("app-window");

    public static Geometry Get(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "circle" : name;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var g)) return g;
            Geometry geo;
            try
            {
                geo = Geometry.Parse(Data.TryGetValue(name, out var d) ? d : Data["circle"]);
            }
            catch (Exception ex)
            {
                Log.Warn($"icon '{name}' failed to parse: {ex.Message}");
                geo = Geometry.Parse(Data["circle"]);
            }
            geo.Freeze();
            Cache[name] = geo;
            return geo;
        }
    }
}
