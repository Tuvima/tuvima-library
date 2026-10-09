using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Administrator-desired network configuration. Runtime observations such as the
/// current public address, active mapping, and latest connectivity result never
/// belong in this file.
/// </summary>
public sealed class NetworkSettings
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "3.0";

    [JsonPropertyName("local")]
    public LocalNetworkSettings Local { get; set; } = new();

    [JsonPropertyName("remote")]
    public RemoteNetworkSettings Remote { get; set; } = new();

    [JsonPropertyName("streaming")]
    public NetworkStreamingSettings Streaming { get; set; } = new();

    [JsonPropertyName("native_app_access")]
    public NativeAppAccessSettings NativeAppAccess { get; set; } = new();
}

/// <summary>
/// Whether phone, TV and tablet apps may reach this Library through the Dashboard. Off by default, and it can
/// only be on while remote access (and therefore a verified secure HTTPS path) is on.
/// </summary>
public sealed class NativeAppAccessSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class LocalNetworkSettings
{
    [JsonPropertyName("port")]
    public int Port { get; set; } = 5016;

    [JsonPropertyName("bind_mode")]
    public string BindMode { get; set; } = NetworkBindModes.Automatic;

    [JsonPropertyName("interface_id")]
    public string? InterfaceId { get; set; }

    [JsonPropertyName("discovery_enabled")]
    public bool DiscoveryEnabled { get; set; } = true;

    [JsonPropertyName("preferred_server_name")]
    public string PreferredServerName { get; set; } = "tuvima";

    [JsonPropertyName("ipv6_enabled")]
    public bool Ipv6Enabled { get; set; } = true;

    /// <summary>
    /// Extra host names (no scheme, no port) the Dashboard answers to, in addition to IP addresses,
    /// localhost, this machine's name and the configured server/public names.
    /// </summary>
    [JsonPropertyName("allowed_hostnames")]
    public List<string> AllowedHostnames { get; set; } = [];
}

public sealed class RemoteNetworkSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("connection_mode")]
    public string ConnectionMode { get; set; } = NetworkConnectionModes.LocalOnly;

    [JsonPropertyName("automatic_router_configuration")]
    public bool AutomaticRouterConfiguration { get; set; }

    [JsonPropertyName("external_port")]
    public int? ExternalPort { get; set; }

    [JsonPropertyName("tls_termination_port")]
    public int? TlsTerminationPort { get; set; }

    [JsonPropertyName("public_hostname")]
    public string? PublicHostname { get; set; }

    /// <summary>
    /// Optional extra port for a same-machine reverse proxy (Caddy, Tailscale Serve). Requests on it are always
    /// treated as remote and are the only ones whose forwarded headers are honoured. Null = off.
    /// </summary>
    [JsonPropertyName("proxy_port")]
    public int? ProxyPort { get; set; }

    [JsonPropertyName("trusted_proxies")]
    public List<string> TrustedProxies { get; set; } = [];

    [JsonPropertyName("trusted_proxy_networks")]
    public List<string> TrustedProxyNetworks { get; set; } = [];
}

public sealed class NetworkStreamingSettings
{
    [JsonPropertyName("remote_quality")]
    public string RemoteQuality { get; set; } = RemoteStreamingQualities.Automatic;

    [JsonPropertyName("upload_protection_enabled")]
    public bool UploadProtectionEnabled { get; set; } = true;

    [JsonPropertyName("reserved_upload_mbps")]
    public int ReservedUploadMbps { get; set; } = 5;

    [JsonPropertyName("concurrent_remote_streams")]
    public string ConcurrentRemoteStreams { get; set; } = RemoteStreamConcurrencyModes.Automatic;
}

public static class NetworkBindModes
{
    public const string Automatic = "automatic";
    public const string SpecificInterface = "specific-interface";
}

public static class NetworkConnectionModes
{
    public const string LocalOnly = "local-only";
    public const string Tailscale = "tailscale";
    public const string DirectOnly = "direct-only";
    public const string Custom = "custom";
}

public static class RemoteStreamingQualities
{
    public const string Automatic = "automatic";
    public const string Original = "original";
    public const string Hd1080 = "1080p";
    public const string Hd720 = "720p";
    public const string DataSaver = "data-saver";
}

public static class RemoteStreamConcurrencyModes
{
    public const string Automatic = "automatic";
}
