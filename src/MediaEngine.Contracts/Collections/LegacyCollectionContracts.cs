using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Collections;

public sealed class RelatedCollectionsResponse
{
    [JsonPropertyName("section_title")] public string SectionTitle { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
    [JsonPropertyName("collections")] public List<CollectionDto> Collections { get; init; } = [];
}

public sealed class CollectionDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("universe_id")] public Guid? UniverseId { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("parent_collection_id")] public Guid? ParentCollectionId { get; init; }
    [JsonPropertyName("universe_status")] public string UniverseStatus { get; init; } = "Unknown";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("works")] public List<WorkDto> Works { get; init; } = [];
}

public sealed class ParentCollectionDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("universe_id")] public Guid? UniverseId { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("wikidata_qid")] public string? WikidataQid { get; init; }
    [JsonPropertyName("parent_collection_id")] public Guid? ParentCollectionId { get; init; }
    [JsonPropertyName("universe_status")] public string UniverseStatus { get; init; } = "Unknown";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("child_collection_count")] public int ChildCollectionCount { get; init; }
    [JsonPropertyName("media_types")] public string? MediaTypes { get; init; }
    [JsonPropertyName("total_works")] public int TotalWorks { get; init; }
    [JsonPropertyName("works")] public List<WorkDto> Works { get; init; } = [];
}

public sealed class WorkDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("collection_id")] public Guid? CollectionId { get; init; }
    [JsonPropertyName("media_type")] public string MediaType { get; init; } = string.Empty;
    [JsonPropertyName("ordinal")] public int? Ordinal { get; init; }
    [JsonPropertyName("universe_mismatch")] public bool UniverseMismatch { get; init; }
    [JsonPropertyName("universe_mismatch_at")] public DateTimeOffset? UniverseMismatchAt { get; init; }
    [JsonPropertyName("canonical_values")] public List<CanonicalValueDto> CanonicalValues { get; init; } = [];
}

public sealed class WorkDetailDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("collection_id")] public Guid? CollectionId { get; init; }
    [JsonPropertyName("parent_work_id")] public Guid? ParentWorkId { get; init; }
    [JsonPropertyName("media_type")] public string MediaType { get; init; } = string.Empty;
    [JsonPropertyName("work_kind")] public string WorkKind { get; init; } = string.Empty;
    [JsonPropertyName("ordinal")] public int? Ordinal { get; init; }
    [JsonPropertyName("is_catalog_only")] public bool IsCatalogOnly { get; init; }
    [JsonPropertyName("wikidata_qid")] public string? WikidataQid { get; init; }
    [JsonPropertyName("canonical_values")] public List<CanonicalValueDto> CanonicalValues { get; init; } = [];
    [JsonPropertyName("editions")] public List<EditionDto> Editions { get; init; } = [];
}

public sealed class EditionDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }
    [JsonPropertyName("format_label")] public string? FormatLabel { get; init; }
    [JsonPropertyName("wikidata_qid")] public string? WikidataQid { get; init; }
    [JsonPropertyName("canonical_values")] public List<CanonicalValueDto> CanonicalValues { get; init; } = [];
    [JsonPropertyName("assets")] public List<EditionAssetDto> Assets { get; init; } = [];
}

public sealed class EditionAssetDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("edition_id")] public Guid EditionId { get; init; }
    [JsonPropertyName("file_path_root")] public string FilePathRoot { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("canonical_values")] public List<CanonicalValueDto> CanonicalValues { get; init; } = [];
}

public sealed class CanonicalValueDto
{
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
    [JsonPropertyName("winning_provider_id")] public string? WinningProviderId { get; init; }
    [JsonPropertyName("last_scored_at")] public DateTimeOffset LastScoredAt { get; init; }
}

public sealed class LibraryWorkListItemDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("collectionId")] public Guid? CollectionId { get; init; }
    [JsonPropertyName("rootWorkId")] public Guid? RootWorkId { get; init; }
    [JsonPropertyName("mediaType")] public string MediaType { get; init; } = string.Empty;
    [JsonPropertyName("workKind")] public string? WorkKind { get; init; }
    [JsonPropertyName("ordinal")] public int? Ordinal { get; init; }
    [JsonPropertyName("wikidataQid")] public string? WikidataQid { get; init; }
    [JsonPropertyName("assetId")] public Guid? AssetId { get; init; }
    [JsonPropertyName("createdAt")] public string? CreatedAt { get; init; }
    [JsonPropertyName("coverUrl")] public string? CoverUrl { get; init; }
    [JsonPropertyName("backgroundUrl")] public string? BackgroundUrl { get; init; }
    [JsonPropertyName("bannerUrl")] public string? BannerUrl { get; init; }
    [JsonPropertyName("heroUrl")] public string? HeroUrl { get; init; }
    [JsonPropertyName("logoUrl")] public string? LogoUrl { get; init; }
    [JsonPropertyName("canonicalValues")]
    public Dictionary<string, string> CanonicalValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CollectionRulePredicateDto
{
    [JsonPropertyName("field")] public string Field { get; init; } = "media_type";
    [JsonPropertyName("op")] public string Op { get; init; } = "eq";
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("display_value")] public string? DisplayValue { get; init; }
    [JsonPropertyName("values")] public string[]? Values { get; init; }
    [JsonPropertyName("display_values")] public string[]? DisplayValues { get; init; }
}

