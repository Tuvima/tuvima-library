using System.Net;
using System.Net.Sockets;

namespace MediaEngine.Domain.Authorization;

/// <summary>
/// Whether a request that is not from this computer or a private address is clearly from outside the home. The Dashboard and the
/// Engine both ask it, so "outside" means one thing. A request is clearly outside when it carries forwarding evidence (a proxy or
/// tunnel is relaying it, or it arrived on the proxy port) or its source address is publicly routable. Loopback, private, link-local,
/// carrier-grade NAT (100.64.0.0/10, which includes Tailscale), unique-local and the container bridge gateway are not evidence on
/// their own: a household behind Docker or on a Tailscale network looks like that.
/// </summary>
public static class OutsideRequestEvidence
{
    /// <param name="forwarded">The visitor's request carried forwarding headers, or the Dashboard said it did.</param>
    /// <param name="viaProxy">The request arrived on the dedicated proxy port, or straight from a configured reverse proxy.</param>
    /// <param name="source">The connection's own address (not a forwarded header).</param>
    /// <param name="containerGateways">The container bridge gateway addresses; one of them is never evidence by itself.</param>
    public static bool IsClearlyOutside(bool forwarded, bool viaProxy, IPAddress? source, IEnumerable<IPAddress>? containerGateways = null)
    {
        if (forwarded || viaProxy)
        {
            return true;
        }

        if (source is null)
        {
            return false;
        }

        source = Unmap(source);
        if (containerGateways is not null && containerGateways.Any(gateway => Unmap(gateway).Equals(source)))
        {
            return false;
        }

        return IsPubliclyRoutable(source);
    }

    /// <summary>True for an address on the public internet. Loopback, private, link-local, 100.64.0.0/10, unique-local, multicast and unspecified are not.</summary>
    public static bool IsPubliclyRoutable(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        address = Unmap(address);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (bytes[0] & 0xFE) == 0xFC); // fc00::/7 unique-local
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return !(bytes[0] == 0
            || bytes[0] == 10
            || bytes[0] == 100 && bytes[1] is >= 64 and <= 127 // 100.64.0.0/10 carrier-grade NAT (Tailscale)
            || bytes[0] == 127
            || bytes[0] == 169 && bytes[1] == 254
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] >= 224); // multicast and reserved
    }

    private static IPAddress Unmap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
