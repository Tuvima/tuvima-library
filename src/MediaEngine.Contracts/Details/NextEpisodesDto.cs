using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Details;

/// <summary>The owned episodes that follow a given episode file in a show's watch order.</summary>
public sealed class NextEpisodesDto
{
    [JsonPropertyName("show_id")] public Guid ShowId { get; init; }

    /// <summary>The episode file the list continues after.</summary>
    [JsonPropertyName("after_asset_id")] public Guid AfterAssetId { get; init; }

    [JsonPropertyName("episodes")] public IReadOnlyList<NextEpisodeDto> Episodes { get; init; } = [];
}

/// <summary>One owned episode, as the file a player opens for it.</summary>
public sealed class NextEpisodeDto
{
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }
    [JsonPropertyName("asset_id")] public Guid AssetId { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("season_number")] public int? SeasonNumber { get; init; }
    [JsonPropertyName("episode_number")] public int? EpisodeNumber { get; init; }
    [JsonPropertyName("still_url")] public string? StillUrl { get; init; }
}
