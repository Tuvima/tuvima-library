using System.Net;

namespace MediaEngine.Web.Services.Configuration;

/// <summary>
/// Finds the default gateway of a container's network. On Docker's bridge network every visitor that reaches the
/// published port arrives as this address, so the Dashboard cannot treat it as proof of a home-network device.
/// </summary>
public static class ContainerGateway
{
    public static bool IsContainer() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TUVIMA_CONTAINER_NETWORK_MODE"))
        || File.Exists("/.dockerenv");

    /// <summary>The default gateways from <c>/proc/net/route</c> when running in a container; empty otherwise.</summary>
    public static IReadOnlyList<IPAddress> Detect()
    {
        if (!IsContainer() || !File.Exists("/proc/net/route"))
        {
            return [];
        }

        try
        {
            return Parse(File.ReadLines("/proc/net/route"));
        }
        catch (IOException)
        {
            // Unreadable routing table: fall back to the normal private-address rule rather than failing startup.
            return [];
        }
    }

    /// <summary>Reads the gateway column of every default route (destination 00000000). Gateways are little-endian hex.</summary>
    public static IReadOnlyList<IPAddress> Parse(IEnumerable<string> routeLines)
    {
        var gateways = new List<IPAddress>();
        foreach (var line in routeLines.Skip(1))
        {
            var columns = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length > 2
                && columns[1] == "00000000"
                && uint.TryParse(columns[2], System.Globalization.NumberStyles.HexNumber, null, out var raw)
                && raw != 0)
            {
                gateways.Add(new IPAddress(raw));
            }
        }

        return gateways;
    }
}
