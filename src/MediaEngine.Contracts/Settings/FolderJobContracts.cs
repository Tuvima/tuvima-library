using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>Asks what converting a folder to Managed would do. Nothing is changed.</summary>
public sealed class FolderConversionPreviewRequest
{
    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = string.Empty;

    /// <summary>
    /// True for the quick answer the confirmation dialog needs: a count of the media files in the folder, read from folder
    /// listings only. False for the preview itself.
    /// </summary>
    [JsonPropertyName("quick")]
    public bool Quick { get; set; }

    /// <summary>For the preview, how many files to examine. 0 examines every file; a large folder is sampled first.</summary>
    [JsonPropertyName("file_limit")]
    public int FileLimit { get; set; }
}

public sealed class FolderConversionSampleDto
{
    [JsonPropertyName("old_path")]
    public string OldPath { get; set; } = string.Empty;

    [JsonPropertyName("new_path")]
    public string NewPath { get; set; } = string.Empty;
}

public sealed class FolderConversionSkipDto
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>What converting a folder would do, worked out without changing anything.</summary>
public sealed class FolderConversionPreviewResponse
{
    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Every media file in the folder (a quick count, not a scan).</summary>
    [JsonPropertyName("total_files")]
    public int TotalFiles { get; set; }

    /// <summary>How many of them are video, which sets the time estimate.</summary>
    [JsonPropertyName("video_files")]
    public int VideoFiles { get; set; }

    /// <summary>How many files the preview examined; fewer than <see cref="TotalFiles"/> for a sampled large folder.</summary>
    [JsonPropertyName("examined")]
    public int Examined { get; set; }

    [JsonPropertyName("complete")]
    public bool Complete { get; set; }

    [JsonPropertyName("to_rename")]
    public int ToRename { get; set; }

    [JsonPropertyName("already_correct")]
    public int AlreadyCorrect { get; set; }

    [JsonPropertyName("duplicates_to_remove")]
    public int DuplicatesToRemove { get; set; }

    [JsonPropertyName("needs_review")]
    public int NeedsReview { get; set; }

    [JsonPropertyName("name_collisions")]
    public int NameCollisions { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    /// <summary>Shortcuts (links and junctions) inside the folder that the job leaves alone.</summary>
    [JsonPropertyName("skipped_links")]
    public int SkippedLinks { get; set; }

    [JsonPropertyName("skip_reasons")]
    public List<FolderConversionSkipDto> SkipReasons { get; set; } = [];

    [JsonPropertyName("samples")]
    public List<FolderConversionSampleDto> Samples { get; set; } = [];

    /// <summary>About how long the conversion will take, in seconds. Replaced by the live rate once it is running.</summary>
    [JsonPropertyName("estimated_seconds")]
    public int EstimatedSeconds { get; set; }

    /// <summary>The "same file twice" setting: <c>keep_one</c> or <c>keep_both</c>.</summary>
    [JsonPropertyName("duplicate_policy")]
    public string DuplicatePolicy { get; set; } = "keep_one";
}

/// <summary>Converts a folder to Managed and organises the files already in it.</summary>
public sealed class FolderConvertRequest
{
    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = string.Empty;
}

/// <summary>Merges one existing folder, once, into a managed folder of the same library.</summary>
public sealed class FolderMergeRequest
{
    /// <summary>The folder whose files are merged.</summary>
    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = string.Empty;

    /// <summary>The managed folder the files go into.</summary>
    [JsonPropertyName("target_source_id")]
    public string TargetSourceId { get; set; } = string.Empty;