public sealed class CollectionRuleGroupDto
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("join_with_previous")] public string JoinWithPrevious { get; init; } = "or";
    [JsonPropertyName("match_mode")] public string MatchMode { get; init; } = "all";
    [JsonPropertyName("conditions")] public List<CollectionRulePredicateDto> Conditions { get; init; } = [];
}

public sealed class CollectionRuleDefinitionDto
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("groups")] public List<CollectionRuleGroupDto> Groups { get; init; } = [];
}

public sealed record CollectionRuleValueDto(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("local_count")] int LocalCount,
    [property: JsonPropertyName("group")] string Group = "Cross-media");

public sealed class CollectionCreateRequest
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("visibility")] public string Visibility { get; init; } = "private";
    [JsonPropertyName("icon_name")] public string? IconName { get; init; }
    [JsonPropertyName("collection_type")] public string CollectionType { get; init; } = "Custom";
    [JsonPropertyName("membership_mode")] public string MembershipMode { get; init; } = "Manual";
    [JsonPropertyName("primary_area")] public string PrimaryArea { get; init; } = "Mixed";
    [JsonPropertyName("owner_kind")] public string OwnerKind { get; init; } = "Profile";
    [JsonPropertyName("audience")] public string Audience { get; init; } = "Private";
    [JsonPropertyName("selected_profile_ids")] public List<Guid> SelectedProfileIds { get; init; } = [];
    [JsonPropertyName("rule_definition")] public CollectionRuleDefinitionDto RuleDefinition { get; init; } = new();
    [JsonPropertyName("sort_field")] public string? SortField { get; init; }
    [JsonPropertyName("sort_direction")] public string SortDirection { get; init; } = "desc";
    [JsonPropertyName("secondary_sort_field")] public string? SecondarySortField { get; init; }
    [JsonPropertyName("secondary_sort_direction")] public string? SecondarySortDirection { get; init; }
    [JsonPropertyName("display_limit")] public int DisplayLimit { get; init; }
    [JsonPropertyName("placements")] public List<CollectionPlacementRequest>? Placements { get; init; }
    [JsonPropertyName("work_ids")] public List<Guid> WorkIds { get; init; } = [];
}

public sealed class CollectionPlacementRequest
{
    [JsonPropertyName("location")] public string Location { get; init; } = string.Empty;
    [JsonPropertyName("position")] public int Position { get; init; }
    [JsonPropertyName("display_limit")] public int DisplayLimit { get; init; }
    [JsonPropertyName("display_mode")] public string DisplayMode { get; init; } = "swimlane";
}

public sealed class CollectionUpdateRequest
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("visibility")] public string? Visibility { get; init; }
    [JsonPropertyName("icon_name")] public string? IconName { get; init; }
    [JsonPropertyName("membership_mode")] public string? MembershipMode { get; init; }
    [JsonPropertyName("primary_area")] public string? PrimaryArea { get; init; }
    [JsonPropertyName("owner_kind")] public string? OwnerKind { get; init; }
    [JsonPropertyName("audience")] public string? Audience { get; init; }
    [JsonPropertyName("selected_profile_ids")] public List<Guid>? SelectedProfileIds { get; init; }
    [JsonPropertyName("rule_definition")] public CollectionRuleDefinitionDto? RuleDefinition { get; init; }
    [JsonPropertyName("sort_field")] public string? SortField { get; init; }
    [JsonPropertyName("sort_direction")] public string? SortDirection { get; init; }
    [JsonPropertyName("secondary_sort_field")] public string? SecondarySortField { get; init; }
    [JsonPropertyName("secondary_sort_direction")] public string? SecondarySortDirection { get; init; }
    [JsonPropertyName("is_enabled")] public bool? IsEnabled { get; init; }
    [JsonPropertyName("is_featured")] public bool? IsFeatured { get; init; }
}

public sealed class CollectionPreviewRequest
{
    [JsonPropertyName("rule_definition")] public CollectionRuleDefinitionDto RuleDefinition { get; init; } = new();
    [JsonPropertyName("sort_field")] public string? SortField { get; init; }
    [JsonPropertyName("sort_direction")] public string SortDirection { get; init; } = "desc";
    [JsonPropertyName("secondary_sort_field")] public string? SecondarySortField { get; init; }
    [JsonPropertyName("secondary_sort_direction")] public string? SecondarySortDirection { get; init; }
    [JsonPropertyName("query")] public string? Query { get; init; }
    [JsonPropertyName("limit")] public int Limit { get; init; } = 20;
}

public sealed record CollectionPreviewResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("items")] List<CollectionResolvedItemDto> Items)
{
    [JsonPropertyName("media_type_counts")]
    public Dictionary<string, int> MediaTypeCounts { get; init; } = [];
}

public sealed class CollectionItemAddRequest
{
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }
}

