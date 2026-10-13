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

    /// <summary><c>organise_folder</c> or <c>merge_folder</c>.</summary>
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
