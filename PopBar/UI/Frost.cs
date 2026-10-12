using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PopBar.Core;
using static PopBar.Native.NativeMethods;

namespace PopBar.UI;

/// <summary>
/// Frosted glass for popups (context menus, combo drop-downs). A popup cannot blur what is under it after it is on
/// screen - the screenshot would contain the popup itself - so the monitor is captured and blurred just before the
/// popup opens (<see cref="Prepare"/>), and a <c>Glass</c> border inside the popup, marked with the attached
/// <see cref="IsGlassProperty"/>, shows the part of that snapshot lying under it once placed. Capture triggers are
/// installed once (<see cref="Install"/>): the mouse press that opens a combo, the ContextMenuOpening of any element,
/// and the tray explicitly.
/// </summary>
internal static class Frost
{
    private const double Scale = 0.5;   // blur at half resolution: 4x cheaper, looks identical through a 3-12px tint
    private const double Radius = 18;

    private static BitmapSource? _shot;
    private static RECT _work;
    private static DateTime _at;

    public static readonly DependencyProperty IsGlassProperty = DependencyProperty.RegisterAttached(
        "IsGlass", typeof(bool), typeof(Frost), new PropertyMetadata(false, OnIsGlassChanged));
    public static void SetIsGlass(DependencyObject d, bool value) => d.SetValue(IsGlassProperty, value);
    public static bool GetIsGlass(DependencyObject d) => (bool)d.GetValue(IsGlassProperty);

    /// <summary>Hook the moments right before a popup appears, app-wide.</summary>
    public static void Install()
    {
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((s, _) => { if (s is ComboBox { IsDropDownOpen: false }) PrepareAtCursor(); }));
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewKeyDownEvent, new KeyEventHandler((s, e) =>
        {
            if (s is ComboBox { IsDropDownOpen: false } cb && (e.Key == Key.F4 || (e.SystemKey == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt)))
                PrepareAt(cb);
        }));
        // Every element's ContextMenu property goes through ContextMenuOpening before the menu shows.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler((s, _) => { if (s is FrameworkElement { ContextMenu: not null }) PrepareAtCursor(); }));
    }

    /// <summary>Blur the monitor under the cursor; call right before opening a popup there.</summary>
    public static void PrepareAtCursor()
    {
        if (GetCursorPos(out var p)) Prepare(p.X, p.Y);
    }

    private static void PrepareAt(Visual v)
    {
        try
        {
            var o = v is UIElement e ? e.PointToScreen(new Point(0, 0)) : new Point();
            Prepare((int)o.X, (int)o.Y);
        }
        catch { PrepareAtCursor(); }
    }

    /// <summary>Snapshot the monitor containing a physical screen point. Cheap to call repeatedly: a fresh shot is reused.</summary>
    public static void Prepare(int x, int y)
    {
        if (ThemeManager.BlurOpacity >= 100) { _shot = null; return; }
        var (work, _) = GetMonitorAt(x, y);
        if (_shot != null && (DateTime.UtcNow - _at).TotalMilliseconds < 250 && work.Left == _work.Left && work.Top == _work.Top) return;
        _shot = FrostedGlass.CaptureScaled(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top, Scale, Radius);
        _work = work;
        _at = DateTime.UtcNow;
    }

    private static void OnIsGlassChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border b || e.NewValue is not true) return;
        // The popup content is shown again on every open; place the snapshot once it is on screen and laid out.
        b.IsVisibleChanged += (_, _) => { if (b.IsVisible) b.Dispatcher.InvokeAsync(() => Apply(b), System.Windows.Threading.DispatcherPriority.Loaded); };
        b.SizeChanged += (_, _) => { if (b.IsVisible) Apply(b); };
    }

    /// <summary>Show the slice of the snapshot under the border; without a usable snapshot fall back to the solid popup colour.</summary>
    private static void Apply(Border b)
    {
        var shot = _shot;
        var source = PresentationSource.FromVisual(b);
        if (shot == null || (DateTime.UtcNow - _at).TotalSeconds > 5 || source == null || b.ActualWidth <= 0 || b.ActualHeight <= 0)
        {
            b.Background = Application.Current.TryFindResource("PopupBrush") as Brush ?? Brushes.White;
            return;
        }
        try
        {
            var origin = b.PointToScreen(new Point(0, 0)); // device pixels
            double dpi = source.CompositionTarget?.TransformToDevice.M11 ?? 1;
            var brush = new ImageBrush(shot)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect((origin.X - _work.Left) * Scale, (origin.Y - _work.Top) * Scale, b.ActualWidth * dpi * Scale, b.ActualHeight * dpi * Scale),
            };
            brush.Freeze();
            b.Background = brush;
        }
        catch (InvalidOperationException) { /* not connected to a window yet; SizeChanged will come back */ }
    }
}
