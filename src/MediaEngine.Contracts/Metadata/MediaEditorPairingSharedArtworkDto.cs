namespace MediaEngine.Contracts.Metadata;

/// <summary>
/// Requests a reviewed show/season preference within an existing TV pairing
/// choice. Owner and image IDs are references; the server resolves all facts.
/// </summary>
public sealed record MediaEditorPairingSharedArtworkPreviewRequestDto(
    string ReviewToken,
    IReadOnlyList<MediaEditorPairingAcceptedDto> Accepted,
    IReadOnlyList<Guid> ExcludedAssetIds,
    Guid OwnerWorkId,
    string Scope,
    string Role,
    Guid ArtworkAssetId);

public sealed record MediaEditorPairingSharedArtworkPreviewDto(
    string SharedArtworkReviewToken,
    DateTimeOffset ExpiresAt,
    Guid OwnerWorkId,
    string Scope,
    string Role,
    Guid ArtworkAssetId,
    string PreferenceRevision,
    IReadOnlyList<MediaEditorPairingArtworkAffectedFileDto> AffectedFiles);
