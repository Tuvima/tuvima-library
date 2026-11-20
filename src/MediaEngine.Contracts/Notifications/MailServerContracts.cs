using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Notifications;

/// <summary>
/// The outgoing mail server the Library sends email through (notifications and password reset share it). The password is never
/// sent back: <see cref="HasPassword"/> only says whether one is saved.
/// </summary>
public sealed class MailServerSettingsDto
{
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 587;

    /// <summary>True (the default) when the connection is encrypted with STARTTLS.</summary>
    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; set; } = true;

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("from_address")]
    public string FromAddress { get; set; } = "";

    [JsonPropertyName("from_name")]
    public string FromName { get; set; } = "Library";

    [JsonPropertyName("has_password")]
    public bool HasPassword { get; set; }
}

/// <summary>
/// What the mail server form sends, for both Save and Send test. A null <see cref="Password"/> keeps the saved password; an empty
/// one clears it. The password is written once and never shown again.
/// </summary>
public sealed class MailServerRequest
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 587;

    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; set; } = true;

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("from_address")]
    public string FromAddress { get; set; } = "";

    [JsonPropertyName("from_name")]
    public string FromName { get; set; } = "Library";
}
