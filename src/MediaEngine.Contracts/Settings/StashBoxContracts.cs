using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>
/// The Stash-box plugin's settings for an administrator. The account key is never part of any response: a server shows only
/// whether it is "connected".
/// </summary>
public sealed class StashBoxSettingsDto
{
    /// <summary>Servers in the order they are tried.</summary>
    [JsonPropertyName("servers")]
    public List<StashBoxServerDto> Servers { get; set; } = [];

    /// <summary>"Sends a fingerprint of your file" is allowed. Off until the administrator turns it on.</summary>
    [JsonPropertyName("fingerprintPermission")]
    public bool FingerprintPermission { get; set; }

    /// <summary>The permission in plain words, for the switch's label.</summary>
    [JsonPropertyName("fingerprintPermissionText")]
    public string FingerprintPermissionText { get; set; } = string.Empty;

    /// <summary>A plain statement of what the plugin contacts and sends.</summary>
    [JsonPropertyName("disclosure")]
    public string Disclosure { get; set; } = string.Empty;
}

public sealed class StashBoxServerDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>The first server (StashDB) cannot be removed.</summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    /// <summary>"connected", "not_connected" or "unknown". A server with no key, or a wrong key, is "not_connected".</summary>
    [JsonPropertyName("connection")]
    public string Connection { get; set; } = "not_connected";
}

public sealed class StashBoxAddServerRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The server's address. Must be https, except for a server on this machine.</summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>Optional: the account key. It is checked against the server and only then kept.</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}

public sealed class StashBoxKeyRequest
{
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class StashBoxOrderRequest
{
    [JsonPropertyName("serverIds")]
    public List<string> ServerIds { get; set; } = [];
}

public sealed class StashBoxFingerprintPermissionRequest
{
    [JsonPropertyName("allowed")]
    public bool Allowed { get; set; }
}

/// <summary>"Test connection": a saved server by id, or a typed address and key before anything is saved.</summary>
public sealed class StashBoxTestRequest
{
    [JsonPropertyName("serverId")]
    public string? ServerId { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}

public sealed class StashBoxTestResultDto
{
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    /// <summary>"connected", "not_connected" or "unavailable".</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "not_connected";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class StashBoxAddServerResultDto
{
    [JsonPropertyName("server")]
    public StashBoxServerDto Server { get; set; } = new();

    /// <summary>Present only when a key was supplied: what the server said about it. A key the server refused was not kept.</summary>
    [JsonPropertyName("test")]
    public StashBoxTestResultDto? Test { get; set; }
}
