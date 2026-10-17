using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using PopBar.Config;

namespace PopBar.Core;

/// <summary>
/// The HttpClients PopBar and its plugins use, one per <see cref="ProxyRoute"/>: as PopBar connects (the "代理" page),
/// directly, or through the proxy set there. AI services get theirs by their own route (<see cref="ForService"/>).
/// A client picks its connection on every request, so a change applies at once, even to clients a plugin holds on
/// to; clients going the same way share one connection pool. Local and LAN hosts are always reached directly.
/// </summary>
internal sealed class ProxyManager
{
    private readonly ConfigStore _store;
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _routes = new();
    private readonly ConcurrentDictionary<Sdk.ProxyRoute, HttpClient> _clients = new();
    private readonly ConcurrentDictionary<string, HttpClient> _services = new(StringComparer.OrdinalIgnoreCase);

    public ProxyManager(ConfigStore store) => _store = store;

    /// <summary>PopBar's own requests (model catalog, plugin downloads), and every plugin's <c>host.Http</c>.</summary>
    public HttpClient Main => For(Sdk.ProxyRoute.Default);

    /// <summary>The client for a route (<c>host.HttpFor</c>).</summary>
    public HttpClient For(Sdk.ProxyRoute route) => _clients.GetOrAdd(route, r => Create(() => Setting(_store.Current.Proxy, r)));

    /// <summary>Requests to an AI service (by provider id), whichever plugin asked: the route set for that service.</summary>
    public HttpClient ForService(string providerId) =>
        _services.GetOrAdd(providerId, id => Create(() =>
        {
            var c = _store.Current;
            var route = c.LlmServices.FirstOrDefault(s => string.Equals(s.Protocol, id, StringComparison.OrdinalIgnoreCase))?.Proxy ?? Sdk.ProxyRoute.Default;
            return Setting(c.Proxy, route);
        }));

    /// <summary>What a route comes to: PopBar's own way, direct, or the proxy (Windows' when none is set).</summary>
    internal static ProxyConfig Setting(ProxyConfig proxy, Sdk.ProxyRoute route) => route switch
    {
        Sdk.ProxyRoute.Direct => new ProxyConfig { Mode = ProxyModes.None },
        Sdk.ProxyRoute.Proxy => string.IsNullOrWhiteSpace(proxy.Address)
            ? new ProxyConfig { Mode = ProxyModes.System }
            : new ProxyConfig { Mode = ProxyModes.Custom, Address = proxy.Address },
        _ => proxy,
    };

    /// <summary>A client fixed to one setting with connections of its own, for a connection test of a draft.</summary>
    public static HttpClient ForTest(ProxyConfig proxy, TimeSpan timeout) =>
        Configure(new HttpClient(NewHandler(proxy), disposeHandler: true) { Timeout = timeout });

    private HttpClient Create(Func<ProxyConfig> setting) =>
        Configure(new HttpClient(new Router(this, setting), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) });

    private static HttpClient Configure(HttpClient http)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PopBar/{typeof(ProxyManager).Assembly.GetName().Version?.ToString(3)} (+https://github.com/iccyuan/PopBar)");
        return http;
    }

    /// <summary>Sends each request through the shared handler of the route its setting names at that moment.</summary>
    private sealed class Router(ProxyManager owner, Func<ProxyConfig> setting) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            owner.Route(setting()).SendAsync(request, ct);
    }

    private HttpMessageInvoker Route(ProxyConfig proxy) =>
        _routes.GetOrAdd(RouteKey(proxy), _ => new HttpMessageInvoker(NewHandler(proxy), disposeHandler: true));

    /// <summary>Settings that lead the same way share a handler: "system", "none", or "custom:" + the address.</summary>
    private static string RouteKey(ProxyConfig p) => p.Mode switch
    {
        ProxyModes.None => ProxyModes.None,
        ProxyModes.Custom => ProxyModes.Custom + ":" + p.Address.Trim(),
        _ => ProxyModes.System,
    };

    private static SocketsHttpHandler NewHandler(ProxyConfig p)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10), // pick up DNS / system proxy changes now and then
        };
        switch (p.Mode)
        {
            case ProxyModes.None:
                handler.UseProxy = false;
                break;
            case ProxyModes.Custom when TryParseAddress(p.Address, out var uri, out var credentials, out _):
                handler.Proxy = new LocalBypass(new WebProxy(uri) { Credentials = credentials });
                break;
            case ProxyModes.Custom:
                if (!string.IsNullOrWhiteSpace(p.Address)) Log.Warn($"proxy address \"{p.Address}\" is not valid; using the system proxy");
                handler.Proxy = new LocalBypass(WebRequest.GetSystemWebProxy());
                break;
            default:
                handler.Proxy = new LocalBypass(WebRequest.GetSystemWebProxy());
                break;
        }
        return handler;
    }

    /// <summary>
    /// A proxy address as typed: "127.0.0.1:7890" (HTTP), "http://host:port", "socks5://host:port", "socks4://..."; a
    /// user and password may come before the host ("http://user:pass@host:port").
    /// </summary>
    public static bool TryParseAddress(string? text, out Uri uri, out NetworkCredential? credentials, out string error)
    {
        uri = null!;
        credentials = null;
        var s = (text ?? "").Trim();
        if (s.Length == 0) { error = "请填写代理地址"; return false; }
        if (!s.Contains("://")) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host)) { error = "地址格式不对，例如 127.0.0.1:7890 或 socks5://127.0.0.1:1080"; return false; }
        var scheme = parsed.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "socks5" or "socks4" or "socks4a")) { error = "只支持 http、socks5、socks4 代理"; return false; }
        if (parsed.IsDefaultPort && !s.Contains(":" + parsed.Port)) { error = "请写上端口，例如 127.0.0.1:7890"; return false; }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            var parts = Uri.UnescapeDataString(parsed.UserInfo).Split(':', 2);
            credentials = new NetworkCredential(parts[0], parts.Length > 1 ? parts[1] : "");
        }
        uri = new UriBuilder(parsed.Scheme, parsed.Host, parsed.Port).Uri;
        error = "";
        return true;
    }

    /// <summary>Hosts never sent through a proxy: this machine and private networks.</summary>
    internal static bool IsLocal(Uri uri)
    {
        var host = uri.IdnHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !host.Contains('.') && !host.Contains(':')) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>A proxy that leaves local and LAN hosts alone.</summary>
    private sealed class LocalBypass(IWebProxy inner) : IWebProxy
    {
        public ICredentials? Credentials { get => inner.Credentials; set => inner.Credentials = value; }
        public Uri? GetProxy(Uri destination) => inner.GetProxy(destination);
        public bool IsBypassed(Uri host) => IsLocal(host) || inner.IsBypassed(host);
    }

    /// <summary>
    /// .NET connects through a dual-mode IPv6 socket by default, i.e. to "::ffff:a.b.c.d" for IPv4 hosts. Some
    /// machines (VPN / TUN adapters, filtering drivers) reject exactly that with WSAEACCES (10013) while plain IPv4
    /// sockets work - curl and browsers are fine, PopBar is not. Connect with a socket of the address's own family.
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
        Exception? last = null;
        foreach (var ip in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose(); // cancelled: don't leak, don't try the next address
                throw;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