public sealed class CollectionItemReorderRequest
{
    [JsonPropertyName("item_ids")] public List<Guid> ItemIds { get; init; } = [];
}

public sealed record CollectionEnabledRequest([property: JsonPropertyName("enabled")] bool Enabled);
public sealed record CollectionFeaturedRequest([property: JsonPropertyName("featured")] bool Featured);

public sealed record CollectionBackfillRequest(
    [property: JsonPropertyName("dry_run")] bool DryRun = false,
    [property: JsonPropertyName("batch_size")] int? BatchSize = null,
    [property: JsonPropertyName("max_items")] int? MaxItems = null);

public sealed record CollectionBackfillResponse(
    [property: JsonPropertyName("candidate_count")] int CandidateCount,
    [property: JsonPropertyName("processed_count")] int ProcessedCount,
    [property: JsonPropertyName("assigned_count")] int AssignedCount,
    [property: JsonPropertyName("created_collection_count")] int CreatedCollectionCount,
    [property: JsonPropertyName("already_assigned_count")] int AlreadyAssignedCount,
    [property: JsonPropertyName("skipped_count")] int SkippedCount,
    [property: JsonPropertyName("failed_count")] int FailedCount,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs);

public sealed class SeriesManifestViewDto
{
    [JsonPropertyName("collection_id")] public Guid CollectionId { get; init; }
    [JsonPropertyName("series_qid")] public required string SeriesQid { get; init; }
    [JsonPropertyName("series_label")] public string? SeriesLabel { get; init; }
    [JsonPropertyName("last_hydrated_at")] public DateTimeOffset? LastHydratedAt { get; init; }
    [JsonPropertyName("container_kind")] public string? ContainerKind { get; init; }
    [JsonPropertyName("expected_total")] public int? ExpectedTotal { get; init; }
    [JsonPropertyName("expected_total_kind")] public string? ExpectedTotalKind { get; init; }
    [JsonPropertyName("expected_total_source")] public string? ExpectedTotalSource { get; init; }
    [JsonPropertyName("expected_total_confidence")] public double? ExpectedTotalConfidence { get; init; }
    [JsonPropertyName("total_count")] public int TotalCount { get; init; }
    [JsonPropertyName("owned_count")] public int OwnedCount { get; init; }
    [JsonPropertyName("missing_count")] public int MissingCount { get; init; }
    [JsonPropertyName("provisional_count")] public int ProvisionalCount { get; init; }
    [JsonPropertyName("ambiguous_count")] public int AmbiguousCount { get; init; }
    [JsonPropertyName("supplementary_count")] public int SupplementaryCount { get; init; }
    [JsonPropertyName("collected_content_count")] public int CollectedContentCount { get; init; }
    [JsonPropertyName("unpositioned_count")] public int UnpositionedCount { get; init; }
    [JsonPropertyName("authoritative_totals_by_container")]
    public IReadOnlyDictionary<string, int> AuthoritativeTotalsByContainer { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("warnings")] public IReadOnlyList<SeriesManifestWarningDto> Warnings { get; init; } = [];
    [JsonPropertyName("items")] public IReadOnlyList<SeriesManifestItemDto> Items { get; init; } = [];
}

public sealed class SeriesManifestItemDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("item_qid")] public required string ItemQid { get; init; }
    [JsonPropertyName("series_qid")] public required string SeriesQid { get; init; }
    [JsonPropertyName("item_label")] public string? ItemLabel { get; init; }
    [JsonPropertyName("item_description")] public string? ItemDescription { get; init; }
    [JsonPropertyName("media_type")] public string? MediaType { get; init; }
    [JsonPropertyName("media_kind")] public string? MediaKind { get; init; }
    [JsonPropertyName("instance_of_qids")] public IReadOnlyList<string> InstanceOfQids { get; init; } = [];
    [JsonPropertyName("raw_ordinal")] public string? RawOrdinal { get; init; }
    [JsonPropertyName("parsed_ordinal")] public double? ParsedOrdinal { get; init; }
    [JsonPropertyName("ordinal_scope_qid")] public string? OrdinalScopeQid { get; init; }
    [JsonPropertyName("sort_order")] public double? SortOrder { get; init; }
    [JsonPropertyName("publication_date")] public string? PublicationDate { get; init; }
    [JsonPropertyName("duration")] public string? Duration { get; init; }
    [JsonPropertyName("parent_collection_qid")] public string? ParentCollectionQid { get; init; }
    [JsonPropertyName("parent_collection_label")] public string? ParentCollectionLabel { get; init; }
    [JsonPropertyName("is_collection")] public bool IsCollection { get; init; }
    [JsonPropertyName("is_expanded_from_collection")] public bool IsExpandedFromCollection { get; init; }
    [JsonPropertyName("membership_scope")] public string MembershipScope { get; init; } = "MainSequence";
    [JsonPropertyName("order_source")] public required string OrderSource { get; init; }
    [JsonPropertyName("ownership_state")] public required string OwnershipState { get; init; }
    [JsonPropertyName("linked_work_id")] public Guid? LinkedWorkId { get; init; }
}

public sealed class SeriesManifestWarningDto
{
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("qid")] public string? Qid { get; init; }
}
