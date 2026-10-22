using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Ingestion;

/// <summary>
/// The live ingestion numbers, worked out once by the Engine from the per-file work rows and sent as one snapshot
/// (over <c>GET /ingestion/queue-progress</c> and the <c>IngestionQueueProgress</c> event). The Dashboard shows them and
/// never derives its own.
/// </summary>
public sealed class IngestionQueueProgressDto
{
    /// <summary>"working", "waiting" (every unfinished file waits for a missing tool) or "idle".</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "idle";

    /// <summary>Files in the active period's set.</summary>
    [JsonPropertyName("files_found")]
    public int FilesFound { get; set; }

    [JsonPropertyName("files_done")]
    public int FilesDone { get; set; }

    /// <summary>Files held back by a missing tool.</summary>
    [JsonPropertyName("files_waiting")]
    public int FilesWaiting { get; set; }

    /// <summary>The distinct tools holding files (for example "ffmpeg"); empty when none.</summary>
    [JsonPropertyName("waiting_for")]
    public IReadOnlyList<string> WaitingFor { get; set; } = [];

    /// <summary>Files that joined after the period's first snapshot ("120 more found").</summary>
    [JsonPropertyName("files_more_found")]
    public int FilesMoreFound { get; set; }

    [JsonPropertyName("outcomes")]
    public IngestionQueueOutcomesDto Outcomes { get; set; } = new();

    /// <summary>Files per media type, for the types with at least one file.</summary>
    [JsonPropertyName("tally")]
    public IReadOnlyList<IngestionQueueTypeTallyDto> Tally { get; set; } = [];

    /// <summary>Files whose media type is not known yet.</summary>
    [JsonPropertyName("files_unsorted")]
    public int FilesUnsorted { get; set; }

    /// <summary>Nine steps per file found.</summary>
    [JsonPropertyName("steps_total")]
    public long StepsTotal { get; set; }

    /// <summary>The sum of every file's step number.</summary>
    [JsonPropertyName("steps_completed")]
    public long StepsCompleted { get; set; }

    /// <summary>The value to display, 0 to 1. It never decreases within a period and is 1 only when idle.</summary>
    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    /// <summary>When the active period started; null when idle and no period has run.</summary>
    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>The newest operation or identity update seen.</summary>
    [JsonPropertyName("last_activity_at")]
    public DateTimeOffset? LastActivityAt { get; set; }

    /// <summary>Seconds left; null unless the estimate is reliable.</summary>
    [JsonPropertyName("estimated_seconds_remaining")]
    public int? EstimatedSecondsRemaining { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>How the finished files ended.</summary>
public sealed class IngestionQueueOutcomesDto
{
    [JsonPropertyName("added")]
    public int Added { get; set; }

    [JsonPropertyName("needs_review")]
    public int NeedsReview { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    /// <summary>Files the Engine could not match. They stay in the Library and are looked up again later; they are not Needs Review items.</summary>
    [JsonPropertyName("unmatched")]
    public int Unmatched { get; set; }
}

/// <summary>How many files of one media type are in the queue.</summary>
public sealed class IngestionQueueTypeTallyDto
{
    /// <summary>The media type name (for example "Movies").</summary>
    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "";

    [JsonPropertyName("files")]
    public int Files { get; set; }
}
