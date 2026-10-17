using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PopBar.Config;
using PopBar.Core;

namespace PopBar.UI;

/// <summary>
/// "代理": the one proxy PopBar knows - type, host, port, optional login - which plugins
/// (<c>host.HttpFor(ProxyRoute.Proxy)</c>) and AI services can choose, and how PopBar itself connects.
/// </summary>
internal partial class ProxyPage : SettingsPage
{
    /// <summary>A site abroad: the usual reason for a proxy.</summary>
    private const string TestUrl = "https://www.gstatic.com/generate_204";

    public ProxyPage()
    {
        InitializeComponent();
        SchemeCombo.ItemsSource = new List<KeyValuePair<string, string>> { new(ProxySchemes.Http, "HTTP"), new(ProxySchemes.Socks5, "SOCKS5") };
        ModeCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new(ProxyModes.System, "系统代理"),
            new(ProxyModes.Custom, "使用代理"),
            new(ProxyModes.None, "不使用代理"),
        };
    }

    protected override void OnLoad(AppConfig c)
    {
        var p = c.Proxy;
        SchemeCombo.SelectedValue = p.Scheme == ProxySchemes.Socks5 ? ProxySchemes.Socks5 : ProxySchemes.Http;
        HostBox.Text = p.Host;
        PortBox.Text = p.Port > 0 ? p.Port.ToString() : "";
        UserBox.Text = p.User;
        PasswordBox.Password = p.Password;
        AuthSw.IsChecked = p.User.Length > 0;
        AuthRow.Visibility = p.User.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ModeCombo.SelectedValue = p.Mode;
        if (ModeCombo.SelectedIndex < 0) ModeCombo.SelectedIndex = 0;
        TestResult.Visibility = Visibility.Collapsed;
    }

    public override void Apply(AppConfig c) => c.Proxy = Draft(ModeCombo.SelectedValue as string ?? ProxyModes.System);

    /// <summary>The proxy as filled in; the login only while 需要认证 is on.</summary>
    private ProxyConfig Draft(string mode)
    {
        bool auth = AuthSw.IsChecked == true;
        return new ProxyConfig
        {
            Mode = mode,
            Scheme = SchemeCombo.SelectedValue as string ?? ProxySchemes.Http,
            Host = HostBox.Text.Trim(),
            Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : 0,
            User = auth ? UserBox.Text.Trim() : "",
            Password = auth ? PasswordBox.Password : "",
        };
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        TestResult.Visibility = Visibility.Collapsed;
        RaiseEdited();
    }

    private void Auth_Changed(object sender, RoutedEventArgs e)
    {
        AuthRow.Visibility = AuthSw.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Field_Changed(sender, e);
    }

    private void Port_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

    /// <summary>Connect through the proxy as filled in (unsaved is fine): any HTTP answer means it works.</summary>
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestResult.Visibility = Visibility.Visible;
        var proxy = Draft(ProxyModes.Custom);
        if (proxy.Host.Length == 0) { Show("请填写主机，本机一般是 127.0.0.1", ok: false); return; }
        if (!proxy.IsSet) { Show("请填写端口（1–65535）", ok: false); return; }
        TestButton.IsEnabled = false;
        Show("测试中…", ok: null);
        try
        {
            using var http = ProxyManager.ForTest(proxy, TimeSpan.FromSeconds(8));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var resp = await http.GetAsync(TestUrl, HttpCompletionOption.ResponseHeadersRead);
            Show($"代理可用 · {sw.ElapsedMilliseconds} ms", ok: true);
        }
        catch (TaskCanceledException) { Show("连接超时：8 秒内没有通过代理连上外网", ok: false); }
        catch (Exception ex) { Show("连接失败：" + (ex.InnerException?.Message ?? ex.Message), ok: false); }
        finally { TestButton.IsEnabled = true; }
    }

    private void Show(string text, bool? ok)
    {
        TestResult.Text = text;
        TestResult.SetResourceReference(TextBlock.ForegroundProperty, ok == null ? "MutedBrush" : ok == true ? "SuccessBrush" : "DangerBrush");
    }
}
