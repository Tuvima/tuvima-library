using System.Net;
using System.Net.Sockets;
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

    public IngressClassifier(DashboardConfigurationReader configuration)
        : this(configuration.LoadNetwork().Remote.ProxyPort, configuration.LoadCore().Auth.TrustedLocalNetworks)
    {
    }

    public IngressClassifier(int? proxyPort, IEnumerable<string>? trustedLocalNetworks)
    {
        _proxyPort = proxyPort;
        _trustedNetworks = (trustedLocalNetworks ?? [])
            .Select(value => System.Net.IPNetwork.TryParse(value, out var network) ? (System.Net.IPNetwork?)network : null)
            .OfType<System.Net.IPNetwork>()
            .ToArray();
    }

    public IngressKind Classify(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Classify(context.Connection.RemoteIpAddress, context.Connection.LocalPort);
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

        if (IPAddress.IsLoopback(address))
        {
            return IngressKind.ThisComputer;
        }

        if (IsPrivate(address) || IsTrusted(address))
        {
            return IngressKind.HomeNetwork;
        }

        return IngressKind.Remote;
    }

    private bool IsTrusted(IPAddress address)
    {
        foreach (var network in _trustedNetworks)
        {
            if (network.BaseAddress.AddressFamily == address.AddressFamily && network.Contains(address))
            {
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
    /// <summary>True when the request came from this computer or the home network (not <see cref="IngressKind.Remote"/>).</summary>
    public static bool IsLocalIngress(this HttpContext context) =>
        context.RequestServices.GetRequiredService<IngressClassifier>().Classify(context) != IngressKind.Remote;
}
