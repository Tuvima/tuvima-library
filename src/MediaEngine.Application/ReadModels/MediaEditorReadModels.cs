using System.Text.Json.Serialization;

namespace MediaEngine.Application.ReadModels;

public sealed record MembershipPreviewRequest(
    [property: JsonPropertyName("scope_id")] string? ScopeId,
    [property: JsonPropertyName("field_values")] Dictionary<string, string?>? FieldValues,
    [property: JsonPropertyName("selected_target_ids")] Dictionary<string, Guid?>? SelectedTargetIds,
    [property: JsonPropertyName("selected_suggestions")] Dictionary<string, MembershipSuggestionSelection>? SelectedSuggestions);

public sealed record MembershipSuggestionSelection(
    [property: JsonPropertyName("entity_id")] Guid? EntityId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("local_existing")] bool LocalExisting,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("provider_name")] string? ProviderName,
    [property: JsonPropertyName("provider_item_id")] string? ProviderItemId,
    [property: JsonPropertyName("external_id_key")] string? ExternalIdKey,
    [property: JsonPropertyName("external_id_value")] string? ExternalIdValue);

/// <summary>
/// The identity artifacts that must commit alongside a confirmed structural
/// alignment. Kept as primitive data so the API can build the intent without
/// leaking storage repository implementations into its endpoint surface.
/// </summary>
public sealed record HierarchyIdentityMutation(
    IReadOnlyList<HierarchyClaimMutation> Claims,
    IReadOnlyList<HierarchyCanonicalMutation> CanonicalValues,
    IReadOnlyList<HierarchyBridgeIdMutation> BridgeIds,
    IReadOnlyList<HierarchyIdentityArtifactMutation> StaleArtifacts,
    IReadOnlyList<HierarchyExternalIdentifierMutation>? ExternalIdentifierMutations = null);

public sealed record HierarchyClaimMutation(
    Guid EntityId,
    Guid ProviderId,
    Guid DecisionSourceProviderId,
    string Key,
    string Value,
    double Confidence,
    bool IsUserLocked,
    DateTimeOffset ClaimedAt);

public sealed record HierarchyCanonicalMutation(
    Guid EntityId,
    string Key,
    string Value,
    Guid? WinningProviderId,
    bool NeedsReview,
    DateTimeOffset LastScoredAt);

public sealed record HierarchyBridgeIdMutation(
    Guid EntityId,
    string Key,
    string Value,
    string ProviderName,
    DateTimeOffset CreatedAt);

public sealed record HierarchyIdentityArtifactMutation(Guid EntityId, string Key);

public sealed record HierarchyExternalIdentifierMutation(
    Guid EntityId,
    IReadOnlyList<string> KeysToRemove,
    IReadOnlyDictionary<string, string> Replacements);

public sealed record MediaEditorNavigatorEnvelope(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("container_entity_id")] Guid ContainerEntityId,
    [property: JsonPropertyName("selected_entity_id")] Guid SelectedEntityId,
    [property: JsonPropertyName("container_label")] string ContainerLabel,
    [property: JsonPropertyName("container_title")] string ContainerTitle,
    [property: JsonPropertyName("container_subtitle")] string? ContainerSubtitle,
    [property: JsonPropertyName("nodes")] IReadOnlyList<MediaEditorNavigatorNodeEnvelope> Nodes);

public sealed record MediaEditorNavigatorNodeEnvelope(
    [property: JsonPropertyName("node_id")] Guid NodeId,
    [property: JsonPropertyName("parent_node_id")] Guid? ParentNodeId,
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("node_kind")] string NodeKind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("ordinal_label")] string? OrdinalLabel,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("is_root")] bool IsRoot,
    [property: JsonPropertyName("is_leaf")] bool IsLeaf,
    [property: JsonPropertyName("is_owned")] bool IsOwned,
    [property: JsonPropertyName("primary_asset_id")] Guid? PrimaryAssetId,
    [property: JsonPropertyName("compact_ordinal_label")] string? CompactOrdinalLabel,
    [property: JsonPropertyName("technical_badges")] IReadOnlyList<string> TechnicalBadges,
    [property: JsonPropertyName("is_clickable")] bool IsClickable,
    [property: JsonPropertyName("can_select_as_editor_target")] bool CanSelectAsEditorTarget,
    [property: JsonPropertyName("can_quarantine")] bool CanQuarantine,
    [property: JsonPropertyName("quarantine_count")] int QuarantineCount,
    string? ArtworkUrl = null,
    string ArtworkShape = "portrait");

public sealed record MembershipSuggestionEnvelope(
    [property: JsonPropertyName("entity_id")] Guid? EntityId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("local_existing")] bool LocalExisting,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("provider_name")] string? ProviderName,
    [property: JsonPropertyName("provider_item_id")] string? ProviderItemId,
    [property: JsonPropertyName("external_id_key")] string? ExternalIdKey,
    [property: JsonPropertyName("external_id_value")] string? ExternalIdValue,
    [property: JsonPropertyName("image_url")] string? ImageUrl = null);

public sealed record MembershipPreviewEnvelope(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("current_path")] string CurrentPath,
    [property: JsonPropertyName("target_path")] string TargetPath,
    [property: JsonPropertyName("requires_new_target")] bool RequiresNewTarget,
    [property: JsonPropertyName("can_apply")] bool CanApply,
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("selected_entity_id")] Guid SelectedEntityId,
    [property: JsonPropertyName("target_root_entity_id")] Guid TargetRootEntityId,
    [property: JsonPropertyName("target_parent_entity_id")] Guid? TargetParentEntityId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("conflict_message")] string? ConflictMessage,
    [property: JsonPropertyName("stage2_target_entity_id")] Guid? Stage2TargetEntityId = null,
    [property: JsonPropertyName("source_parent_will_be_empty")] bool SourceParentWillBeEmpty = false);
