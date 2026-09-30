using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Metadata;

public sealed class MediaEditorContextDto
{
    [JsonPropertyName("launch_entity_id")]
    public Guid LaunchEntityId { get; set; }

    [JsonPropertyName("launch_entity_kind")]
    public string LaunchEntityKind { get; set; } = "Work";

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("editor_mode")]
    public string EditorMode { get; set; } = "singular";

    [JsonPropertyName("available_tabs")]
    public List<string> AvailableTabs { get; set; } = [];

    [JsonPropertyName("content_tab_label")]
    public string? ContentTabLabel { get; set; }

    [JsonPropertyName("supports_file_tab")]
    public bool SupportsFileTab { get; set; }

    [JsonPropertyName("file_metadata_sync_status")]
    public string? FileMetadataSyncStatus { get; set; }

    [JsonPropertyName("current_target_summary")]
    public MediaEditorTargetSummaryDto? CurrentTargetSummary { get; set; }

    [JsonPropertyName("identity_summary")]
    public MediaEditorIdentitySummaryDto? IdentitySummary { get; set; }

    [JsonPropertyName("field_lock_map")]
    public Dictionary<string, bool> FieldLockMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("display_override_keys")]
    public List<string> DisplayOverrideKeys { get; set; } = [];

    [JsonPropertyName("display_overrides")]
    public Dictionary<string, string> DisplayOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("initial_scope")]
    public string InitialScope { get; set; } = string.Empty;

    [JsonPropertyName("scopes")]
    public List<MediaEditorScopeDto> Scopes { get; set; } = [];
}

public sealed class MediaEditorTargetSummaryDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }
}

public sealed class MediaEditorIdentitySummaryDto
{
    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; set; }

    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; set; }

    [JsonPropertyName("match_source")]
    public string? MatchSource { get; set; }

    [JsonPropertyName("match_method")]
    public string? MatchMethod { get; set; }

    [JsonPropertyName("wikidata_qid")]
    public string? WikidataQid { get; set; }

    [JsonPropertyName("wikidata_status")]
    public string? WikidataStatus { get; set; }

    [JsonPropertyName("qid_resolution_method")]
    public string? QidResolutionMethod { get; set; }

    [JsonPropertyName("match_level")]
    public string? MatchLevel { get; set; }

    [JsonPropertyName("universe_name")]
    public string? UniverseName { get; set; }

    [JsonPropertyName("universe_qid")]
    public string? UniverseQid { get; set; }

    [JsonPropertyName("stage3_status")]
    public string? Stage3Status { get; set; }
}

public sealed class MediaEditorScopeDto
{
    [JsonPropertyName("scope_id")]
    public string ScopeId { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("field_entity_id")]
    public Guid FieldEntityId { get; set; }

    [JsonPropertyName("field_entity_kind")]
    public string FieldEntityKind { get; set; } = "Work";

    [JsonPropertyName("artwork_owner_entity_id")]
    public Guid? ArtworkOwnerEntityId { get; set; }

    [JsonPropertyName("artwork_owner_entity_kind")]
    public string? ArtworkOwnerEntityKind { get; set; }

    [JsonPropertyName("display_title")]
    public string DisplayTitle { get; set; } = string.Empty;

    [JsonPropertyName("display_subtitle")]
    public string? DisplaySubtitle { get; set; }

    [JsonPropertyName("breadcrumb_label")]
    public string BreadcrumbLabel { get; set; } = string.Empty;

    [JsonPropertyName("canonical_target_group")]
    public string CanonicalTargetGroup { get; set; } = string.Empty;

    [JsonPropertyName("identity_summary")]
    public MediaEditorIdentitySummaryDto? IdentitySummary { get; set; }

    [JsonPropertyName("scope_summary")]
    public string? ScopeSummary { get; set; }

    [JsonPropertyName("read_only_hint")]
    public string? ReadOnlyHint { get; set; }

    [JsonPropertyName("can_edit_fields")]
    public bool CanEditFields { get; set; } = true;

    [JsonPropertyName("artwork_slots")]
    public List<string> ArtworkSlots { get; set; } = [];

    [JsonPropertyName("artwork_presentation")]
    public string ArtworkPresentation { get; set; } = "single";

    [JsonPropertyName("can_edit_artwork")]
    public bool CanEditArtwork { get; set; }

    [JsonPropertyName("available_tabs")]
    public List<string> AvailableTabs { get; set; } = [];

    [JsonPropertyName("content_tab_label")]
    public string? ContentTabLabel { get; set; }

    [JsonPropertyName("retail_identity_mode")]
    public string RetailIdentityMode { get; set; } = "owned";

    [JsonPropertyName("canonical_identity_mode")]
    public string CanonicalIdentityMode { get; set; } = "owned";

