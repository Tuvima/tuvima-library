using System.Text.Json.Serialization;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Contracts.Authentication;

public static class ClientApiScopes
{
    public static readonly string LibraryRead = ApplicationPermissionIds.LibraryRead.Value;
    public static readonly string ArtworkRead = ApplicationPermissionIds.ArtworkRead.Value;
    public static readonly string ProgressRead = ApplicationPermissionIds.ProgressRead.Value;
    public static readonly string ProgressWrite = ApplicationPermissionIds.ProgressWrite.Value;
    public static readonly string QueueRead = ApplicationPermissionIds.QueueRead.Value;
    public static readonly string QueueWrite = ApplicationPermissionIds.QueueWrite.Value;
    public static readonly string PlaybackRead = ApplicationPermissionIds.PlaybackRead.Value;
    public static readonly string PlaybackWrite = ApplicationPermissionIds.PlaybackWrite.Value;
    public static readonly string DownloadsRead = ApplicationPermissionIds.DownloadsRead.Value;
    public static readonly string DownloadsWrite = ApplicationPermissionIds.DownloadsWrite.Value;

    public static readonly IReadOnlyList<string> Consumer = Array.AsReadOnly(
        ApplicationPermissionIds.NativeClient.Select(id => id.Value).ToArray());

    public static readonly IReadOnlyList<string> Default = Array.AsReadOnly<string>(
    [
        LibraryRead,
        ArtworkRead,
        ProgressRead,
        ProgressWrite,
        QueueRead,
        QueueWrite,
        PlaybackRead,
        PlaybackWrite,
    ]);
}

public sealed class DeviceAuthorizationRequest
{
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("client_name")] public string ClientName { get; init; } = string.Empty;
    [JsonPropertyName("client_version")] public string ClientVersion { get; init; } = string.Empty;
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("device_class")] public string DeviceClass { get; init; } = "television";
    [JsonPropertyName("scope")] public string Scope { get; init; } = string.Empty;
    [JsonPropertyName("capabilities")] public ClientCapabilitiesDto Capabilities { get; init; } = new();
}

public sealed class DeviceAuthorizationResponse
{
    [JsonPropertyName("device_code")] public string DeviceCode { get; init; } = string.Empty;
    [JsonPropertyName("user_code")] public string UserCode { get; init; } = string.Empty;
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; init; } = string.Empty;
    [JsonPropertyName("verification_uri_complete")] public string VerificationUriComplete { get; init; } = string.Empty;
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("interval")] public int Interval { get; init; }
}

public sealed class OAuthTokenRequest
{
    [JsonPropertyName("grant_type")] public string GrantType { get; init; } = string.Empty;
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("device_code")] public string? DeviceCode { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
}

public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "Bearer";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = string.Empty;
    [JsonPropertyName("scope")] public string Scope { get; init; } = string.Empty;
    [JsonPropertyName("device_id")] public Guid DeviceId { get; init; }
    [JsonPropertyName("profile_id")] public Guid ProfileId { get; init; }
}

public sealed class OAuthErrorResponse
{
    [JsonPropertyName("error")] public string Error { get; init; } = string.Empty;
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
    [JsonPropertyName("interval")] public int? Interval { get; init; }
}

public sealed class PairingReviewResponse
{
    [JsonPropertyName("request_id")] public Guid RequestId { get; init; }
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("client_name")] public string ClientName { get; init; } = string.Empty;
    [JsonPropertyName("client_version")] public string ClientVersion { get; init; } = string.Empty;
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("device_class")] public string DeviceClass { get; init; } = string.Empty;
    [JsonPropertyName("requested_scopes")] public IReadOnlyList<string> RequestedScopes { get; init; } = [];
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class PairingDecisionRequest
{
    [JsonPropertyName("user_code")] public string UserCode { get; init; } = string.Empty;
    [JsonPropertyName("approved")] public bool Approved { get; init; }
    [JsonPropertyName("scopes")] public IReadOnlyList<string> Scopes { get; init; } = [];
}

public sealed class ClientDeviceDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("profile_id")] public Guid ProfileId { get; init; }
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("device_class")] public string DeviceClass { get; init; } = string.Empty;
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("client_name")] public string ClientName { get; init; } = string.Empty;
    [JsonPropertyName("client_version")] public string ClientVersion { get; init; } = string.Empty;
    [JsonPropertyName("scopes")] public IReadOnlyList<string> Scopes { get; init; } = [];
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("last_seen_at")] public DateTimeOffset LastSeenAt { get; init; }
    [JsonPropertyName("revoked_at")] public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>The person this phone backs its photos up to; null until one is chosen.</summary>
    [JsonPropertyName("backup_profile_id")] public Guid? BackupProfileId { get; init; }
}

/// <summary>A phone choosing whose photos it backs up. The person's PIN is needed when they have one.</summary>
public sealed class SetDeviceBackupProfileRequest
{
    [JsonPropertyName("profile_id")] public Guid ProfileId { get; init; }
    [JsonPropertyName("pin")] public string? Pin { get; init; }
}

public sealed class ClientCapabilitiesDto
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("containers")] public IReadOnlyList<string> Containers { get; init; } = [];
    [JsonPropertyName("video_codecs")] public IReadOnlyList<string> VideoCodecs { get; init; } = [];
    [JsonPropertyName("audio_codecs")] public IReadOnlyList<string> AudioCodecs { get; init; } = [];
    [JsonPropertyName("subtitle_formats")] public IReadOnlyList<string> SubtitleFormats { get; init; } = [];
    [JsonPropertyName("protocols")] public IReadOnlyList<string> Protocols { get; init; } = ["https", "http-range"];
    [JsonPropertyName("max_width")] public int? MaxWidth { get; init; }
    [JsonPropertyName("max_height")] public int? MaxHeight { get; init; }
    [JsonPropertyName("max_bitrate_kbps")] public int? MaxBitrateKbps { get; init; }
    [JsonPropertyName("max_audio_channels")] public int? MaxAudioChannels { get; init; }
    [JsonPropertyName("supports_hdr")] public bool SupportsHdr { get; init; }
    [JsonPropertyName("supports_playback_speed")] public bool SupportsPlaybackSpeed { get; init; }
    [JsonPropertyName("supports_offline_downloads")] public bool SupportsOfflineDownloads { get; init; }
}

public sealed class TuvimaDiscoveryResponse
{
    [JsonPropertyName("product")] public string Product { get; init; } = "Tuvima Library";
    [JsonPropertyName("server_id")] public string ServerId { get; init; } = string.Empty;
    [JsonPropertyName("server_name")] public string ServerName { get; init; } = string.Empty;
    [JsonPropertyName("api_base_url")] public string ApiBaseUrl { get; init; } = string.Empty;
    [JsonPropertyName("supported_api_versions")] public IReadOnlyList<string> SupportedApiVersions { get; init; } = ["1"];
    [JsonPropertyName("device_authorization_endpoint")] public string DeviceAuthorizationEndpoint { get; init; } = "/api/v1/oauth/device_authorization";
    [JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; init; } = "/api/v1/oauth/token";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; init; } = "/pair";
    [JsonPropertyName("capabilities")] public IReadOnlyList<string> Capabilities { get; init; } = ["browse", "search", "details", "artwork", "progress", "queues", "downloads", "playback"];
}
