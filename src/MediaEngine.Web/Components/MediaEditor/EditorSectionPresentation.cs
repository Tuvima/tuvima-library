namespace MediaEngine.Web.Components.MediaEditor;

// Read-only presentation records shared by editor sections. Mutable drafts,
// permissions, loading and all persistence remain in SharedMediaEditorShell.

public sealed record MatchCardDisplay(
    string Badge,
    string Title,
    string? Creator,
    string? Year,
    string? CoverUrl,
    IReadOnlyList<string> Chips,
    IReadOnlyList<IdentityLinkDisplay> Links,
    string Note);

public sealed record IdentityLinkDisplay(string Label, string Url);

public sealed record CandidateComparisonRow(string Label, string LocalValue, string CandidateValue, double Score, string? Verdict = null);

public sealed record ArtworkSlotDefinition(
    string AssetType,
    string Label,
    string Description,
    string Icon,
    string PreviewClass,
    string ImageClass,
    bool UploadEnabled,
    string UploadHelp,
    string MetaLabel);

public sealed record ArtworkVariantDisplayItem(
    string Key,
    Guid VariantId,
    string AssetType,
    string? ImageUrl,
    bool IsPreferred,
    bool IsPending,
    bool CanDelete,
    string Origin,
    string? ProviderName,
    DateTimeOffset? CreatedAt,
    int? WidthPx,
    int? HeightPx);

public sealed record HistoryTimelineEntry(
    DateTimeOffset OccurredAt, string Category, string Label, string? Detail,
    string ActorLabel, string? Scope, int AffectedSelectedFiles, string EventType);
