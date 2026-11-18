using System.Net;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Web.Services.Configuration;

/// <summary>
/// Where the Dashboard listens (home network spec D8). On by default, the Dashboard answers on the home network so
/// phones, TVs and tablets can open it; the Settings switch "Reach from other devices" turns that off and binds it to
/// this computer again on the next start. The Engine is not touched here: it always stays on this computer.
/// A container install stays reachable whatever the switch says, because its published port would otherwise stop
/// working; Settings does not offer the switch there.
/// </summary>
public static class DashboardBinding
{
    public const string AllInterfaces = "0.0.0.0";
    public const string Loopback = "127.0.0.1";

    /// <summary>True when the Dashboard is meant to answer on the home network.</summary>
    public static bool ListensOnNetwork(NetworkSettings network, bool inContainer) =>
        inContainer || network.Local.ReachOtherDevices;

    /// <summary>
    /// The URL list the Dashboard should use, or null to leave the host's own configuration alone.
    /// </summary>
    /// <param name="network">The shared network settings.</param>
    /// <param name="explicitUrls">Hosting configuration (<c>--urls</c>, ASPNETCORE_URLS, container settings), if any.</param>
    /// <param name="proxyUrl">The optional same-machine proxy listener, or null.</param>
    /// <param name="proxyPort">The proxy port behind <paramref name="proxyUrl"/>.</param>
    /// <param name="inContainer">True on a container install.</param>
    public static string? Resolve(NetworkSettings network, string? explicitUrls, string? proxyUrl, int? proxyPort, bool inContainer)
    {
        ArgumentNullException.ThrowIfNull(network);
        var onNetwork = ListensOnNetwork(network, inContainer);
        var proxy = proxyUrl is null ? null : onNetwork ? proxyUrl : NarrowToLoopback(proxyUrl);

        if (string.IsNullOrWhiteSpace(explicitUrls))
        {
            var main = $"http://{(onNetwork ? AllInterfaces : Loopback)}:{network.Local.Port}";
            return proxy is null ? main : $"{main};{proxy}";
        }

        // Explicit hosting settings stay authoritative for development and orchestration, except that the off switch
        // always wins: it must never be possible to switch it off and still be listening on the network.
        var urls = onNetwork ? explicitUrls : NarrowToLoopback(explicitUrls);
        if (proxy is not null && proxyPort is int port && !ProxyPortConfiguration.UrlsContainPort(urls, port))
        {
            urls = $"{urls};{proxy}";
        }

        return string.Equals(urls, explicitUrls, StringComparison.Ordinal) ? null : urls;
    }

    /// <summary>
    /// Rewrites every address in a semicolon-separated URL list that is not already this computer (a wildcard such as
    /// <c>+</c>, <c>*</c>, <c>0.0.0.0</c> or <c>[::]</c>, or a network address) to <c>127.0.0.1</c>, keeping scheme and port.
    /// </summary>
    public static string NarrowToLoopback(string urls)
    {
        var narrowed = new List<string>();
        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var url = NarrowOne(raw);
            if (!narrowed.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                narrowed.Add(url);
            }
        }

        return string.Join(';', narrowed);
    }

    private static string NarrowOne(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return url;
        }

        var scheme = url[..(schemeEnd + 3)];
        var rest = url[(schemeEnd + 3)..];
        var pathStart = rest.IndexOf('/');
        var authority = pathStart < 0 ? rest : rest[..pathStart];
        var path = pathStart < 0 ? string.Empty : rest[pathStart..];

        string host;
        string port;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            host = close < 0 ? authority : authority[..(close + 1)];
            port = close >= 0 && close + 1 < authority.Length ? authority[(close + 1)..] : string.Empty;
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            port = colon < 0 ? string.Empty : authority[colon..];
        }

        return IsThisComputer(host) ? url : $"{scheme}{Loopback}{port}{path}";
    }

    private static bool IsThisComputer(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }
}
