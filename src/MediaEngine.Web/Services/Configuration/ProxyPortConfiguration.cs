using System.Net;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Web.Services.Configuration;

/// <summary>Works out which port (if any) the Dashboard also listens on for a same-machine reverse proxy.</summary>
public static class ProxyPortConfiguration
{
    /// <summary>The port the bundled Tailscale preset (deploy/tailscale) proxies to.</summary>
    public const int TailscalePresetPort = 5017;

    /// <summary>
    /// <c>TUVIMA_PROXY_PORT</c> wins, then <c>remote.proxy_port</c>; with neither, a configured
    /// <c>TUVIMA_TAILSCALE_URL</c> turns on the preset port so Tailscale Serve never lands on the main port.
    /// Invalid values and the main port itself are ignored.
    /// </summary>
    public static int? Resolve(NetworkSettings network, string? environmentValue, string? tailscaleUrl)
    {
        var main = network.Local.Port;
        if (int.TryParse(environmentValue, out var fromEnvironment)
            && fromEnvironment is >= 1 and <= 65535
            && fromEnvironment != main)
        {
            return fromEnvironment;
        }

        if (network.Remote.ProxyPort is int configured && configured != main)
        {
            return configured;
        }

        return !string.IsNullOrWhiteSpace(tailscaleUrl) && TailscalePresetPort != main
            ? TailscalePresetPort
            : null;
    }

    /// <summary>True when a semicolon-separated URL list (ASPNETCORE_URLS style) already binds <paramref name="port"/>.</summary>
    public static bool UrlsContainPort(string urls, int port) =>
        urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(url =>
            {
                var trimmed = url.TrimEnd('/');
                var colon = trimmed.LastIndexOf(':');
                return colon >= 0
                    && !trimmed[(colon + 1)..].Contains(']')
                    && int.TryParse(trimmed[(colon + 1)..], out var bound)
                    && bound == port;
            });

    /// <summary>True when an administrator trusts a proxy that is not on this computer (for example a Docker network).</summary>
    public static bool HasNonLoopbackProxies(RemoteNetworkSettings remote) =>
        remote.TrustedProxyNetworks.Count > 0
        || remote.TrustedProxies.Any(value => !IPAddress.TryParse(value, out var address) || !IPAddress.IsLoopback(address));

    /// <summary>
    /// A same-machine proxy only needs loopback, so the proxy port stays off the network unless a
    /// non-loopback proxy is trusted.
    /// </summary>
    public static string BindUrl(RemoteNetworkSettings remote, int port) =>
        HasNonLoopbackProxies(remote) ? $"http://0.0.0.0:{port}" : $"http://localhost:{port}";
}
