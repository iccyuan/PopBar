using PopBar.Config;
using PopBar.Core;

namespace PopBar.Tests;

public class ProxyTests
{
    [Theory]
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890/")]
    [InlineData("http://proxy.lan:8080", "http://proxy.lan:8080/")]
    [InlineData("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080/")]
    [InlineData(" socks4://10.0.0.2:1080 ", "socks4://10.0.0.2:1080/")]
    public void Proxy_addresses_parse(string text, string expected)
    {
        Assert.True(ProxyManager.TryParseAddress(text, out var uri, out var credentials, out _));
        Assert.Equal(expected, uri.ToString());
        Assert.Null(credentials);
    }

    [Fact]
    public void Credentials_come_out_of_the_address()
    {
        Assert.True(ProxyManager.TryParseAddress("http://me:p%40ss@127.0.0.1:7890", out var uri, out var credentials, out _));
        Assert.Equal("http://127.0.0.1:7890/", uri.ToString()); // not sent as part of the proxy URI
        Assert.Equal("me", credentials!.UserName);
        Assert.Equal("p@ss", credentials.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1")]           // no port
    [InlineData("https://127.0.0.1:443")] // TLS to the proxy is not supported
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("not an address")]
    public void Bad_addresses_are_refused_with_a_reason(string text)
    {
        Assert.False(ProxyManager.TryParseAddress(text, out _, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://192.168.1.20:8080", true)]
    [InlineData("http://10.1.2.3", true)]
    [InlineData("http://172.20.0.5", true)]
    [InlineData("http://nas", true)]          // a bare LAN name
    [InlineData("http://[::1]:8080", true)]
    [InlineData("https://api.openai.com", false)]
    [InlineData("http://172.32.0.1", false)]  // just outside 172.16/12
    [InlineData("https://8.8.8.8", false)]
    public void Local_and_lan_hosts_never_go_through_a_proxy(string url, bool local) =>
        Assert.Equal(local, ProxyManager.IsLocal(new Uri(url)));

    [Fact]
    public void Routes_come_to_popbars_way_direct_or_the_proxy()
    {
        var proxy = new ProxyConfig { Mode = ProxyModes.None, Address = "127.0.0.1:7890" };
        // PopBar connects directly, a plugin still uses the proxy
        Assert.Same(proxy, ProxyManager.Setting(proxy, PopBar.Sdk.ProxyRoute.Default));
        var via = ProxyManager.Setting(proxy, PopBar.Sdk.ProxyRoute.Proxy);
        Assert.Equal((ProxyModes.Custom, "127.0.0.1:7890"), (via.Mode, via.Address));
        // PopBar uses the proxy, a plugin goes direct
        proxy.Mode = ProxyModes.Custom;
        Assert.Equal(ProxyModes.None, ProxyManager.Setting(proxy, PopBar.Sdk.ProxyRoute.Direct).Mode);
        // no address set: "use the proxy" means Windows' proxy
        Assert.Equal(ProxyModes.System, ProxyManager.Setting(new ProxyConfig(), PopBar.Sdk.ProxyRoute.Proxy).Mode);
    }

    [Fact]
    public void Old_configs_keep_using_the_system_proxy()
    {
        var c = ConfigStore.Parse($$"""{ "configVersion": {{new AppConfig().ConfigVersion}}, "llmServices": [ { "protocol": "openai" } ] }""", out _);
        Assert.Equal(ProxyModes.System, c.Proxy.Mode);
        Assert.Equal(PopBar.Sdk.ProxyRoute.Default, c.LlmServices[0].Proxy);
    }
}
