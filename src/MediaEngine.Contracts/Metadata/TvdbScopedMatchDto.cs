namespace MediaEngine.Contracts.Metadata;

public sealed record TvdbMatchCandidateDto(
    string Id,
    string SeriesId,
    string Title,
    int SeasonNumber,
    int? EpisodeNumber,
    string? Date,
    string? Description,
    string? ImageUrl,
    string SeasonType,
    string AttributionUrl);

public sealed record TvdbScopedMatchCandidatesDto(
    string ScopeId,
    string ShowName,
    string SeriesId,
    string CurrentRevision,
    int? OwnedSeasonNumber,
    int? OwnedEpisodeNumber,
    IReadOnlyList<int> AvailableSeasons,
    IReadOnlyList<TvdbMatchCandidateDto> Candidates);

public sealed record ApplyTvdbScopedMatchDto(string CandidateId, string SeriesId, string ExpectedRevision);

public sealed record TvdbScopedMatchResultDto(
    string ScopeId,
    string CandidateId,
    string NewRevision,
    string Message);
