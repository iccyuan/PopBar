using System.Windows;

namespace PopBar.Sdk;

/// <summary>Conventions for input a plugin synthesizes itself (SendInput).</summary>
public static class HostInput
{
    /// <summary>
    /// Put this in <c>dwExtraInfo</c> of every keyboard / mouse event you inject with SendInput: PopBar's own input
    /// hooks skip tagged events, so a synthesized wheel or key is not mistaken for the user's (which would hide the
    /// bar or start a selection).
    /// </summary>
    public const nuint InjectedTag = 0x504F5042; // "POPB"
}

/// <summary>Marks windows that are PopBar's own transient chrome.</summary>
public static class HostChrome
{
    /// <summary>
    /// True on PopBar's own transient windows (the action bar). Tools that pick or capture windows on screen, such as
    /// the screenshot plugin, should skip them: the user just clicked through one, it is never what they meant.
    /// </summary>
    public static readonly DependencyProperty IsChromeProperty =
        DependencyProperty.RegisterAttached("IsChrome", typeof(bool), typeof(HostChrome), new PropertyMetadata(false));

    public static bool GetIsChrome(DependencyObject o) => (bool)o.GetValue(IsChromeProperty);
    public static void SetIsChrome(DependencyObject o, bool value) => o.SetValue(IsChromeProperty, value);
}
