namespace MediaEngine.Contracts.Artwork;

public sealed record ProviderArtworkCandidateDto(string Id, string Provider, string ThumbnailUrl,
    int? Width, int? Height, bool AlreadyAdded);
public sealed record ProviderArtworkDiscoveryDto(IReadOnlyList<ProviderArtworkCandidateDto> Items, string? Message);
public sealed record ProviderArtworkImportRequest(IReadOnlyList<string> CandidateIds);
public sealed record ProviderArtworkImportItemDto(string CandidateId, Guid? AssetId, string? Error);
public sealed record ProviderArtworkImportResultDto(IReadOnlyList<ProviderArtworkImportItemDto> Items);
