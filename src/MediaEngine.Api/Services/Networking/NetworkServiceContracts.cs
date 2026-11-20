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

public interface IRemoteCheckService
{
    /// <param name="skipRequested">The person chose "Not now" in the guided flow.</param>
    Task<RemoteCheckResultDto> CheckAsync(bool skipRequested, CancellationToken ct);
}

/// <summary>What looking up and connecting to the person's own address showed. Certificate fields are null when no secure connection was made.</summary>
public sealed record RemoteAddressProbeResult(
    bool NameResolves,
    IReadOnlyList<string> Addresses,
    bool Reachable,
    bool? CertificateTrusted,
    DateTimeOffset? CertificateExpiresAt);

/// <summary>
/// Looks up the person's own address and opens one secure connection to it. It never leaves the address the person chose
/// and sends no request, only the handshake.
/// </summary>
public interface IRemoteAddressProbe
{
    Task<RemoteAddressProbeResult> ProbeAsync(Uri address, CancellationToken ct);
}

/// <summary>
/// Who could and could not sign in from outside the home network today. An outside sign-in needs a password or passkey, so a
/// person without one stays home only.
/// </summary>
/// <param name="HomeOnlyProfileNames">Names of the people who have no password or passkey (administrators too, unless none of them has one: that is reported on its own).</param>
/// <param name="NoAdministratorHasPassword">True when no enabled server administrator has a password or passkey.</param>
public sealed record RemoteSignInReadiness(IReadOnlyList<string> HomeOnlyProfileNames, bool NoAdministratorHasPassword);

public interface IRemoteSignInReadinessReader
{
    Task<RemoteSignInReadiness> ReadAsync(CancellationToken ct);
}

/// <summary>The recent outside sign-in history: who tried to sign in from outside the home network, how, and whether they got in.</summary>
public interface IOutsideSignInHistory
{
    Task<IReadOnlyList<OutsideSignInDto>> GetRecentAsync(int limit, CancellationToken ct);
}

/// <summary>
/// The rule that outside access needs somebody to look after it: it may only be switched on while an enabled server
/// administrator has a password or passkey.
/// </summary>
public interface IOutsideAccessGate
{
    Task<bool> HasSecuredAdministratorAsync(CancellationToken ct);
}
