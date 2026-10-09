using System.Net;
using System.Net.Sockets;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Web.Services.Configuration;

/// <summary>Where a Dashboard request physically came from.</summary>
public enum IngressKind
{
    /// <summary>The same computer that runs Tuvima Library (loopback).</summary>
    ThisComputer,

    /// <summary>A device on the home network (private address or an administrator-trusted range).</summary>
    HomeNetwork,

    /// <summary>Anything else, including every request that arrived through the proxy port.</summary>
    Remote,
}

/// <summary>
/// The single answer to "is this request local?". It looks only at the connection's processed remote
/// address and the port the request arrived on, never at headers a browser can send. Forwarded headers
/// are applied (on the proxy port only) before this runs, and anything on the proxy port is Remote anyway.
/// </summary>
public sealed class IngressClassifier
{
    private readonly int? _proxyPort;
    private readonly IReadOnlyList<System.Net.IPNetwork> _trustedNetworks;
    private readonly IReadOnlyList<IPAddress> _proxyAddresses;
    private readonly IReadOnlyList<System.Net.IPNetwork> _proxyNetworks;

    /// <param name="proxyPort">The dedicated proxy port, or null when none is configured.</param>
    /// <param name="trustedLocalNetworks"><c>auth.trusted_local_networks</c>: extra ranges that count as the home network.</param>
    /// <param name="trustedProxies">
    /// Configured reverse-proxy addresses/networks. A connection straight from one of them on the main port is a
    /// proxy relaying a visitor whose address is hidden (forwarded headers are not honoured there), so it is Remote.
    /// </param>
    public IngressClassifier(
        int? proxyPort,
        IEnumerable<string>? trustedLocalNetworks,
        IEnumerable<string>? trustedProxies = null,
        IEnumerable<string>? trustedProxyNetworks = null)
    {
        _proxyPort = proxyPort;
        _trustedNetworks = ParseNetworks(trustedLocalNetworks);
        _proxyNetworks = ParseNetworks(trustedProxyNetworks);
        _proxyAddresses = (trustedProxies ?? [])
            .Select(value => IPAddress.TryParse(value, out var address)
                ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
                : null)
            .OfType<IPAddress>()
            .ToArray();
    }

    private static System.Net.IPNetwork[] ParseNetworks(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(value => System.Net.IPNetwork.TryParse(value, out var network) ? (System.Net.IPNetwork?)network : null)
            .OfType<System.Net.IPNetwork>()
            .ToArray();

    public IngressKind Classify(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Classify(context.Connection.RemoteIpAddress, context.Connection.LocalPort);
    }

    /// <summary>
    /// True when a configured reverse proxy connected to the main port. Forwarded headers are ignored there, so every
    /// visitor it relays shows the proxy's own address; the proxy should use the proxy port instead.
    /// </summary>
    public bool IsProxyOnMainPort(IPAddress? address, int localPort)
    {
        if (address is null || (_proxyPort is int proxyPort && localPort == proxyPort))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IsConfiguredProxy(address);
    }

    public IngressKind Classify(IPAddress? address, int localPort)
    {
        if (_proxyPort is int proxyPort && localPort == proxyPort)
        {
            return IngressKind.Remote;
        }

        if (address is null)
        {
            return IngressKind.Remote;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        // A configured proxy is checked first: a reverse proxy on this same machine connects from loopback,
        // and treating that as "this computer" would let every internet visitor skip the setup code.
        if (IsConfiguredProxy(address))
        {
            return IngressKind.Remote;
        }

        if (IPAddress.IsLoopback(address))
        {
            return IngressKind.ThisComputer;
        }

        if (IsPrivate(address) || IsInAny(_trustedNetworks, address))
        {
            return IngressKind.HomeNetwork;
        }

        return IngressKind.Remote;
    }

    private bool IsConfiguredProxy(IPAddress address) =>
        _proxyAddresses.Any(proxy => proxy.Equals(address)) || IsInAny(_proxyNetworks, address);

    private static bool IsInAny(IReadOnlyList<System.Net.IPNetwork> networks, IPAddress address)
    {
        foreach (var network in networks)
        {
            if (network.BaseAddress.AddressFamily == address.AddressFamily)
            {
                if (network.Contains(address))
                {
                    return true;
                }
            }
            else if (network.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6
                && address.AddressFamily == AddressFamily.InterNetwork
                && network.Contains(address.MapToIPv6()))
            {
                // An IPv4 address against an IPv4-mapped IPv6 range such as ::ffff:10.0.0.0/104.
                return true;
            }
        }

        return false;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fe80::/10 link-local and fc00::/7 unique-local.
            return address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
        }

        return address.AddressFamily == AddressFamily.InterNetwork
            && (bytes[0] == 10
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 169 && bytes[1] == 254);
    }
}

public static class IngressClassifierExtensions
{
    /// <summary>The wire value (<c>this_computer</c>, <c>home_network</c> or <c>remote</c>) sent to the Engine.</summary>
    public static string ToWireValue(this IngressKind kind) => kind switch
    {
        IngressKind.ThisComputer => ClientIngressValues.ThisComputer,
        IngressKind.HomeNetwork => ClientIngressValues.HomeNetwork,
        _ => ClientIngressValues.Remote,
    };

    /// <summary>Reads a wire value back into a place. Anything unrecognised counts as remote (fail closed).</summary>
    public static IngressKind FromWireValue(string? value) => value switch
    {
        ClientIngressValues.ThisComputer => IngressKind.ThisComputer,
        ClientIngressValues.HomeNetwork => IngressKind.HomeNetwork,
        _ => IngressKind.Remote,
    };

    /// <summary>Where this request came from, as the Engine's wire value.</summary>
    public static string ClientIngress(this HttpContext context) =>
        context.RequestServices.GetRequiredService<IngressClassifier>().Classify(context).ToWireValue();

    /// <summary>
    /// True when the visitor's own request carried forwarding headers. A tunnel or proxy running on this computer looks
    /// like loopback, so the Engine does not offer the no-password sign-in to a visitor it may be relaying.
    /// </summary>
    public static bool WasForwarded(this HttpContext context) =>
        context.Request.Headers.ContainsKey("X-Forwarded-For")
        || context.Request.Headers.ContainsKey("Forwarded")
        || context.Request.Headers.ContainsKey("Via");
}
