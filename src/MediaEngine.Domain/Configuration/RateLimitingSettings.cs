using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Rate limiting policy parameters for the Engine API.
/// Loaded from <c>config/core.json</c> (<c>rate_limiting</c> section).
/// </summary>
public sealed class RateLimitingSettings
{
    /// <summary>API key generation: strict limit to prevent brute-force.</summary>
    [JsonPropertyName("key_generation")]
    public RateLimitPolicy KeyGeneration { get; set; } = new() { PermitLimit = 5, WindowMinutes = 1 };

    /// <summary>File streaming: higher limit for media playback.</summary>
    [JsonPropertyName("streaming")]
    public RateLimitPolicy Streaming { get; set; } = new() { PermitLimit = 100, WindowMinutes = 1 };

    /// <summary>
    /// Book and comic file reads (<c>/read/{id}/file</c>). A browser reader fetches many small byte ranges
    /// per book, and the Dashboard forwards every person's reads from one address, so this is counted per
    /// signed-in session (per address for any other caller) and is far higher than <see cref="Streaming"/>.
    /// </summary>
    [JsonPropertyName("reader_files")]
    public RateLimitPolicy ReaderFiles { get; set; } = new() { PermitLimit = 600, WindowMinutes = 1 };

    /// <summary>
    /// General API: default limit for all other endpoints. Dashboard routes fan
    /// out into many parallel reads, so this protects against floods without
    /// throttling ordinary navigation and artwork-rich page loads.
    /// </summary>
    [JsonPropertyName("general")]
    public RateLimitPolicy General { get; set; } = new() { PermitLimit = 600, WindowMinutes = 1 };
}

/// <summary>A single rate limit policy with a permit count and time window.</summary>
public sealed class RateLimitPolicy
{
    [JsonPropertyName("permit_limit")]
    public int PermitLimit { get; set; }

    [JsonPropertyName("window_minutes")]
    public int WindowMinutes { get; set; } = 1;
}
