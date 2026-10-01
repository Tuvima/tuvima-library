namespace MediaEngine.Contracts.Metadata;

/// <summary>Only selection and target identity are client inputs; evidence is read from owned assets.</summary>
public sealed record MediaEditorPairingPreviewRequestDto(
    IReadOnlyList<Guid> AssetIds,
    string? TargetParentId,
    IReadOnlyDictionary<Guid, string>? ExpectedSelectionRevisions);

public sealed record MediaEditorPairingChildDto(
    string ChildId,
    string ParentId,
    string Provider,
    string Title,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? DiscNumber,
    int? TrackNumber,
    string? RecordingId,
    Guid? LocalSeasonWorkId = null);

public sealed record MediaEditorPairingCandidateDto(
    MediaEditorPairingChildDto Child,
    string Band,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Conflicts,
    bool CanSave = false,
    string? SaveLimitation = null);

public sealed record MediaEditorPairingRowDto(
    Guid AssetId,
    string OriginalFileName,
    string? CurrentAssignmentId,
    string? CurrentAssignmentScope,
    MediaEditorPairingCandidateDto? Proposed,
    IReadOnlyList<MediaEditorPairingCandidateDto> Alternatives,
    string Band,
    bool CanPreselect,
    string? Limitation,
    bool CanSave = false,
    string? SaveLimitation = null);

public sealed record MediaEditorPairingPreviewDto(
    string MediaKind,
    string Provider,
    string TargetParentId,
    bool CatalogueComplete,
    string? CatalogueWarning,
    IReadOnlyList<MediaEditorPairingRowDto> Rows,
    string? ReviewToken = null,
    DateTimeOffset? ReviewExpiresAt = null,
    Guid? LocalParentWorkId = null);

public sealed record MediaEditorPairingAcceptedDto(Guid AssetId, string CandidateId);

public sealed record MediaEditorPairingSaveRequestDto(
    string ReviewToken,
    string OperationToken,
    IReadOnlyList<MediaEditorPairingAcceptedDto> Accepted,
    IReadOnlyList<Guid> ExcludedAssetIds,
    string? ArtworkReviewToken = null,
    string? SharedArtworkReviewToken = null);

public sealed record MediaEditorPairingSavedRowDto(
    Guid AssetId,
    string Outcome,
    string? SyncState,
    string? ConflictReason);

public sealed record MediaEditorPairingSaveResultDto(
    string Outcome,
    IReadOnlyList<MediaEditorPairingSavedRowDto> Rows);

public sealed record MediaEditorPairingChildSearchRequestDto(
    string ReviewToken,
    Guid AssetId,
    string? Query = null,
    int? SeasonNumber = null,
    int Offset = 0,
    int Limit = 50);

public sealed record MediaEditorPairingChildSearchItemDto(
    MediaEditorPairingChildDto Child,
    bool CanSave,
    string? SaveLimitation);

public sealed record MediaEditorPairingChildSearchDto(
    Guid AssetId,
    int TotalCount,
    IReadOnlyList<MediaEditorPairingChildSearchItemDto> Items);
