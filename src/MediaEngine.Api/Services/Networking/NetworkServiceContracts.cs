using System.Net;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Api.Services.Networking;

public sealed record GatewayCandidate(IPAddress GatewayAddress, IPAddress InternalAddress, string InterfaceName);

public interface IGatewayDiscoveryService
{
    IReadOnlyList<GatewayCandidate> GetIpv4Gateways();
}

public interface INetworkEnvironmentService
{
    IReadOnlyList<NetworkAddressDto> GetUsableAddresses(bool includeIpv6);
}

public interface INetworkTopologyService
{
    NetworkTopologySnapshot GetSnapshot();
}

public sealed record NetworkTopologySnapshot(
    string Kind,
    bool SupportsRouterDiscovery,
    string Detail,
    string? GatewayAddress,
    string? InterfaceName);

public interface INetworkDiagnosticsService
{
    Task<NetworkTestResultDto> TestLocalAsync(CancellationToken ct);
    Task<NetworkTestResultDto> TestRemoteAsync(CancellationToken ct);
    Task<PortAvailabilityResultDto> CheckPortAvailabilityAsync(int port, CancellationToken ct);
    Task<NetworkBandwidthStatusDto> TestBandwidthAsync(CancellationToken ct);
}
