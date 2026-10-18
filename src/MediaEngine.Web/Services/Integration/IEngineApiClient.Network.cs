using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<NetworkSettingsDto?> GetNetworkSettingsAsync(CancellationToken ct = default);
    Task<NetworkSettingsDto?> UpdateNetworkSettingsAsync(NetworkSettingsDto settings, CancellationToken ct = default);
    Task<NetworkRuntimeStatusDto?> GetNetworkRuntimeStatusAsync(CancellationToken ct = default);
    Task<NetworkTestResultDto?> TestLocalNetworkAsync(CancellationToken ct = default);
    Task<NetworkTestResultDto?> TestRemoteNetworkAsync(CancellationToken ct = default);
    Task<NetworkBandwidthStatusDto?> TestNetworkBandwidthAsync(CancellationToken ct = default);
    Task<PortAvailabilityResultDto?> CheckNetworkPortAsync(int port, CancellationToken ct = default);
    Task<PortAvailabilityResultDto?> ApplyNetworkPortAsync(int port, CancellationToken ct = default);
    Task<NetworkRuntimeStatusDto?> RenewNetworkRouterMappingAsync(CancellationToken ct = default);
    Task<NetworkSettingsDto?> ResetNetworkSettingsAsync(CancellationToken ct = default);
}