    /// <summary><c>copy</c> (the default: the source is never written) or <c>move</c> (each original is removed once its copy is checked).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "copy";
}

/// <summary>Asks what merging a folder would do. Nothing is changed.</summary>
public sealed class FolderMergePreviewRequest
{
    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("target_source_id")]
    public string TargetSourceId { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "copy";

    /// <summary>True for the quick answer the sheet opens with: a count and the room needed, read from folder listings only.</summary>
    [JsonPropertyName("quick")]
    public bool Quick { get; set; }

    /// <summary>For the preview, how many files to examine. 0 examines every file; a large folder is sampled first.</summary>
    [JsonPropertyName("file_limit")]
    public int FileLimit { get; set; }
}

/// <summary>What merging a folder would do, worked out without changing anything.</summary>
public sealed class FolderMergePreviewResponse
{
    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("target_source_id")]
    public string TargetSourceId { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "copy";

    /// <summary>Every media file in the source folder (a quick count, not a scan).</summary>
    [JsonPropertyName("total_files")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("video_files")]
    public int VideoFiles { get; set; }

    /// <summary>How many files the preview examined; fewer than <see cref="TotalFiles"/> for a sampled large folder.</summary>
    [JsonPropertyName("examined")]
    public int Examined { get; set; }

    [JsonPropertyName("complete")]
    public bool Complete { get; set; }

    /// <summary>Files that would be copied (or moved) to their standard names.</summary>
    [JsonPropertyName("to_transfer")]
    public int ToTransfer { get; set; }

    /// <summary>Files an identical copy of which is already held: at the standard name in the target, or elsewhere in the library.</summary>
    [JsonPropertyName("already_in_target")]
    public int AlreadyInTarget { get; set; }

    /// <summary>Files that cannot be named yet; they go to the target's From merge folder and wait in Review.</summary>
    [JsonPropertyName("needs_review")]
    public int NeedsReview { get; set; }

    [JsonPropertyName("name_collisions")]
    public int NameCollisions { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("skip_reasons")]
    public List<FolderConversionSkipDto> SkipReasons { get; set; } = [];

    [JsonPropertyName("samples")]
    public List<FolderConversionSampleDto> Samples { get; set; } = [];

    [JsonPropertyName("estimated_seconds")]
    public int EstimatedSeconds { get; set; }

    /// <summary>The "same file twice" setting: <c>keep_one</c> or <c>keep_both</c>.</summary>
    [JsonPropertyName("duplicate_policy")]
    public string DuplicatePolicy { get; set; } = "keep_one";

    /// <summary>Total size of the source folder's media files, in bytes.</summary>
    [JsonPropertyName("source_bytes")]
    public long SourceBytes { get; set; }

    /// <summary>Room left on the target folder's drive, in bytes; null when it cannot be read.</summary>
    [JsonPropertyName("target_free_bytes")]
    public long? TargetFreeBytes { get; set; }

    /// <summary>Why the merge cannot start right now, in plain words; null when it can.</summary>
    [JsonPropertyName("blocked_reason")]
    public string? BlockedReason { get; set; }
}

/// <summary>Pause, resume or cancel a folder job. <see cref="Action"/> is <c>pause</c>, <c>resume</c> or <c>cancel</c>.</summary>
public sealed class FolderJobControlRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;
}

