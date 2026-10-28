namespace MediaEngine.Domain.Entities;

public sealed class MediaOperation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string OperationType { get; init; } = "";
    public string OperationKind { get; init; } = "";
    public Guid? EntityId { get; init; }
    public string? EntityKind { get; init; }
    public string? SourcePath { get; init; }
    public string? ContentHash { get; init; }
    public string? CapabilityId { get; init; }
    public string? CapabilityVersion { get; init; }
    public string? SubKey { get; init; }
    public string? PluginId { get; init; }
    public string? PluginVersion { get; init; }

    /// <summary>
    /// The audience word (<c>administrators</c>, <c>adults</c> or <c>everyone</c>) of the person who asked for this work, or <c>null</c> when
    /// the Library asked itself. Plugins and sources are used only within their own audience when the work really runs.
    /// </summary>
    public string? RequestedAudience { get; init; }

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

    /// <summary>A person paused a long-running job. It keeps its place and its progress and is not picked up again until it is resumed.</summary>
    public const string Paused = "paused";
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
    public const string ViewDocumentOcr = "view.document_ocr";

    /// <summary>Runs a person's auto-filing rules and works out Inbox suggestions for one document.</summary>
    public const string ViewDocumentApplyRules = "view.document_apply_rules";

    /// <summary>Converts a folder from As-is to Managed: renames and reorganises the files already there.</summary>
    public const string OrganiseFolder = "organise_folder";

    /// <summary>Merges one existing folder, once, into a managed folder with the same naming, duplicate and tag rules.</summary>
    public const string MergeFolder = "merge_folder";

    /// <summary>Changes what a folder is (Library setup D40(c)): moves it to a library of another kind in the same area and identifies its files again under that kind.</summary>
    public const string ChangeFolderKind = "change_folder_kind";
}

/// <summary>Queue names for operations that need more than the default queue.</summary>
public static class MediaOperationQueue
{
    /// <summary>Long folder jobs (organising a folder, merging folders). They run one at a time.</summary>
    public const string FolderJobs = "folder_jobs";
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
    public const string Organising = "organising";
    public const string Paused = "paused";
}
