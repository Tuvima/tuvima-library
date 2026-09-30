using System.Text.Json.Serialization;
using MediaEngine.Contracts.Search;

namespace MediaEngine.Contracts.Matching;

public sealed class ApplyMatchRequestDto
{
    [JsonPropertyName("qid")]
    public string? Qid { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("director")]
    public string? Director { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("retail_provider_name")]
    public string? RetailProviderName { get; set; }

    [JsonPropertyName("retail_provider_item_id")]
    public string? RetailProviderItemId { get; set; }

    [JsonPropertyName("retail_bridge_ids")]
    public Dictionary<string, string>? RetailBridgeIds { get; set; }

    [JsonPropertyName("retail_description")]
    public string? RetailDescription { get; set; }
}

public sealed class ApplyMatchResponseDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("wikidata_status")]
    public string WikidataStatus { get; set; } = string.Empty;

    [JsonPropertyName("claims_written")]
    public int ClaimsWritten { get; set; }

    [JsonPropertyName("hydration_triggered")]
    public bool HydrationTriggered { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public sealed class ItemCanonicalSearchRequestDto
{
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("target_kind")]
    public string TargetKind { get; set; } = string.Empty;

    [JsonPropertyName("target_field_group")]
    public string TargetFieldGroup { get; set; } = string.Empty;

    [JsonPropertyName("draft_fields")]
    public Dictionary<string, string> DraftFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("query_override")]
    public string? QueryOverride { get; set; }

    [JsonPropertyName("search_mode")]
    public string SearchMode { get; set; } = "retail_only";

    [JsonPropertyName("max_candidates")]
    public int MaxCandidates { get; set; } = 6;
}

