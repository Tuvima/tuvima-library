using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// What the Match tool needs to start from for one item (universal editor v3, WP4 and D4): the file's own name as the Engine reads it
/// (the show, season and episode a TV file name carries) and, when a provider can say, the Engine's best guess. The Match search is
/// filled from this so a failed item starts where the file name points instead of where a person has to guess. A read; nothing changes.
/// </summary>
public sealed class EditorMatchContextDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    /// <summary>The editor's media type: TV, Movies, Music, Books and so on.</summary>
    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    /// <summary>The file's name without its folder, as it sits on disk; empty when the item has no file.</summary>
    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>The title the file name carries once the show, season, episode and year markers are taken out.</summary>
    [JsonPropertyName("parsed_title")]
    public string? ParsedTitle { get; set; }

    /// <summary>For TV, the show the file name or its folder names.</summary>
    [JsonPropertyName("parsed_show")]
    public string? ParsedShow { get; set; }

    [JsonPropertyName("parsed_season")]
    public int? ParsedSeason { get; set; }

    [JsonPropertyName("parsed_episode")]
    public int? ParsedEpisode { get; set; }

    [JsonPropertyName("parsed_year")]
    public string? ParsedYear { get; set; }

    /// <summary>The provider's top answer for the parsed show; null when no provider is connected or nothing fits.</summary>
    [JsonPropertyName("best_guess")]
    public EditorMatchBestGuessDto? BestGuess { get; set; }
}

/// <summary>The Engine's best guess at what a file is, shown with a "Best guess" tag on the top result.</summary>
public sealed class EditorMatchBestGuessDto
{
    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>The provider's id for the guessed record (for TV, the TheTVDB series id).</summary>
    [JsonPropertyName("provider_item_id")]
    public string ProviderItemId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }
}

/// <summary>
/// "Match the other episodes in this season by number" (universal editor v3, D4): once an episode is matched into a show, the other
/// episodes in the same folder season are matched to the same season of that show by their own episode numbers.
/// </summary>
public sealed class EpisodeSiblingMatchTarget
{
    /// <summary>The TheTVDB series the chosen episode belongs to.</summary>
    [JsonPropertyName("series_id")]
    public string SeriesId { get; set; } = string.Empty;

    /// <summary>The season of that show the other episodes are matched into (the chosen episode's season).</summary>
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }
}

/// <summary>What the by-number match would do, row by row, so the switch can say how many episodes it covers. A read; nothing changes.</summary>
public sealed class EpisodeSiblingMatchPreviewDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("series_id")]
    public string SeriesId { get; set; } = string.Empty;

    [JsonPropertyName("show_name")]
    public string ShowName { get; set; } = string.Empty;

    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    /// <summary>How many other episodes in the folder season would be matched (rows that found their number in the chosen season).</summary>
    [JsonPropertyName("matchable_count")]
    public int MatchableCount { get; set; }

    [JsonPropertyName("rows")]
    public List<EpisodeSiblingMatchRowDto> Rows { get; set; } = [];
}

/// <summary>One other episode in the folder season and the episode of the chosen show it would take.</summary>
public sealed class EpisodeSiblingMatchRowDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("source_file_name")]
    public string SourceFileName { get; set; } = string.Empty;

    [JsonPropertyName("episode_number")]
    public int EpisodeNumber { get; set; }

    /// <summary>The TheTVDB episode with the same number; null when the chosen season has no such number.</summary>
    [JsonPropertyName("target_episode_id")]
    public string? TargetEpisodeId { get; set; }

    [JsonPropertyName("target_title")]
    public string? TargetTitle { get; set; }

    [JsonPropertyName("found")]
    public bool Found { get; set; }
}

/// <summary>How a <see cref="EditSessionChangeKinds.SiblingMatch"/> change names what to do: the target as JSON in the change's target id.</summary>
public static class EditSessionSiblingMatchTarget
{
    public static string Serialize(EpisodeSiblingMatchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return JsonSerializer.Serialize(target);
    }

    /// <summary>Reads the target back; false when the text is not one the Dashboard staged.</summary>
    public static bool TryParse(string? targetId, out EpisodeSiblingMatchTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return false;
        }

        try
        {
            target = JsonSerializer.Deserialize<EpisodeSiblingMatchTarget>(targetId);
        }
        catch (JsonException)
        {
            // Not JSON, or not a sibling match: the caller says the target could not be read.
            return false;
        }

        if (target is not null
            && target.SeriesId.Length is >= 1 and <= 12
            && target.SeriesId.All(char.IsAsciiDigit)
            && target.SeasonNumber is >= 0 and <= 999)
        {
            return true;
        }

        target = null;
        return false;
    }
}