    [JsonPropertyName("canonical_identity_owner_scope_id")]
    public string? CanonicalIdentityOwnerScopeId { get; set; }

    [JsonPropertyName("artwork_mode")]
    public string ArtworkMode { get; set; } = "owned";

    [JsonPropertyName("artwork_owner_scope_id")]
    public string? ArtworkOwnerScopeId { get; set; }

    [JsonPropertyName("files_mode")]
    public string FilesMode { get; set; } = "item";

    [JsonPropertyName("history_owner_scope_id")]
    public string? HistoryOwnerScopeId { get; set; }
}

public sealed class MediaEditorNavigatorDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("container_entity_id")]
    public Guid ContainerEntityId { get; set; }

    [JsonPropertyName("selected_entity_id")]
    public Guid SelectedEntityId { get; set; }

    [JsonPropertyName("container_label")]
    public string ContainerLabel { get; set; } = string.Empty;

    [JsonPropertyName("container_title")]
    public string ContainerTitle { get; set; } = string.Empty;

    [JsonPropertyName("container_subtitle")]
    public string? ContainerSubtitle { get; set; }

    [JsonPropertyName("nodes")]
    public List<MediaEditorNavigatorNodeDto> Nodes { get; set; } = [];
}

public sealed class MediaEditorNavigatorNodeDto
{
    [JsonPropertyName("node_id")]
    public Guid NodeId { get; set; }

    [JsonPropertyName("parent_node_id")]
    public Guid? ParentNodeId { get; set; }

    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("scope_id")]
    public string ScopeId { get; set; } = string.Empty;

    [JsonPropertyName("node_kind")]
    public string NodeKind { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("ordinal_label")]
    public string? OrdinalLabel { get; set; }

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("is_root")]
    public bool IsRoot { get; set; }

    [JsonPropertyName("is_leaf")]
    public bool IsLeaf { get; set; }

    [JsonPropertyName("is_owned")]
    public bool IsOwned { get; set; }

    [JsonPropertyName("primary_asset_id")]
    public Guid? PrimaryAssetId { get; set; }

    [JsonPropertyName("artwork_url")]
    public string? ArtworkUrl { get; set; }

    [JsonPropertyName("artwork_shape")]
    public string ArtworkShape { get; set; } = "portrait";

    [JsonPropertyName("compact_ordinal_label")]
    public string? CompactOrdinalLabel { get; set; }

    [JsonPropertyName("technical_badges")]
    public List<string> TechnicalBadges { get; set; } = [];

    [JsonPropertyName("is_clickable")]
    public bool IsClickable { get; set; }

    [JsonPropertyName("can_select_as_editor_target")]
    public bool CanSelectAsEditorTarget { get; set; }

    [JsonPropertyName("can_quarantine")]
    public bool CanQuarantine { get; set; }

    [JsonPropertyName("quarantine_count")]
    public int QuarantineCount { get; set; }
}

public sealed class MediaEditorMembershipSuggestionDto
{
    [JsonPropertyName("entity_id")]
    public Guid? EntityId { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "local";

    [JsonPropertyName("local_existing")]
    public bool LocalExisting { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; set; }

    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; set; }

    [JsonPropertyName("external_id_key")]
    public string? ExternalIdKey { get; set; }

    [JsonPropertyName("external_id_value")]
    public string? ExternalIdValue { get; set; }
}

public sealed class MediaEditorMembershipPreviewRequestDto
{
    [JsonPropertyName("scope_id")]
    public string? ScopeId { get; set; }

    [JsonPropertyName("field_values")]
    public Dictionary<string, string?>? FieldValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("selected_target_ids")]
    public Dictionary<string, Guid?>? SelectedTargetIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("selected_suggestions")]
    public Dictionary<string, MediaEditorMembershipSuggestionDto>? SelectedSuggestions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MediaEditorMembershipPreviewDto
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("current_path")]
    public string CurrentPath { get; set; } = string.Empty;

    [JsonPropertyName("target_path")]
    public string TargetPath { get; set; } = string.Empty;

    [JsonPropertyName("requires_new_target")]
    public bool RequiresNewTarget { get; set; }

    [JsonPropertyName("can_apply")]
    public bool CanApply { get; set; }

    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    [JsonPropertyName("selected_entity_id")]
    public Guid SelectedEntityId { get; set; }

    [JsonPropertyName("target_root_entity_id")]
    public Guid TargetRootEntityId { get; set; }

    [JsonPropertyName("target_parent_entity_id")]
    public Guid? TargetParentEntityId { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("conflict_message")]
    public string? ConflictMessage { get; set; }

    [JsonPropertyName("stage2_target_entity_id")]
    public Guid? Stage2TargetEntityId { get; set; }

    [JsonPropertyName("source_parent_will_be_empty")]
    public bool SourceParentWillBeEmpty { get; set; }
}