public sealed class ItemCanonicalSearchResponseDto
{
    public List<string> ProviderErrors { get; set; } = [];

    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("target_kind")]
    public string TargetKind { get; set; } = string.Empty;

    [JsonPropertyName("target_field_group")]
    public string TargetFieldGroup { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("retail_candidates")]
    public List<ItemCanonicalRetailCandidateDto> RetailCandidates { get; set; } = [];

    [JsonPropertyName("linked_candidates")]
    public List<ItemCanonicalLinkedCandidateDto> LinkedCandidates { get; set; } = [];

    [JsonPropertyName("fallback_actions")]
    public List<string> FallbackActions { get; set; } = [];

    [JsonPropertyName("no_result_message")]
    public string? NoResultMessage { get; set; }

    [JsonPropertyName("can_apply_unlinked_canonical")]
    public bool CanApplyUnlinkedCanonical { get; set; }

    [JsonPropertyName("missing_required_fields")]
    public List<string> MissingRequiredFields { get; set; } = [];

    [JsonPropertyName("unlinked_fields")]
    public Dictionary<string, string> UnlinkedFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("draft_fields")]
    public Dictionary<string, string> DraftFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ItemCanonicalRetailCandidateDto
{
    [JsonPropertyName("candidate_id")]
    public string CandidateId { get; set; } = string.Empty;

    [JsonPropertyName("provider_id")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = string.Empty;

    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("director")]
    public string? Director { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("preview_url")]
    public string? PreviewUrl { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("extra_fields")]
    public Dictionary<string, string> ExtraFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("composite_score")]
    public double CompositeScore { get; set; }

    [JsonPropertyName("match_scores")]
    public FieldMatchScoresDto? MatchScores { get; set; }

    [JsonPropertyName("link_state")]
    public string LinkState { get; set; } = "provider_only";

    [JsonPropertyName("link_status_label")]
    public string LinkStatusLabel { get; set; } = "Linked to provider only";

    [JsonPropertyName("is_applicable")]
    public bool IsApplicable { get; set; }

    [JsonPropertyName("blocked_reason")]
    public string? BlockedReason { get; set; }

    [JsonPropertyName("required_fields")]
    public Dictionary<string, string> RequiredFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("suggested_fields")]
    public Dictionary<string, string> SuggestedFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("bridge_ids")]
    public Dictionary<string, string> BridgeIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("qid_fields")]
    public Dictionary<string, string> QidFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ItemCanonicalLinkedCandidateDto
{
    [JsonPropertyName("candidate_id")]
    public string CandidateId { get; set; } = string.Empty;

    [JsonPropertyName("qid")]
    public string Qid { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("instance_of")]
    public string? InstanceOf { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("director")]
    public string? Director { get; set; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("wikipedia_extract")]
    public string? WikipediaExtract { get; set; }

    [JsonPropertyName("resolution_tier")]
    public string? ResolutionTier { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("match_scores")]
    public FieldMatchScoresDto? MatchScores { get; set; }

    [JsonPropertyName("bridge_ids")]
    public Dictionary<string, string> BridgeIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("media_type_metadata")]
    public Dictionary<string, string> MediaTypeMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("link_state")]
    public string LinkState { get; set; } = "linked";

    [JsonPropertyName("link_status_label")]
    public string LinkStatusLabel { get; set; } = "Linked to Wikidata";

    [JsonPropertyName("is_applicable")]
    public bool IsApplicable { get; set; }

    [JsonPropertyName("blocked_reason")]
    public string? BlockedReason { get; set; }

    [JsonPropertyName("required_fields")]
    public Dictionary<string, string> RequiredFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("suggested_fields")]
    public Dictionary<string, string> SuggestedFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("qid_fields")]
    public Dictionary<string, string> QidFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ItemCanonicalApplyRequestDto
{
    [JsonPropertyName("target_kind")]
    public string TargetKind { get; set; } = string.Empty;

    [JsonPropertyName("target_field_group")]
    public string TargetFieldGroup { get; set; } = string.Empty;

    [JsonPropertyName("link_state")]
    public string LinkState { get; set; } = string.Empty;

    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; set; }

    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; set; }

    [JsonPropertyName("required_fields")]
    public Dictionary<string, string> RequiredFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("suggested_fields")]
    public Dictionary<string, string> SuggestedFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("accepted_suggested_keys")]
    public List<string> AcceptedSuggestedKeys { get; set; } = [];

    [JsonPropertyName("bridge_ids")]
    public Dictionary<string, string> BridgeIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("qid_fields")]
    public Dictionary<string, string> QidFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ItemCanonicalApplyResponseDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("link_state")]
    public string LinkState { get; set; } = string.Empty;

    [JsonPropertyName("link_status_label")]
    public string LinkStatusLabel { get; set; } = string.Empty;

    [JsonPropertyName("fields_applied")]
    public int FieldsApplied { get; set; }

    [JsonPropertyName("ids_cleared")]
    public List<string> IdsCleared { get; set; } = [];

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("identity_job_id")]
    public Guid? IdentityJobId { get; set; }

    [JsonPropertyName("pipeline_state")]
    public string? PipelineState { get; set; }

    [JsonPropertyName("artwork_changed")]
    public bool ArtworkChanged { get; set; }

    [JsonPropertyName("artwork_removed_count")]
    public int ArtworkRemovedCount { get; set; }

    [JsonPropertyName("artwork_message")]
    public string? ArtworkMessage { get; set; }

    [JsonPropertyName("hierarchy_changed")]
    public bool HierarchyChanged { get; set; }

    [JsonPropertyName("selected_entity_id")]
    public Guid SelectedEntityId { get; set; }

    [JsonPropertyName("target_root_entity_id")]
    public Guid TargetRootEntityId { get; set; }

    [JsonPropertyName("target_parent_entity_id")]
    public Guid? TargetParentEntityId { get; set; }

    [JsonPropertyName("previous_path")]
    public string? PreviousPath { get; set; }

    [JsonPropertyName("target_path")]
    public string? TargetPath { get; set; }

    [JsonPropertyName("identity_revision")]
    public string? IdentityRevision { get; set; }
}

