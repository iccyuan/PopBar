using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PopBar.Config;

namespace PopBar.UI;

/// <summary>"关于": version, where the config and log live, the repository. Nothing to save.</summary>
internal partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        var ver = typeof(AboutPage).Assembly.GetName().Version;
        AboutVersion.Text = "v" + ver?.ToString(3) + "  ·  .NET " + Environment.Version;
        ConfigPathRun.Text = ConfigStore.FilePath;
        LogPathRun.Text = System.IO.Path.Combine(ConfigStore.Dir, "popbar.log");
    }

    private void OpenConfigDir_Click(object sender, RoutedEventArgs e) =>
        Open("explorer.exe", $"/select,\"{ConfigStore.FilePath}\"", "open config dir");

    private void OpenLog_Click(object sender, RoutedEventArgs e) =>
        Open(System.IO.Path.Combine(ConfigStore.Dir, "popbar.log"), null, "open log");

    private void OpenRepo_Click(object sender, RoutedEventArgs e) =>
        Open("https://github.com/iccyuan/PopBar", null, "open repo");

    private static void Open(string target, string? args, string what)
    {
        try { Process.Start(new ProcessStartInfo(target) { Arguments = args ?? "", UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(what, ex); }
    }
}
