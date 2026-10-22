using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.WriteBack;

/// <summary>
/// Where the batched file write-back stands (Settings > Libraries > Advanced). Changes are saved in Library at once; the files catch up
/// when a scheduled run, or Write now, works through the waiting list.
/// </summary>
public sealed class FileWriteScheduleDto
{
    /// <summary>False when write-back is turned off: nothing is queued and the interval does not apply.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("interval_minutes")]
    public int IntervalMinutes { get; init; }

    /// <summary>Files with a write waiting or being written.</summary>
    [JsonPropertyName("waiting_count")]
    public int WaitingCount { get; init; }

    [JsonPropertyName("failed_count")]
    public int FailedCount { get; init; }

    [JsonPropertyName("is_running")]
    public bool IsRunning { get; init; }

    /// <summary>While a run is going: how many files it is writing and how many are done. Zero otherwise.</summary>
    [JsonPropertyName("run_total")]
    public int RunTotal { get; init; }

    [JsonPropertyName("run_done")]
    public int RunDone { get; init; }

    /// <summary>When the last run finished; null before the first run.</summary>
    [JsonPropertyName("last_run_at")]
    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>When the next scheduled run starts; null when nothing is waiting or write-back is off.</summary>
    [JsonPropertyName("next_run_at")]
    public DateTimeOffset? NextRunAt { get; init; }
}

/// <summary>Changes the time between scheduled runs, 1 to 1440 minutes.</summary>
public sealed class UpdateFileWriteScheduleDto
{
    [JsonPropertyName("interval_minutes")]
    public int IntervalMinutes { get; init; }
}

/// <summary>One file that could not be written, with why.</summary>
public sealed class FailedFileWriteDto
{
    /// <summary><c>media_asset</c> or <c>view_item</c>.</summary>
    [JsonPropertyName("target_kind")]
    public string TargetKind { get; init; } = string.Empty;

    [JsonPropertyName("asset_id")]
    public Guid? AssetId { get; init; }

    [JsonPropertyName("local_item_id")]
    public Guid? LocalItemId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("attempts")]
    public int Attempts { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>What is waiting to be written into one media file (the File section of Editing Mode).</summary>
public sealed class AssetFileWriteStatusDto
{
    /// <summary>True when changes are waiting to be written into the file.</summary>
    [JsonPropertyName("waiting")]
    public bool Waiting { get; init; }

    /// <summary>When the next scheduled run starts; null when nothing is waiting.</summary>
    [JsonPropertyName("next_run_at")]
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>Why the last attempt did not work, when it did not.</summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    /// <summary>When Library last wrote this file successfully.</summary>
    [JsonPropertyName("last_written_at")]
    public DateTimeOffset? LastWrittenAt { get; init; }
}
