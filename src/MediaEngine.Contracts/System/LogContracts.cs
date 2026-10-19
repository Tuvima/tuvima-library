using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.System;

/// <summary>
/// The four retention limits on the Logs page. The same shape is read, and sent back to change them.
/// Personal activity is not covered: it is kept until a person or administrator clears it.
/// </summary>
public sealed class LogRetentionSettingsDto
{
    /// <summary>How many days the readable Events log is kept.</summary>
    [JsonPropertyName("activity_retention_days")]
    public int ActivityRetentionDays { get; init; }

    /// <summary>The most events kept; the oldest go first once there are more.</summary>
    [JsonPropertyName("activity_max_events")]
    public int ActivityMaxEvents { get; init; }

    /// <summary>How many days the Engine's technical log files are kept.</summary>
    [JsonPropertyName("log_retention_days")]
    public int LogRetentionDays { get; init; }

    /// <summary>The most space, in megabytes, all technical log files may take together; the oldest files go first.</summary>
    [JsonPropertyName("log_max_total_mb")]
    public int LogMaxTotalMegabytes { get; init; }
}

/// <summary>The retention limits plus how much is stored now, so the page can show "412,000 of 500,000 events".</summary>
public sealed class LogRetentionStatusDto
{
    [JsonPropertyName("settings")]
    public LogRetentionSettingsDto Settings { get; init; } = new();

    /// <summary>Events in the Events log now.</summary>
    [JsonPropertyName("activity_event_count")]
    public long ActivityEventCount { get; init; }

    /// <summary>Technical log files on disk now.</summary>
    [JsonPropertyName("log_file_count")]
    public int LogFileCount { get; init; }

    /// <summary>Total size of the technical log files on disk now, in bytes.</summary>
    [JsonPropertyName("log_total_bytes")]
    public long LogTotalBytes { get; init; }
}

/// <summary>One line of the Engine's technical log.</summary>
public sealed class TechnicalLogLineDto
{
    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; init; }

    /// <summary><c>verbose</c>, <c>debug</c>, <c>information</c>, <c>warning</c>, <c>error</c> or <c>fatal</c>.</summary>
    [JsonPropertyName("level")]
    public string Level { get; init; } = "information";

    /// <summary>Ingestion, Playback, Network, Providers, Security, AI or Other.</summary>
    [JsonPropertyName("area")]
    public string Area { get; init; } = "Other";

    /// <summary>The message; an error also carries its detail on the lines after the first.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>A page of technical log lines, newest first.</summary>
public sealed class TechnicalLogPageDto
{
    [JsonPropertyName("lines")]
    public List<TechnicalLogLineDto> Lines { get; init; } = [];

    /// <summary>Pass this back as <c>cursor</c> to get the next, older page. Null when there are no older lines.</summary>
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}

/// <summary>One recurring Engine task for the Scheduled tasks list.</summary>
public sealed class ScheduledTaskDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>One line saying what the task does.</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>When it runs, in plain words ("Every day at 3:00").</summary>
    [JsonPropertyName("schedule")]
    public string Schedule { get; init; } = string.Empty;

    /// <summary>When it last ran since the Engine started; null when it has not.</summary>
    [JsonPropertyName("last_run_at")]
    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary><c>done</c> or <c>failed</c>; null when it has not run since the Engine started.</summary>
    [JsonPropertyName("last_run_result")]
    public string? LastRunResult { get; init; }

    /// <summary>A short reason, set only when the last run failed.</summary>
    [JsonPropertyName("last_run_reason")]
    public string? LastRunReason { get; init; }

    /// <summary>When it runs next; null when the task does not know.</summary>
    [JsonPropertyName("next_run_at")]
    public DateTimeOffset? NextRunAt { get; init; }
}

/// <summary>Every recurring Engine task, plus when the Engine started (what "last run" is measured from).</summary>
public sealed class ScheduledTasksResponse
{
    [JsonPropertyName("engine_started_at")]
    public DateTimeOffset EngineStartedAt { get; init; }

    [JsonPropertyName("tasks")]
    public List<ScheduledTaskDto> Tasks { get; init; } = [];
}
