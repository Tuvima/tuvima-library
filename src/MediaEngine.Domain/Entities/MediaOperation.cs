namespace MediaEngine.Domain.Entities;

public sealed class MediaOperation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string OperationType { get; init; } = "";
    public string OperationKind { get; init; } = "";
    public Guid? EntityId { get; init; }
    public string? EntityKind { get; init; }
    public Guid? BatchId { get; init; }
    public string? SourcePath { get; init; }
    public string? ContentHash { get; init; }
    public string? CapabilityId { get; init; }
    public string? CapabilityVersion { get; init; }
    public string? SubKey { get; init; }
    public string? PluginId { get; init; }
    public string? PluginVersion { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string Status { get; init; } = MediaOperationStatus.Pending;
    public string? Stage { get; init; }
    public int Priority { get; init; } = 100;
    public string QueueName { get; init; } = "default";
    public long PositionKey { get; init; }
    public int AttemptCount { get; init; }
    public int PoisonAttemptCount { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public DateTimeOffset? HeartbeatAt { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public int ProgressPercent { get; init; }
    public int ItemsTotal { get; init; }
    public int ItemsCompleted { get; init; }
    public int ItemsFailed { get; init; }
    public string? ResultSummary { get; init; }
    public string? LastError { get; init; }
    public string? LastOutcomeCategory { get; init; }
    public string? MissingReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public string IdempotencyKey { get; init; } = "";
}

public static class MediaOperationStatus
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Leased = "leased";
    public const string Running = "running";
    public const string RetryWaiting = "retry_waiting";
    public const string Succeeded = "succeeded";
    public const string NoResult = "no_result";
    public const string MissingConfirmed = "missing_confirmed";
    public const string NotApplicable = "not_applicable";
    public const string Blocked = "blocked";
    public const string FailedRetryable = "failed_retryable";
    public const string FailedTerminal = "failed_terminal";
    public const string DeadLettered = "dead_lettered";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
    public const string Skipped = "skipped";
}

public static class MediaOperationKind
{
    public const string Ingestion = "ingestion";
    public const string Identity = "identity";
    public const string Enrichment = "enrichment";
    public const string TextTrack = "text_track";
    public const string Plugin = "plugin";
    public const string Writeback = "writeback";
    public const string Ai = "ai";
    public const string Maintenance = "maintenance";
    public const string ViewDocument = "view_document";
}

public static class MediaOperationType
{
    public const string IngestionFile = "ingestion.file";
    public const string IdentityRetailMatch = "identity.retail_match";
    public const string IdentityWikidataBridge = "identity.wikidata_bridge";
    public const string IdentityQuickHydration = "identity.quick_hydration";
    public const string EnrichmentCoverArt = "enrichment.cover_art";
    public const string EnrichmentPeople = "enrichment.people";
    public const string EnrichmentDescription = "enrichment.description";
    public const string EnrichmentRelationships = "enrichment.relationships";
    public const string TextTrackLyrics = "text_track.lyrics";
    public const string TextTrackSubtitles = "text_track.subtitles";
    public const string PluginCommercialSkip = "plugin.commercial_skip";
    public const string PluginPlaybackSegmentDetection = "plugin.playback_segment_detection";
    public const string WritebackMetadata = "writeback.metadata";
    public const string AiTldr = "ai.tldr";
    public const string AiVibeTags = "ai.vibe_tags";
    public const string AiSmartLabels = "ai.smart_labels";
    public const string ViewDocumentTextRead = "view.document_text_read";
}

public static class MediaOperationStage
{
    public const string Discovered = "discovered";
    public const string Settling = "settling";
    public const string WaitingForLock = "waiting_for_lock";
    public const string Queued = "queued";
    public const string Hashing = "hashing";
    public const string Parsing = "parsing";
    public const string Scoring = "scoring";
    public const string Registered = "registered";
    public const string QueuedIdentity = "queued_identity";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Planned = "planned";
    public const string ProviderLookup = "provider_lookup";
    public const string Downloading = "downloading";
    public const string Analyzing = "analyzing";
    public const string WritingArtifact = "writing_artifact";
    public const string NoResult = "no_result";
    public const string Blocked = "blocked";
}
