namespace MediaEngine.Contracts.Metadata;

public sealed record MediaEditorEditionCoverPreviewRequestDto(
    Guid AssetId,
    Guid ArtworkAssetId);

public sealed record MediaEditorEditionCoverAffectedFileDto(
    Guid AssetId,
    Guid LibraryId);

public sealed record MediaEditorEditionCoverPreviewDto(
    string ReviewToken,
    DateTimeOffset ExpiresAt,
    Guid AssetId,
    Guid EditionId,
    Guid WorkId,
    string MediaType,
    string CurrentOwnerKind,
    Guid? CurrentOwnerId,
    Guid ArtworkAssetId,
    string EditionRevision,
    string? MusicBrainzReleaseId,
    IReadOnlyList<MediaEditorEditionCoverAffectedFileDto> AffectedFiles);

public sealed record MediaEditorEditionCoverSaveRequestDto(
    string ReviewToken,
    string OperationToken);

public sealed record MediaEditorEditionCoverSaveResultDto(
    string Outcome,
    Guid EditionId,
    Guid ArtworkAssetId);
