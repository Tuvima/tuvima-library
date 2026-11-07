namespace MediaEngine.Contracts.Display;

public sealed record UniversalSearchResponseDto(
    string Query,
    UniversalSearchResultDto? TopResult,
    IReadOnlyList<UniversalSearchSectionDto> Sections,
    int TotalCount);

public sealed record UniversalSearchSectionDto(
    string Key,
    string Title,
    IReadOnlyList<UniversalSearchResultDto> Results,
    int TotalCount,
    string? SeeAllRoute);

public sealed record UniversalSearchResultDto(
    Guid Id,
    string EntityType,
    string? MediaType,
    string Title,
    string? Subtitle,
    string? Creator,
    string? Year,
    string? ArtworkUrl,
    string? Description,
    string DetailRoute,
    string PrimaryActionLabel,
    string MatchReason,
    double Relevance)
{
    public IReadOnlyList<string> Facts { get; init; } = [];
    public IReadOnlyList<UniversalSearchPreviewItemDto> PreviewItems { get; init; } = [];

    /// <summary>Who owns the file and where in the owner it sits; set only on a result of the "Supporting files" section.</summary>
    public MediaEngine.Contracts.Details.SupportingFileSearchHitDto? SupportingFile { get; init; }
}

public sealed record UniversalSearchPreviewItemDto(
    Guid Id,
    string Title,
    string? Subtitle,
    string? ArtworkUrl,
    string? DetailRoute);
