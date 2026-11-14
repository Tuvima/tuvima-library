using System.Text.Json.Serialization;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Contracts.Playback;

/// <summary>
/// What the Dashboard's browser measured about itself, sent once per session (playback-path spec D12). A browser is not a paired
/// device, so the Engine keeps this in memory against the signed-in profile and the browser session key, and uses it in place of
/// the fixed "web" list when it builds that browser's playback manifests.
/// </summary>
public sealed class BrowserCapabilitiesReportDto
{
    /// <summary>A random key for this browser session; the manifest request carries the same key.</summary>
    [JsonPropertyName("browser")] public string Browser { get; init; } = string.Empty;

    [JsonPropertyName("capabilities")] public ClientCapabilitiesDto Capabilities { get; init; } = new();
}

/// <summary>
/// A player telling the Engine it started a title in a rung and could not play it (playback-path spec D13). The Engine looks the
/// file's codec and container up itself; the player only says which rung it was playing and, optionally, why.
/// </summary>
public sealed class PlaybackFailureReportDto
{
    /// <summary>The rung the player was playing, from the manifest's playback path (one of the playback rungs).</summary>
    [JsonPropertyName("rung")] public string Rung { get; init; } = string.Empty;

    /// <summary>The player's own words for what went wrong (short; may be empty).</summary>
    [JsonPropertyName("detail")] public string? Detail { get; init; }

    /// <summary>The browser session key, for the Dashboard's browser; paired devices leave it empty because the Engine knows the device.</summary>
    [JsonPropertyName("browser")] public string? Browser { get; init; }
}
