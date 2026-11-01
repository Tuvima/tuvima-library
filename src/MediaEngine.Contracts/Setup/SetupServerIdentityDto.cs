using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Setup;

/// <summary>
/// How this server introduces itself: a friendly name people read, and the short network name
/// (answered as <c>host_name.local</c>) that phones and TVs can use instead of an IP address.
/// <c>HostNameDerived</c> is true while the network name just follows the friendly name: the Engine decides it on read
/// (nothing was set by hand) and, when a save says it is still true, derives the network name itself.
/// </summary>
public sealed record SetupServerIdentityDto(
    [property: JsonPropertyName("server_name")] string ServerName,
    [property: JsonPropertyName("host_name")] string HostName,
    [property: JsonPropertyName("host_name_derived")] bool HostNameDerived = true);
