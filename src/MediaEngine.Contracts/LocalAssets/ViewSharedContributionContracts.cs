using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.LocalAssets;

public sealed record ViewSharedTransferPreviewDto(
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("total_bytes")] long TotalBytes,
    [property: JsonPropertyName("destination_root")] string DestinationRoot,
    [property: JsonPropertyName("destination_kind")] string DestinationKind,
    [property: JsonPropertyName("source_cleanup_required")] bool SourceCleanupRequired,
    [property: JsonPropertyName("already_shared")] bool AlreadyShared);

public sealed record ViewSharedTransferRequest(
    [property: JsonPropertyName("destination_kind")] string DestinationKind = "timeline",
    [property: JsonPropertyName("folder_name")] string? FolderName = null);

public sealed record ViewSharedTransferResultDto(
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("files_transferred")] int FilesTransferred,
    [property: JsonPropertyName("destination_paths")] IReadOnlyList<string> DestinationPaths,
    [property: JsonPropertyName("source_cleanup_pending")] bool SourceCleanupPending);

public sealed record ViewSharedContributionPreviewRequest(
    [property: JsonPropertyName("item_ids")] IReadOnlyList<Guid> ItemIds,
    [property: JsonPropertyName("destination_kind")] string DestinationKind = "timeline",
    [property: JsonPropertyName("folder_name")] string? FolderName = null);

public sealed record ViewSharedContributionPreviewDto(
    [property: JsonPropertyName("preview_revision")] string PreviewRevision,
    [property: JsonPropertyName("items")] IReadOnlyList<ViewSharedTransferPreviewDto> Items,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("total_bytes")] long TotalBytes,
    [property: JsonPropertyName("move_count")] int MoveCount,
    [property: JsonPropertyName("copy_count")] int CopyCount,
    [property: JsonPropertyName("destination_kind")] string DestinationKind,
    [property: JsonPropertyName("destination_label")] string? DestinationLabel);

public sealed record ViewSharedContributionSubmitRequest(
    [property: JsonPropertyName("item_ids")] IReadOnlyList<Guid> ItemIds,
    [property: JsonPropertyName("destination_kind")] string DestinationKind,
    [property: JsonPropertyName("folder_name")] string? FolderName,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("preview_revision")] string PreviewRevision,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey);

public sealed record ViewSharedDirectAddRequest(
    [property: JsonPropertyName("item_ids")] IReadOnlyList<Guid> ItemIds,
    [property: JsonPropertyName("destination_kind")] string DestinationKind = "timeline",
    [property: JsonPropertyName("folder_name")] string? FolderName = null,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("idempotency_key")] string? IdempotencyKey = null);

public sealed record ViewSharedContributionDecisionRequest(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("expected_revision")] int ExpectedRevision,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("destination_kind")] string? DestinationKind = null,
    [property: JsonPropertyName("folder_name")] string? FolderName = null);

public sealed record ViewSharedContributionRevisionRequest(
    [property: JsonPropertyName("expected_revision")] int ExpectedRevision);

public sealed record ViewSharedContributionItemDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("media_kind")] string MediaKind,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("execution_state")] string ExecutionState,
    [property: JsonPropertyName("error")] string? Error);

public sealed record ViewSharedContributionEventDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("actor_name")] string ActorName,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt);

public sealed record ViewSharedContributionDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("submitted_by_profile_id")] Guid? SubmittedByProfileId,
    [property: JsonPropertyName("submitted_by_name")] string SubmittedByName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("destination_kind")] string DestinationKind,
    [property: JsonPropertyName("destination_label")] string? DestinationLabel,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("submitted_at")] DateTimeOffset SubmittedAt,
    [property: JsonPropertyName("decided_at")] DateTimeOffset? DecidedAt,
    [property: JsonPropertyName("decided_by_name")] string? DecidedByName,
    [property: JsonPropertyName("decision_reason")] string? DecisionReason,
    [property: JsonPropertyName("items")] IReadOnlyList<ViewSharedContributionItemDto> Items,
    [property: JsonPropertyName("events")] IReadOnlyList<ViewSharedContributionEventDto> Events,
    [property: JsonPropertyName("includes_hidden_items")] bool IncludesHiddenItems = false);

public sealed record ViewSharedContributionPageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ViewSharedContributionDto> Items,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("can_submit")] bool CanSubmit,
    [property: JsonPropertyName("can_review")] bool CanReview);
