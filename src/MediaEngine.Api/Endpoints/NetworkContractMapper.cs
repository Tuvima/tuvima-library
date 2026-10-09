using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Endpoints;

internal static class NetworkContractMapper
{
    public static NetworkSettingsDto ToContract(NetworkSettings settings) => new()
    {
        SchemaVersion = settings.SchemaVersion,
        Local = new LocalNetworkSettingsDto
        {
            Port = settings.Local.Port,
            BindMode = settings.Local.BindMode,
            InterfaceId = settings.Local.InterfaceId,
            DiscoveryEnabled = settings.Local.DiscoveryEnabled,
            PreferredServerName = settings.Local.PreferredServerName,
            Ipv6Enabled = settings.Local.Ipv6Enabled,
        },
        Remote = new RemoteNetworkSettingsDto
        {
            Enabled = settings.Remote.Enabled,
            ConnectionMode = settings.Remote.ConnectionMode,
            AutomaticRouterConfiguration = settings.Remote.AutomaticRouterConfiguration,
            ExternalPort = settings.Remote.ExternalPort,
            TlsTerminationPort = settings.Remote.TlsTerminationPort,
            PublicHostname = settings.Remote.PublicHostname,
            TrustedProxies = [.. settings.Remote.TrustedProxies],
            TrustedProxyNetworks = [.. settings.Remote.TrustedProxyNetworks],
        },
        Streaming = new NetworkStreamingSettingsDto
        {
            RemoteQuality = settings.Streaming.RemoteQuality,
            UploadProtectionEnabled = settings.Streaming.UploadProtectionEnabled,
            ReservedUploadMbps = settings.Streaming.ReservedUploadMbps,
            ConcurrentRemoteStreams = settings.Streaming.ConcurrentRemoteStreams,
        },
        NativeAppAccess = new NativeAppAccessSettingsDto { Enabled = settings.NativeAppAccess.Enabled },
    };

    public static NetworkSettings ToStorage(NetworkSettingsDto dto) => new()
    {
        SchemaVersion = dto.SchemaVersion,
        Local = new LocalNetworkSettings
        {
            Port = dto.Local.Port,
            BindMode = Normalize(dto.Local.BindMode),
            InterfaceId = NormalizeOptional(dto.Local.InterfaceId),
            DiscoveryEnabled = dto.Local.DiscoveryEnabled,
            PreferredServerName = Normalize(dto.Local.PreferredServerName),
            Ipv6Enabled = dto.Local.Ipv6Enabled,
        },
        Remote = new RemoteNetworkSettings
        {
            Enabled = dto.Remote.Enabled,
            ConnectionMode = Normalize(dto.Remote.ConnectionMode),
            AutomaticRouterConfiguration = dto.Remote.AutomaticRouterConfiguration,
            ExternalPort = dto.Remote.ExternalPort,
            TlsTerminationPort = dto.Remote.TlsTerminationPort,
            PublicHostname = NormalizeOptional(dto.Remote.PublicHostname),
            TrustedProxies = dto.Remote.TrustedProxies
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            TrustedProxyNetworks = dto.Remote.TrustedProxyNetworks
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        },
        Streaming = new NetworkStreamingSettings
        {
            RemoteQuality = Normalize(dto.Streaming.RemoteQuality),
            UploadProtectionEnabled = dto.Streaming.UploadProtectionEnabled,
            ReservedUploadMbps = dto.Streaming.ReservedUploadMbps,
            ConcurrentRemoteStreams = Normalize(dto.Streaming.ConcurrentRemoteStreams),
        },
        NativeAppAccess = new NativeAppAccessSettings { Enabled = dto.NativeAppAccess?.Enabled ?? false },
    };

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
