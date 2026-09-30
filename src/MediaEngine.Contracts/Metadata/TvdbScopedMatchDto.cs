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
    IReadOnlyList<TvdbMatchCandidateDto> Candidates,
    string SeasonType = "default",
    IReadOnlyList<string>? AvailableSeasonTypes = null,
    bool HasConfirmedSeasonMatch = false,
    int? ConfirmedSeasonNumber = null,
    string ShowSeasonType = "default");

public sealed record ApplyTvdbScopedMatchDto(
    string CandidateId,
    string SeriesId,
    string ExpectedRevision,
    string? SeasonType = null);

public sealed record TvdbScopedMatchResultDto(
    string ScopeId,
    string CandidateId,
    string NewRevision,
    string Message);

public sealed record TvdbShowOrderImpactDto(
    Guid EntityId,
    string ScopeId,
    string CandidateId,
    int? SeasonNumber,
    int? EpisodeNumber,
    bool IsMapped,
    string? Message);

public sealed record TvdbShowOrderPreviewDto(
    string SeriesId,
    string CurrentSeasonType,
    string RequestedSeasonType,
    string CurrentRevision,
    IReadOnlyList<string> AvailableSeasonTypes,
    IReadOnlyList<TvdbShowOrderImpactDto> AffectedScopes,
    bool CanApply,
    string? BlockingMessage);

public sealed record ApplyTvdbShowOrderDto(string SeasonType, string ExpectedRevision);

public sealed record TvdbShowOrderResultDto(string SeasonType, string NewRevision, string Message);
