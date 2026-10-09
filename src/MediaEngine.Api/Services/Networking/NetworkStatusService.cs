using System.Net;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Networking;

public sealed class NetworkStatusService
{
    private readonly IConfigurationLoader _configuration;
    private readonly INetworkEnvironmentService _environment;
    private readonly INetworkTopologyService _topology;
    private readonly NetworkRuntimeState _runtime;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public NetworkStatusService(
        IConfigurationLoader configuration,
        INetworkEnvironmentService environment,
        INetworkTopologyService topology,
        NetworkRuntimeState runtime,
        TimeProvider timeProvider)
    {
        _configuration = configuration;
        _environment = environment;
        _topology = topology;
        _runtime = runtime;
        _timeProvider = timeProvider;
    }

    public NetworkRuntimeStatusDto GetStatus()
    {
        var settings = _configuration.LoadNetwork();
        var addresses = _environment.GetUsableAddresses(settings.Local.Ipv6Enabled).ToList();
        var selected = SelectPreferredAddress(settings, addresses);
        var remoteTest = _runtime.LastRemoteTest;
        var mapping = _runtime.RouterMapping;
        var tailscale = _runtime.GetRemoteProvider("tailscale");
        var topology = _topology.GetSnapshot();
        var mappingState = EffectiveMappingState(mapping, _timeProvider.GetUtcNow());
        var remoteState = !settings.AllowsInternet
            ? "disabled"
            : settings.Remote.ConnectionMode == NetworkConnectionModes.Tailscale
                && tailscale is { State: RemoteProviderState.Connected, SecureHttps: true }
                    ? "available"
            : remoteTest?.Status == "passed"
                ? "available"
                : remoteTest?.Status == "failed" ? "needs-attention" : "unavailable";
        var connectionType = !settings.AllowsInternet
            ? "local-only"
            : settings.Remote.ConnectionMode switch
            {
                NetworkConnectionModes.Tailscale => "tailscale",
                NetworkConnectionModes.Custom => "custom",
                NetworkConnectionModes.DirectOnly => "direct",
                _ => "direct",
            };
        var secure = settings.Remote.ConnectionMode switch
        {
            NetworkConnectionModes.Custom when remoteTest?.Status == "passed"
                                                && Uri.TryCreate(settings.Remote.PublicHostname, UriKind.Absolute, out var uri)
                                                && uri.Scheme == Uri.UriSchemeHttps => "enabled",
            NetworkConnectionModes.DirectOnly when remoteTest?.Status == "passed" => "enabled",
            NetworkConnectionModes.Tailscale when tailscale is { State: RemoteProviderState.Connected, SecureHttps: true } => "enabled",
            _ when !settings.AllowsInternet => "not-configured",
            _ => "needs-attention",
        };
        var localState = addresses.Count == 0 ? "unavailable" : "healthy";
        var allGood = localState == "healthy" && (!settings.AllowsInternet || remoteState == "available");

        return new NetworkRuntimeStatusDto
        {
            LocalAccess = localState,
            RemoteAccess = remoteState,
            ConnectionType = connectionType,
            SecureConnection = secure,
            LocalAddresses = addresses,
            PreferredLocalAddress = selected is null ? null : NetworkDiagnosticsService.FormatAddress(selected.Address, settings.Local.Port),
            ExternalAddress = settings.Remote.ConnectionMode == NetworkConnectionModes.Tailscale
                ? tailscale?.PublicAddress
                : settings.Remote.PublicHostname,
            PublicIp = mapping?.PublicAddress,
            RouterConfiguration = settings.Remote.AutomaticRouterConfiguration
                ? "automatic"
                : settings.Remote.ConnectionMode == NetworkConnectionModes.DirectOnly ? "manual" : "not-configured",
            RouterMethod = mappingState == RouterMappingState.Active ? mapping?.Method : null,
            MappingStatus = mappingState switch
            {
                RouterMappingState.Active => "active",
                RouterMappingState.Expired => "expired",
                RouterMappingState.RouterRefused => "router-refused",
                RouterMappingState.UnsupportedTopology => "unsupported-topology",
                RouterMappingState.ProtocolUnavailable => "protocol-unavailable",
                RouterMappingState.Failed => "failed",
                _ => "not-attempted",
            },
            MappingCheckedAt = _runtime.RouterMappingCheckedAt,
            MappingExpiresAt = mapping?.ExpiresAt,
            MappingDetail = mapping?.Message ?? "Router mapping has not been attempted.",
            Topology = topology.Kind,
            TopologyDetail = topology.Detail,
            RouterGateway = topology.GatewayAddress,
            TailscaleState = tailscale?.State.ToString().ToLowerInvariant() ?? "not-installed",
            TailnetAddress = tailscale?.PublicAddress,
            TailscaleServeHttps = tailscale?.SecureHttps == true,
            LastTestedAt = remoteTest?.TestedAt ?? _runtime.LastLocalTest?.TestedAt,
            LastTestSucceeded = remoteTest is null ? null : remoteTest.Status == "passed",
            UptimeSeconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds),
            Ipv6Available = addresses.Any(address => address.AddressFamily == "ipv6"),
            CgnatSuspected = IsPrivateOrCarrierGradeAddress(mapping?.PublicAddress),
            Headline = allGood ? "Everything looks good" : BuildHeadline(localState, remoteState, settings.AllowsInternet),
            Guidance = allGood
                ? settings.AllowsInternet
                    ? "Your server is accessible locally and through its configured remote path."
                    : "Your server is accessible on your local network. Remote access is turned off."
                : BuildGuidance(localState, remoteState, settings.AllowsInternet),
            Bandwidth = CopyBandwidth(_runtime.Bandwidth, settings.Streaming.ReservedUploadMbps),
            HardwareAcceleration = _configuration.LoadTranscoding().HardwareAcceleration,
        };
    }

    private static NetworkAddressDto? SelectPreferredAddress(NetworkSettings settings, IReadOnlyList<NetworkAddressDto> addresses)
    {
        if (settings.Local.BindMode == NetworkBindModes.SpecificInterface)
        {
            return addresses.FirstOrDefault(address => string.Equals(address.InterfaceId, settings.Local.InterfaceId, StringComparison.OrdinalIgnoreCase));
        }

        return addresses.FirstOrDefault(address => address.AddressFamily == "ipv4") ?? addresses.FirstOrDefault();
    }

    internal static RouterMappingState EffectiveMappingState(RouterMappingResult? mapping, DateTimeOffset now) =>
        mapping is { State: RouterMappingState.Active, ExpiresAt: { } expiresAt } && expiresAt <= now
            ? RouterMappingState.Expired
            : mapping?.State ?? RouterMappingState.NotAttempted;

    private static string BuildHeadline(string local, string remote, bool remoteEnabled) =>
        local != "healthy" ? "Local access needs attention"
        : remoteEnabled && remote == "needs-attention" ? "Remote access needs attention"
        : remoteEnabled ? "Remote access has not been verified"
        : "Local access needs attention";

    private static string BuildGuidance(string local, string remote, bool remoteEnabled) =>
        local != "healthy" ? "Tuvima could not find a usable address on your local network."
        : remoteEnabled && remote == "needs-attention" ? "The latest remote connection test failed. Review the failed check for the next action."
        : remoteEnabled ? "Run Test Connection to check the configured remote path."
        : "Run a local connection test after the Dashboard finishes starting.";

    private static NetworkBandwidthStatusDto CopyBandwidth(NetworkBandwidthStatusDto current, double reserved) => new()
    {
        UploadCapacityMbps = current.UploadCapacityMbps,
        ReservedMbps = reserved,
        AvailableMbps = current.AvailableMbps,
        MeasuredAt = current.MeasuredAt,
        Status = current.Status,
    };

    private static bool IsPrivateOrCarrierGradeAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }
}
