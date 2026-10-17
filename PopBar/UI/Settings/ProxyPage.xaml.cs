using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using PopBar.Config;
using PopBar.Core;

namespace PopBar.UI;

/// <summary>
/// "代理": the one proxy PopBar knows - which plugins (<c>host.HttpFor(ProxyRoute.Proxy)</c>) and AI services can choose -
/// and how PopBar itself connects, which "跟随主程序" follows.
/// </summary>
internal partial class ProxyPage : SettingsPage
{
    /// <summary>A site abroad: the usual reason for a proxy.</summary>
    private const string TestUrl = "https://www.gstatic.com/generate_204";

    public ProxyPage()
    {
        InitializeComponent();
        ModeCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new(ProxyModes.System, "系统代理"),
            new(ProxyModes.Custom, "使用代理"),
            new(ProxyModes.None, "不使用代理"),
        };
    }

    protected override void OnLoad(AppConfig c)
    {
        AddressBox.Text = c.Proxy.Address;
        ModeCombo.SelectedValue = c.Proxy.Mode;
        if (ModeCombo.SelectedIndex < 0) ModeCombo.SelectedIndex = 0;
        TestResult.Visibility = Visibility.Collapsed;
    }

    public override void Apply(AppConfig c) => c.Proxy = new ProxyConfig
    {
        Mode = ModeCombo.SelectedValue as string ?? ProxyModes.System,
        Address = AddressBox.Text.Trim(),
    };

    private void Address_Changed(object sender, TextChangedEventArgs e)
    {
        TestResult.Visibility = Visibility.Collapsed;
        RaiseEdited();
    }

    /// <summary>Connect through the address as typed (unsaved is fine): any HTTP answer means the proxy works.</summary>
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestResult.Visibility = Visibility.Visible;
        if (!ProxyManager.TryParseAddress(AddressBox.Text, out _, out _, out var error)) { Show(error, ok: false); return; }
        TestButton.IsEnabled = false;
        Show("测试中…", ok: null);
        try
        {
            using var http = ProxyManager.ForTest(new ProxyConfig { Mode = ProxyModes.Custom, Address = AddressBox.Text.Trim() }, TimeSpan.FromSeconds(8));
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
