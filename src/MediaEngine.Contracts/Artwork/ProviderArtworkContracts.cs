namespace MediaEngine.Contracts.Artwork;

public sealed record ProviderArtworkCandidateDto(string Id, string Provider, string ThumbnailUrl,
    int? Width, int? Height, bool AlreadyAdded);
public sealed record ProviderArtworkDiscoveryDto(IReadOnlyList<ProviderArtworkCandidateDto> Items, string? Message,
    string? AttributionUrl = null,
    ProviderArtworkRequestContextDto? Context = null,
    int Page = 1,
    int PageSize = 48,
    int TotalCount = 0,
    bool HasMore = false);
public sealed record ProviderArtworkRequestContextDto(
    string ScopeId,
    string Role,
    string SourceAssetType,
    string Provider,
    string ProviderItemId,
    string? ReleaseId = null,
    string? OrderContext = null);
public sealed record ProviderArtworkDiscoveryRequestDto(
    int Page = 1,
    int PageSize = 48,
    string? SourceAssetType = null,
    string? Provider = null,
    string? ProviderItemId = null,
    string? ReleaseId = null,
    string? OrderContext = null);
public sealed record ProviderArtworkImportRequest(IReadOnlyList<string> CandidateIds);
public sealed record ProviderArtworkImportItemDto(string CandidateId, Guid? AssetId, string? Error);
public sealed record ProviderArtworkImportResultDto(IReadOnlyList<ProviderArtworkImportItemDto> Items);
