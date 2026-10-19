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