/// <summary>Where a folder job is, in the words the folder row shows.</summary>
public sealed class FolderJobStatusDto
{
    [JsonPropertyName("operation_id")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = string.Empty;

    /// <summary><c>organise_folder</c>, <c>merge_folder</c> or <c>change_folder_kind</c>.</summary>
    [JsonPropertyName("job_type")]
    public string JobType { get; set; } = "organise_folder";

    /// <summary><c>queued</c>, <c>running</c>, <c>paused</c>, <c>completed</c>, <c>cancelled</c> or <c>failed</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "queued";

    [JsonPropertyName("items_total")]
    public int ItemsTotal { get; set; }

    [JsonPropertyName("items_completed")]
    public int ItemsCompleted { get; set; }

    [JsonPropertyName("items_failed")]
    public int ItemsFailed { get; set; }

    [JsonPropertyName("progress_percent")]
    public int ProgressPercent { get; set; }

    /// <summary>About how long is left, in seconds; null until there is something to base it on.</summary>
    [JsonPropertyName("estimated_seconds_remaining")]
    public int? EstimatedSecondsRemaining { get; set; }

    /// <summary>One plain sentence about the job, for example "Stopped after 1,240 of 8,310; the rest will be tidied gradually."</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>The name of the folder this job is waiting behind, when another job holds the queue.</summary>
    [JsonPropertyName("queued_behind")]
    public string? QueuedBehind { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>True once the job has a log of its moves that can be read or exported.</summary>
    [JsonPropertyName("has_log")]
    public bool HasLog { get; set; }
}

public sealed class FolderJobLogEntryDto
{
    [JsonPropertyName("old_path")]
    public string OldPath { get; set; } = string.Empty;

    [JsonPropertyName("new_path")]
    public string NewPath { get; set; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }
}

/// <summary>A page of a folder job's log: every file it looked at, old path to new path.</summary>
public sealed class FolderJobLogDto
{
    [JsonPropertyName("operation_id")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("entries")]
    public List<FolderJobLogEntryDto> Entries { get; set; } = [];
}

/// <summary>
/// Asks to change what a folder is (Library setup D40(c)): the kind it should become and the library of that kind it goes into. The folder is
/// named by the route. A kind that holds one library needs no <see cref="LibraryId"/> (the kind's own library is used); a kind that allows
/// several named libraries needs the id of an existing library of that kind. A new library is created first, through the normal library
/// action, and its id is passed here.
/// </summary>
public sealed class FolderKindChangeRequest
{
    /// <summary>The kind the folder becomes (for example <c>other_audio</c>). Never a Vault kind, never the folder's current kind, and always in the same area.</summary>
    [JsonPropertyName("kind_key")]
    public string KindKey { get; set; } = string.Empty;

    /// <summary>The existing library of that kind the folder moves into. Required for a kind with several libraries (unless it has exactly one); when given it must be a library of <see cref="KindKey"/>.</summary>
    [JsonPropertyName("library_id")]
    public string? LibraryId { get; set; }
}

/// <summary>What changing what a folder is would do, worked out without changing anything: the numbers for the confirmation (D60).</summary>
public sealed class FolderKindChangePreviewResponse
{
    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = string.Empty;

    /// <summary>What the folder is now (the kind's label, for example "Movies").</summary>
    [JsonPropertyName("from_kind_label")]
    public string FromKindLabel { get; set; } = string.Empty;

    [JsonPropertyName("to_kind_key")]
    public string ToKindKey { get; set; } = string.Empty;

    [JsonPropertyName("to_kind_label")]
    public string ToKindLabel { get; set; } = string.Empty;

    /// <summary>The library the folder moves into.</summary>
    [JsonPropertyName("to_library_id")]
    public string ToLibraryId { get; set; } = string.Empty;

    [JsonPropertyName("to_library_name")]
    public string ToLibraryName { get; set; } = string.Empty;

    /// <summary>The files in the folder that change kind with it.</summary>
    [JsonPropertyName("file_count")]
    public int FileCount { get; set; }

    /// <summary>How many files will be re-filed into the new kind's naming pattern in the next batch; 0 for a Read-only folder, where nothing moves.</summary>
    [JsonPropertyName("moves")]
    public int Moves { get; set; }

    /// <summary>True when the new kind is plain and the folder's kind now looks details up, so the details looked up for its files are dropped (D69).</summary>
    [JsonPropertyName("drops_details")]
    public bool DropsDetails { get; set; }

    /// <summary>True when the new kind looks details up, so a new first check starts for the folder and moves wait until it passes (D35, D61).</summary>
    [JsonPropertyName("starts_first_check")]
    public bool StartsFirstCheck { get; set; }

    /// <summary>True for a Managed folder: other apps pointing at its files (another player, a playlist, a backup) will lose them when they are re-filed.</summary>
    [JsonPropertyName("other_apps_warning")]
    public bool OtherAppsWarning { get; set; }

    /// <summary>The people who can see these files now and would not in the new library (Simpler access D13). Meaningful only when <see cref="VisibilityLossChecked"/> is true.</summary>
    [JsonPropertyName("visibility_loss")]
    public List<MediaEngine.Contracts.Authentication.VisibilityLossDto> VisibilityLoss { get; set; } = [];

    /// <summary>False when who would lose sight of the files could not be worked out; an empty <see cref="VisibilityLoss"/> then does not mean nobody.</summary>
    [JsonPropertyName("visibility_loss_checked")]
    public bool VisibilityLossChecked { get; set; }

    /// <summary>The plain reason the question was refused or could not be answered, when <see cref="VisibilityLossChecked"/> is false.</summary>
    [JsonPropertyName("visibility_message")]
    public string? VisibilityMessage { get; set; }
}
