namespace MediaEngine.Api.Services.Networking;

public sealed record NetworkTopologyProbe(
    bool RunningInContainer,
    string? DeclaredNetworkMode)
{
    public static NetworkTopologyProbe Capture()
    {
        var declared = Environment.GetEnvironmentVariable("TUVIMA_CONTAINER_NETWORK_MODE")?.Trim().ToLowerInvariant();
        return new NetworkTopologyProbe(ContainerEnvironment.IsContainer(), declared);
    }
}

/// <summary>
/// Describes whether router discovery can actually reach the physical LAN gateway.
/// In Docker bridge mode the visible gateway is the host-side bridge (commonly
/// associated with docker0), not the household router, so PCP/NAT-PMP/SSDP must not
/// be attempted from the application container.
/// </summary>
public sealed class NetworkTopologyService : INetworkTopologyService
{
    private readonly IGatewayDiscoveryService _gateways;
    private readonly NetworkTopologyProbe _probe;

    public NetworkTopologyService(IGatewayDiscoveryService gateways, NetworkTopologyProbe probe)
    {
        _gateways = gateways;
        _probe = probe;
    }

    public NetworkTopologySnapshot GetSnapshot()
    {
        var gateway = _gateways.GetIpv4Gateways().FirstOrDefault();
        if (!_probe.RunningInContainer)
        {
            return new NetworkTopologySnapshot(
                "native",
                gateway is not null,
                gateway is null
                    ? "No LAN gateway was detected."
                    : "Router discovery can use the host's LAN gateway.",
                gateway?.GatewayAddress.ToString(),
                gateway?.InterfaceName);
        }

        if (string.Equals(_probe.DeclaredNetworkMode, "host", StringComparison.OrdinalIgnoreCase))
        {
            return new NetworkTopologySnapshot(
                "docker-host",
                gateway is not null,
                gateway is null
                    ? "Docker host networking is enabled, but no LAN gateway was detected."
                    : "Docker host networking exposes the host LAN gateway to router discovery.",
                gateway?.GatewayAddress.ToString(),
                gateway?.InterfaceName);
        }

        if (string.Equals(_probe.DeclaredNetworkMode, "bridge", StringComparison.OrdinalIgnoreCase)
            || LooksLikeContainerBridge(gateway))
        {
            return new NetworkTopologySnapshot(
                "docker-bridge",
                false,
                "Router discovery sees the Docker bridge (commonly docker0) rather than the LAN router. Use Tailscale or a reverse proxy on the Docker host; automatic router mapping is unsupported in this topology.",
                gateway?.GatewayAddress.ToString(),
                gateway?.InterfaceName);
        }

        return new NetworkTopologySnapshot(
            "container-unknown",
            false,
            "Tuvima is running in a container whose network topology is not declared. Router discovery was not attempted because the visible gateway may be a container bridge rather than the LAN router.",
            gateway?.GatewayAddress.ToString(),
            gateway?.InterfaceName);
    }

    private static bool LooksLikeContainerBridge(GatewayCandidate? gateway)
    {
        if (gateway is null)
        {
            return false;
        }

        var name = gateway.InterfaceName;
        return name.StartsWith("eth", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("veth", StringComparison.OrdinalIgnoreCase);
    }
}
