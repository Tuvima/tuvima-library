using System.Net;
using MediaEngine.Domain.Configuration;
using Microsoft.AspNetCore.HttpOverrides;

namespace MediaEngine.Web.Services.Configuration;

public static class ForwardedHeaderConfiguration
{
    public static void Configure(
        ForwardedHeadersOptions options,
        RemoteNetworkSettings remote,
        string? tailscaleUrl)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        // Applies only on the proxy port (see Program.cs), so a same-machine proxy is trusted there and nowhere else.
        AddProxy(options, IPAddress.Loopback);
        AddProxy(options, IPAddress.IPv6Loopback);

        foreach (var address in remote.TrustedProxies)
        {
            if (IPAddress.TryParse(address, out var proxy))
            {
                AddProxy(options, proxy);
            }
        }

        foreach (var cidr in remote.TrustedProxyNetworks)
        {
            if (System.Net.IPNetwork.TryParse(cidr, out var network)
                && !options.KnownIPNetworks.Contains(network))
            {
                options.KnownIPNetworks.Add(network);
                if (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var mapped = new System.Net.IPNetwork(
                        network.BaseAddress.MapToIPv6(),
                        96 + network.PrefixLength);
                    if (!options.KnownIPNetworks.Contains(mapped))
                    {
                        options.KnownIPNetworks.Add(mapped);
                    }
                }
            }
        }

        foreach (var value in new[] { remote.PublicHostname, tailscaleUrl })
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && !options.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                options.AllowedHosts.Add(uri.Host);
            }
        }
    }

    /// <summary>
    /// Applies forwarded headers only to requests that arrived on the dedicated proxy port. On every other port
    /// (including the main one) an <c>X-Forwarded-*</c> header from the network is simply ignored.
    /// </summary>
    public static IApplicationBuilder UseForwardedHeadersOnProxyPort(this IApplicationBuilder app, int? proxyPort) =>
        app.UseWhen(
            context => proxyPort is int port && context.Connection.LocalPort == port,
            branch => branch.UseForwardedHeaders());

    private static void AddProxy(ForwardedHeadersOptions options, IPAddress address)
    {
        if (!options.KnownProxies.Contains(address))
        {
            options.KnownProxies.Add(address);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var mapped = address.MapToIPv6();
            if (!options.KnownProxies.Contains(mapped))
            {
                options.KnownProxies.Add(mapped);
            }
        }
    }
}
