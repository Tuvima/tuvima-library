using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Setup;

/// <summary>
/// How this server introduces itself: a friendly name people read, and the short network name
/// (answered as <c>host_name.local</c>) that phones and TVs can use instead of an IP address.
/// </summary>
public sealed record SetupServerIdentityDto(
    [property: JsonPropertyName("server_name")] string ServerName,
    [property: JsonPropertyName("host_name")] string HostName);