/// <summary>
/// The side-effect-free structural impact of applying a selected retail result.
/// The provider candidate itself is supplied by the request; this payload only
/// describes the locally-owned item and any parent move it would require.
/// </summary>
public sealed class RetailMatchMovePreviewDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("identity_target_id")]
    public Guid IdentityTargetId { get; set; }

    [JsonPropertyName("current_identity_revision")]
    public string CurrentIdentityRevision { get; set; } = string.Empty;

    [JsonPropertyName("expected_identity_revision")]
    public string? ExpectedIdentityRevision { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = "none";

    [JsonPropertyName("requires_parent_move_confirmation")]
    public bool RequiresParentMoveConfirmation { get; set; }

    [JsonPropertyName("can_apply")]
    public bool CanApply { get; set; }

    [JsonPropertyName("current_parent_entity_id")]
    public Guid? CurrentParentEntityId { get; set; }

    [JsonPropertyName("target_parent_entity_id")]
    public Guid? TargetParentEntityId { get; set; }

    [JsonPropertyName("target_root_entity_id")]
    public Guid TargetRootEntityId { get; set; }

    [JsonPropertyName("current_path")]
    public string CurrentPath { get; set; } = string.Empty;

    [JsonPropertyName("target_path")]
    public string TargetPath { get; set; } = string.Empty;

    [JsonPropertyName("source_parent_will_be_empty")]
    public bool SourceParentWillBeEmpty { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("conflict_message")]
    public string? ConflictMessage { get; set; }
}

public sealed class ReplaceRetailMatchRequestDto
{
    [JsonPropertyName("target_kind")]
    public string TargetKind { get; set; } = string.Empty;

    [JsonPropertyName("target_field_group")]
    public string TargetFieldGroup { get; set; } = string.Empty;

    [JsonPropertyName("target_scope_id")]
    public string TargetScopeId { get; set; } = string.Empty;

    [JsonPropertyName("provider_id")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = string.Empty;

    [JsonPropertyName("provider_item_id")]
    public string ProviderItemId { get; set; } = string.Empty;

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("required_fields")]
    public Dictionary<string, string> RequiredFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("suggested_fields")]
    public Dictionary<string, string> SuggestedFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("bridge_ids")]
    public Dictionary<string, string> BridgeIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("clear_auto_aligned_wikidata")]
    public bool ClearAutoAlignedWikidata { get; set; } = true;

    [JsonPropertyName("review_item_id")]
    public Guid? ReviewItemId { get; set; }

    [JsonPropertyName("expected_identity_revision")]
    public string? ExpectedIdentityRevision { get; set; }
}

public sealed class ReplaceWikidataMatchRequestDto
{
    [JsonPropertyName("target_kind")]
    public string TargetKind { get; set; } = string.Empty;

    [JsonPropertyName("target_field_group")]
    public string TargetFieldGroup { get; set; } = string.Empty;

    [JsonPropertyName("target_scope_id")]
    public string TargetScopeId { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = "replace";

    [JsonPropertyName("qid")]
    public string? Qid { get; set; }

    [JsonPropertyName("suggested_fields")]
    public Dictionary<string, string> SuggestedFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("accepted_suggested_keys")]
    public List<string> AcceptedSuggestedKeys { get; set; } = [];

    [JsonPropertyName("rejected_qid")]
    public string? RejectedQid { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("keep_retail_match")]
    public bool KeepRetailMatch { get; set; } = true;

    [JsonPropertyName("rehydrate_now")]
    public bool RehydrateNow { get; set; } = true;

    [JsonPropertyName("review_item_id")]
    public Guid? ReviewItemId { get; set; }
}

public sealed class CreateManualRequestDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class CreateManualResponseDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("wikidata_status")]
    public string WikidataStatus { get; set; } = "manual";

    [JsonPropertyName("claims_written")]
    public int ClaimsWritten { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
