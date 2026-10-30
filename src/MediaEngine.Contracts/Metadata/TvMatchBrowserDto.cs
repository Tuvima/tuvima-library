namespace MediaEngine.Contracts.Metadata;

/// <summary>One show in the Match tool's Show column (universal editor v2, D4).</summary>
public sealed record TvMatchBrowserShowDto(
    string SeriesId,
    string Name,
    string? Year,
    string? Overview,
    string? ImageUrl,
    string AttributionUrl,
    bool IsCurrent);

public sealed record TvMatchBrowserShowsDto(
    string Query,
    IReadOnlyList<TvMatchBrowserShowDto> Shows);

/// <summary>One season of a show in the Season column. <see cref="SeasonId"/> is the TheTVDB season record.</summary>
public sealed record TvMatchBrowserSeasonDto(
    string SeasonId,
    string SeriesId,
    int SeasonNumber,
    string Title,
    string? ImageUrl,
    bool IsCurrent);

public sealed record TvMatchBrowserSeasonsDto(
    string SeriesId,
    string ShowName,
    IReadOnlyList<TvMatchBrowserSeasonDto> Seasons);

/// <summary>
/// One real episode record in the Episode column. It carries the full identity a save needs (show, season, episode numbers and
/// ids), so choosing it can never produce a show-only match on an episode. <see cref="HasFile"/> says a library file already
/// holds this slot; <see cref="IsCurrent"/> says it is the item being edited.
/// </summary>
public sealed record TvMatchBrowserEpisodeDto(
    string EpisodeId,
    string SeriesId,
    string ShowName,
    string SeasonId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    string? Aired,
    string? Overview,
    string? ImageUrl,
    string AttributionUrl,
    bool IsCurrent,
    bool HasFile,
    Guid? FileEntityId);

public sealed record TvMatchBrowserEpisodesDto(
    string SeriesId,
    string ShowName,
    string SeasonId,
    int SeasonNumber,
    IReadOnlyList<TvMatchBrowserEpisodeDto> Episodes);
