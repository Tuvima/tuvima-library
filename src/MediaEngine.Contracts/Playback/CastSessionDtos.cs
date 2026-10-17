using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Playback;

/// <summary>The receivers a paired app may ask the Engine to prepare a cast session for.</summary>
public static class CastReceivers
{
    /// <summary>Google's Default Media Receiver (a Chromecast or Google TV with no custom receiver app).</summary>
    public const string GoogleCastDefault = "google-cast-default";
}

/// <summary>How ready a cast session is: the TV may start only at <see cref="Ready"/>.</summary>
public static class CastSessionStatuses
{
    public const string Ready = "ready";
    public const string Preparing = "preparing";
    public const string Unavailable = "unavailable";
}

/// <summary>What a paired app sends to start a cast session. Only <see cref="CastReceivers.GoogleCastDefault"/> is accepted.</summary>
public sealed record CastSessionRequestDto
{
    [JsonPropertyName("receiver")] public string Receiver { get; init; } = string.Empty;
}

/// <summary>
/// A cast session for one title: relative paths under <c>/cast/v1/{grant}/</c> that a home-network TV can load with no
/// sign-in. The grant is a short-lived secret that reads this one title only.
/// </summary>
public sealed record CastSessionDto
{
    [JsonPropertyName("assetId")] public Guid AssetId { get; init; }

    /// <summary>One of <see cref="CastSessionStatuses"/>. Nothing but <c>ready</c> carries a playable <see cref="MediaPath"/>.</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = CastSessionStatuses.Unavailable;

    /// <summary>One of <see cref="PlaybackDeliveryModes"/>: direct-stream with byte ranges, or hls from the prepared package.</summary>
    [JsonPropertyName("delivery")] public string Delivery { get; init; } = PlaybackDeliveryModes.DirectStream;

    /// <summary>Relative to the server address the app is paired with; always starts with <c>/cast/v1/</c>. Null unless ready.</summary>
    [JsonPropertyName("mediaPath")] public string? MediaPath { get; init; }

    /// <summary><c>video/mp4</c>, <c>video/webm</c> or <c>application/x-mpegURL</c>.</summary>
    [JsonPropertyName("contentType")] public string? ContentType { get; init; }

    [JsonPropertyName("posterPath")] public string? PosterPath { get; init; }
    [JsonPropertyName("textTracks")] public IReadOnlyList<CastTextTrackDto> TextTracks { get; init; } = [];
    [JsonPropertyName("durationSeconds")] public double? DurationSeconds { get; init; }
    [JsonPropertyName("resumeSeconds")] public double? ResumeSeconds { get; init; }

    /// <summary>When the grant stops working; the app asks for a new session to renew it.</summary>
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>A plain sentence for the person when the session is preparing or unavailable.</summary>
    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>One subtitle track a TV can show, as WebVTT behind the same grant.</summary>
public sealed record CastTextTrackDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("language")] public string Language { get; init; } = "und";
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("isForced")] public bool IsForced { get; init; }
    [JsonPropertyName("isDefault")] public bool IsDefault { get; init; }
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("contentType")] public string ContentType { get; init; } = "text/vtt";
}
