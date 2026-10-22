using System.Net;
using System.Net.Sockets;

namespace MediaEngine.Providers.Services.StashBox;

/// <summary>
/// The connection rule for a cover image that is not on this machine: it may only reach a public internet address. The address
/// is checked on the connection itself, after the name is resolved, so a name that resolves to a private address (or is changed
/// between a check and the connection) can never make the Engine fetch from the home network or from itself (security review L7).
/// </summary>
public static class StashBoxPublicConnect
{
    /// <summary>True for an address on the public internet; false for loopback, private, link-local, shared, multicast and unspecified ones.</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
                || bytes[0] == 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 198 && bytes[1] is 18 or 19
                || bytes[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Global unicast only (2000::/3), which leaves out unique-local, link-local, site-local and multicast addresses.
            return (bytes[0] & 0xe0) == 0x20;
        }

        return false;
    }

    /// <summary>A handler that connects only to public addresses and follows no redirect.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        foreach (var address in addresses.Where(IsPublicAddress))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("The cover address does not resolve to a public internet address.");
    }
}
