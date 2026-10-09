using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

public sealed class NetworkSettingsDto
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = "3.0";
    [JsonPropertyName("who_can_connect")] public string WhoCanConnect { get; set; } = "home_network";
    [JsonPropertyName("local")] public LocalNetworkSettingsDto Local { get; set; } = new();
    [JsonPropertyName("remote")] public RemoteNetworkSettingsDto Remote { get; set; } = new();
    [JsonPropertyName("streaming")] public NetworkStreamingSettingsDto Streaming { get; set; } = new();
    [JsonPropertyName("native_app_access")] public NativeAppAccessSettingsDto NativeAppAccess { get; set; } = new();
}

public sealed class NativeAppAccessSettingsDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

public sealed class LocalNetworkSettingsDto
{
    [JsonPropertyName("port")] public int Port { get; set; } = 5016;
    [JsonPropertyName("bind_mode")] public string BindMode { get; set; } = "automatic";
    [JsonPropertyName("interface_id")] public string? InterfaceId { get; set; }
    [JsonPropertyName("discovery_enabled")] public bool DiscoveryEnabled { get; set; } = true;
    [JsonPropertyName("preferred_server_name")] public string PreferredServerName { get; set; } = "tuvima";
    [JsonPropertyName("ipv6_enabled")] public bool Ipv6Enabled { get; set; } = true;
    [JsonPropertyName("allowed_hostnames")] public List<string> AllowedHostnames { get; set; } = [];
}

public sealed class RemoteNetworkSettingsDto
{
    [JsonPropertyName("connection_mode")] public string ConnectionMode { get; set; } = "local-only";
    [JsonPropertyName("automatic_router_configuration")] public bool AutomaticRouterConfiguration { get; set; }
    [JsonPropertyName("external_port")] public int? ExternalPort { get; set; }
    [JsonPropertyName("tls_termination_port")] public int? TlsTerminationPort { get; set; }
    [JsonPropertyName("public_hostname")] public string? PublicHostname { get; set; }
    [JsonPropertyName("proxy_port")] public int? ProxyPort { get; set; }
    [JsonPropertyName("trusted_proxies")] public List<string> TrustedProxies { get; set; } = [];
    [JsonPropertyName("trusted_proxy_networks")] public List<string> TrustedProxyNetworks { get; set; } = [];
}

public sealed class NetworkStreamingSettingsDto
{
    [JsonPropertyName("remote_quality")] public string RemoteQuality { get; set; } = "automatic";
    [JsonPropertyName("upload_protection_enabled")] public bool UploadProtectionEnabled { get; set; } = true;
    [JsonPropertyName("reserved_upload_mbps")] public int ReservedUploadMbps { get; set; } = 5;
    [JsonPropertyName("concurrent_remote_streams")] public string ConcurrentRemoteStreams { get; set; } = "automatic";
}

public sealed class NetworkRuntimeStatusDto
{
    [JsonPropertyName("local_access")] public string LocalAccess { get; set; } = "unavailable";
    [JsonPropertyName("remote_access")] public string RemoteAccess { get; set; } = "disabled";
    [JsonPropertyName("connection_type")] public string ConnectionType { get; set; } = "local-only";
    [JsonPropertyName("secure_connection")] public string SecureConnection { get; set; } = "not-configured";
    [JsonPropertyName("local_addresses")] public List<NetworkAddressDto> LocalAddresses { get; set; } = [];
    [JsonPropertyName("preferred_local_address")] public string? PreferredLocalAddress { get; set; }
    [JsonPropertyName("external_address")] public string? ExternalAddress { get; set; }
    [JsonPropertyName("public_ip")]
    public string? PublicIp { get; set; }
    [JsonPropertyName("router_configuration")] public string RouterConfiguration { get; set; } = "not-configured";
    [JsonPropertyName("router_method")] public string? RouterMethod { get; set; }
    [JsonPropertyName("mapping_status")] public string MappingStatus { get; set; } = "inactive";
    [JsonPropertyName("mapping_checked_at")] public DateTimeOffset? MappingCheckedAt { get; set; }
    [JsonPropertyName("mapping_expires_at")] public DateTimeOffset? MappingExpiresAt { get; set; }
    [JsonPropertyName("mapping_detail")] public string MappingDetail { get; set; } = string.Empty;
    [JsonPropertyName("topology")] public string Topology { get; set; } = "unknown";
    [JsonPropertyName("topology_detail")] public string TopologyDetail { get; set; } = string.Empty;
    [JsonPropertyName("router_gateway")] public string? RouterGateway { get; set; }
    [JsonPropertyName("tailscale_state")] public string TailscaleState { get; set; } = "not-installed";
    [JsonPropertyName("tailnet_address")] public string? TailnetAddress { get; set; }
    [JsonPropertyName("tailscale_serve_https")] public bool TailscaleServeHttps { get; set; }
    [JsonPropertyName("last_tested_at")] public DateTimeOffset? LastTestedAt { get; set; }
    [JsonPropertyName("last_test_succeeded")] public bool? LastTestSucceeded { get; set; }
    [JsonPropertyName("uptime_seconds")] public long UptimeSeconds { get; set; }
    [JsonPropertyName("ipv6_available")] public bool Ipv6Available { get; set; }
    [JsonPropertyName("cgnat_suspected")] public bool CgnatSuspected { get; set; }
    [JsonPropertyName("headline")] public string Headline { get; set; } = string.Empty;
    [JsonPropertyName("guidance")] public string Guidance { get; set; } = string.Empty;
    [JsonPropertyName("bandwidth")] public NetworkBandwidthStatusDto Bandwidth { get; set; } = new();
    [JsonPropertyName("hardware_acceleration")] public string HardwareAcceleration { get; set; } = "unknown";
}

public sealed class NetworkAddressDto
{
    [JsonPropertyName("interface_id")] public string InterfaceId { get; set; } = string.Empty;
    [JsonPropertyName("interface_name")] public string InterfaceName { get; set; } = string.Empty;
    [JsonPropertyName("address")] public string Address { get; set; } = string.Empty;
    [JsonPropertyName("address_family")] public string AddressFamily { get; set; } = "ipv4";
}

public sealed class NetworkBandwidthStatusDto
{
    [JsonPropertyName("upload_capacity_mbps")] public double? UploadCapacityMbps { get; set; }
    [JsonPropertyName("reserved_mbps")] public double ReservedMbps { get; set; }
    [JsonPropertyName("available_mbps")] public double? AvailableMbps { get; set; }
    [JsonPropertyName("measured_at")] public DateTimeOffset? MeasuredAt { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "not-tested";
}

public sealed class NetworkTestResultDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("headline")] public string Headline { get; set; } = string.Empty;
    [JsonPropertyName("detail")] public string Detail { get; set; } = string.Empty;
    [JsonPropertyName("tested_at")] public DateTimeOffset TestedAt { get; set; }
    [JsonPropertyName("checks")] public List<NetworkTestCheckDto> Checks { get; set; } = [];
}

public sealed class NetworkTestCheckDto
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("detail")] public string Detail { get; set; } = string.Empty;
}

public sealed class RemoteAccessReadinessDto
{
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("checks")] public List<NetworkTestCheckDto> Checks { get; set; } = [];
}

public sealed class PortAvailabilityRequest
{
    [JsonPropertyName("port")] public int Port { get; set; }
}

public sealed class PortAvailabilityResultDto
{
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("restart_required")] public bool RestartRequired { get; set; } = true;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}
