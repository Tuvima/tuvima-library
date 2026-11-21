using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<NetworkSettingsDto?> GetNetworkSettingsAsync(CancellationToken ct = default);
    /// <summary>Remembers that the first-run home-network question was answered; no fresh sign-in is needed.</summary>
    Task<bool> MarkFirewallPromptAnsweredAsync(CancellationToken ct = default);
    Task<NetworkSettingsDto?> UpdateNetworkSettingsAsync(NetworkSettingsDto settings, CancellationToken ct = default);
    Task<NetworkRuntimeStatusDto?> GetNetworkRuntimeStatusAsync(CancellationToken ct = default);

    /// <summary>GET /network/app-addresses - the address to type into a Tuvima app, for any signed-in person.</summary>
    Task<NetworkAppAddressesDto?> GetNetworkAppAddressesAsync(CancellationToken ct = default);
    Task<NetworkTestResultDto?> TestLocalNetworkAsync(CancellationToken ct = default);
    Task<NetworkTestResultDto?> TestRemoteNetworkAsync(CancellationToken ct = default);

    /// <summary>GET /network/remote-check - what the away-from-home check found, in plain findings, and the way in Library suggests. Reads only.</summary>
    Task<RemoteCheckResultDto?> GetRemoteCheckAsync(CancellationToken ct = default);

    /// <summary>GET /network/outside-sign-ins - the last attempts to sign in from outside the home network, newest first (administrators only). Null when the Engine could not be asked.</summary>
    Task<IReadOnlyList<OutsideSignInDto>?> GetOutsideSignInsAsync(CancellationToken ct = default);

    /// <summary>POST /network/remote-off - switches away-from-home access off at once; home access is untouched. Null when the Engine could not be asked.</summary>
    Task<RemoteOffResultDto?> TurnRemoteOffAsync(CancellationToken ct = default);

    /// <summary>POST /network/remote-fix/trust-proxy - the one-click fix for "Library does not trust your proxy yet" (administrators only; safe to repeat). Null when the Engine refused or could not be asked.</summary>
    Task<RemoteFixResultDto?> TrustRemoteProxyAsync(CancellationToken ct = default);
    Task<NetworkBandwidthStatusDto?> TestNetworkBandwidthAsync(CancellationToken ct = default);
    Task<PortAvailabilityResultDto?> CheckNetworkPortAsync(int port, CancellationToken ct = default);
    Task<PortAvailabilityResultDto?> ApplyNetworkPortAsync(int port, CancellationToken ct = default);
    Task<NetworkRuntimeStatusDto?> RenewNetworkRouterMappingAsync(CancellationToken ct = default);
    Task<NetworkSettingsDto?> ResetNetworkSettingsAsync(CancellationToken ct = default);
}
