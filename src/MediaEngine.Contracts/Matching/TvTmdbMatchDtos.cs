using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Matching;

public sealed class TvTmdbSeasonListDto
{
    [JsonPropertyName("show_id")] public string ShowId { get; set; } = string.Empty;
    [JsonPropertyName("show_name")] public string ShowName { get; set; } = string.Empty;
    [JsonPropertyName("seasons")] public List<TvTmdbSeasonDto> Seasons { get; set; } = [];
}

public sealed class TvTmdbSeasonDto
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("episode_count")] public int EpisodeCount { get; set; }
    [JsonPropertyName("poster_url")] public string? PosterUrl { get; set; }
}

public sealed class TvTmdbEpisodeListDto
{
    [JsonPropertyName("provider_id")] public string ProviderId { get; set; } = string.Empty;
    [JsonPropertyName("show_id")] public string ShowId { get; set; } = string.Empty;
    [JsonPropertyName("show_name")] public string ShowName { get; set; } = string.Empty;
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("episodes")] public List<TvTmdbEpisodeDto> Episodes { get; set; } = [];
}

public sealed class TvTmdbEpisodeDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("air_date")] public string? AirDate { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("runtime_minutes")] public int? RuntimeMinutes { get; set; }
    [JsonPropertyName("still_url")] public string? StillUrl { get; set; }
}

public sealed class TvTmdbSeasonReviewDto
{
    [JsonPropertyName("show_id")] public string ShowId { get; set; } = string.Empty;
    [JsonPropertyName("show_name")] public string ShowName { get; set; } = string.Empty;
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("provider_id")] public string ProviderId { get; set; } = string.Empty;
    [JsonPropertyName("rows")] public List<TvTmdbSeasonReviewRowDto> Rows { get; set; } = [];
}

public sealed class TvTmdbSeasonReviewRowDto
{
    [JsonPropertyName("asset_id")] public Guid AssetId { get; set; }
    [JsonPropertyName("local_title")] public string LocalTitle { get; set; } = string.Empty;
    [JsonPropertyName("local_episode_number")] public int? LocalEpisodeNumber { get; set; }
    [JsonPropertyName("episode")] public TvTmdbEpisodeDto? Episode { get; set; }
    [JsonPropertyName("can_apply")] public bool CanApply { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
}
