using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// "Re-match seasons and episodes" (universal editor v3, D8 item 3): after a show is re-matched, the library's seasons and episodes of
/// that show are matched again against the new show by their own season and episode numbers. Whatever the new show does not have is sent
/// to Needs Review instead of being left on the old match. The target names the new TheTVDB show.
/// </summary>
public sealed class ShowChildRematchTarget
{
    [JsonPropertyName("series_id")]
    public string SeriesId { get; set; } = string.Empty;
}

/// <summary>What "Re-match seasons and episodes" would cover for a show and a chosen TheTVDB show (a read, so the switch can say how many).</summary>
public sealed class ShowChildRematchPreviewDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("series_id")]
    public string SeriesId { get; set; } = string.Empty;

    [JsonPropertyName("show_name")]
    public string ShowName { get; set; } = string.Empty;

    /// <summary>Episodes of the library show that have a season and episode number to match by.</summary>
    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }

    /// <summary>Of those, how many the chosen show has an episode for.</summary>
    [JsonPropertyName("matchable_count")]
    public int MatchableCount { get; set; }

    /// <summary>Of those, how many the chosen show does not have (they go to Needs Review).</summary>
    [JsonPropertyName("not_found_count")]
    public int NotFoundCount { get; set; }
}

public static class EditSessionChildRematchTarget
{
    public static string Serialize(ShowChildRematchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return JsonSerializer.Serialize(target);
    }

    public static bool TryParse(string? targetId, out ShowChildRematchTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return false;
        }

        try
        {
            target = JsonSerializer.Deserialize<ShowChildRematchTarget>(targetId);
        }
        catch (JsonException)
        {
            // Not JSON, or not a re-match target: the caller says the target could not be read.
            return false;
        }

        if (target is not null && target.SeriesId.Length is >= 1 and <= 12 && target.SeriesId.All(char.IsAsciiDigit))
        {
            return true;
        }

        target = null;
        return false;
    }
}
