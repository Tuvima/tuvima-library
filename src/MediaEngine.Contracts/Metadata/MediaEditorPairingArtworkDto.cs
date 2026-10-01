namespace MediaEngine.Contracts.Metadata;

/// <summary>
/// Reviews one managed episode still against a complete TV pairing choice.
/// Only references are client-authored; owner, variant, and impact facts are
/// resolved and frozen by the server.
/// </summary>
public sealed record MediaEditorPairingArtworkPreviewRequestDto(
    string ReviewToken,
    IReadOnlyList<MediaEditorPairingAcceptedDto> Accepted,
    IReadOnlyList<Guid> ExcludedAssetIds,
    string TargetCandidateId,
    Guid ArtworkAssetId);

public sealed record MediaEditorPairingArtworkAffectedFileDto(Guid AssetId, Guid LibraryId);

public sealed record MediaEditorPairingArtworkPreviewDto(
    string ArtworkReviewToken,
    DateTimeOffset ExpiresAt,
    Guid OwnerWorkId,
    Guid ArtworkAssetId,
    string PreferenceRevision,
    IReadOnlyList<MediaEditorPairingArtworkAffectedFileDto> AffectedFiles,
    string Scope = "episode");
