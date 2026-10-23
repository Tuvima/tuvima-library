using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>Parameters for background housekeeping tasks.</summary>
public sealed class MaintenanceSettings
{
    /// <summary>Maximum retained transaction log entries.</summary>
    [JsonPropertyName("max_transaction_log_entries")]
    public int MaxTransactionLogEntries { get; set; } = 100_000;

    /// <summary>Whether startup should issue a VACUUM.</summary>
    [JsonPropertyName("vacuum_on_startup")]
    public bool VacuumOnStartup { get; set; } = false;

    /// <summary>Number of days to retain system activity entries.</summary>
    [JsonPropertyName("activity_retention_days")]
    public int ActivityRetentionDays { get; set; } = 90;

    /// <summary>
    /// Most system activity entries to keep. Once exceeded, the oldest entries go first. This is the size limit of the
    /// readable Events log; personal activity is not covered (it is kept until a person or administrator clears it).
    /// </summary>
    [JsonPropertyName("activity_max_events")]
    public int ActivityMaxEvents { get; set; } = 500_000;

    /// <summary>Number of days to keep the Engine's technical log files.</summary>
    [JsonPropertyName("log_retention_days")]
    public int LogRetentionDays { get; set; } = 90;

    /// <summary>
    /// Total size, in megabytes, of all Engine log files together. Once exceeded, the oldest files go first
    /// (the file being written is never removed).
    /// </summary>
    [JsonPropertyName("log_max_total_mb")]
    public int LogMaxTotalMegabytes { get; set; } = 1024;

    /// <summary>Interval in days between automatic metadata sync runs.</summary>
    [JsonPropertyName("weekly_sync_interval_days")]
    public int WeeklySyncIntervalDays { get; set; } = 7;

    /// <summary>Number of entities to enqueue per metadata sync batch.</summary>
    [JsonPropertyName("weekly_sync_batch_size")]
    public int WeeklySyncBatchSize { get; set; } = 50;

    /// <summary>Milliseconds to wait between metadata sync batches.</summary>
    [JsonPropertyName("weekly_sync_batch_delay_ms")]
    public int WeeklySyncBatchDelayMs { get; set; } = 2000;

    /// <summary>Interval in hours between automatic reconciliation scans.</summary>
    [JsonPropertyName("reconciliation_interval_hours")]
    public int ReconciliationIntervalHours { get; set; } = 24;

    /// <summary>Number of days to retain rejected files before cleanup.</summary>
    [JsonPropertyName("rejected_retention_days")]
    public int RejectedRetentionDays { get; set; } = 30;

    /// <summary>
    /// How many days state waits for a missing file before it is cleared: a person's progress and bookmarks restored from a backup
    /// while their file is not in the library yet (backup and restore spec D5, D13). Safer deleting reuses this one setting for how
    /// long a Missing file is kept, so the Library has a single waiting period rather than two.
    /// </summary>
    [JsonPropertyName("missing_file_retention_days")]
    public int MissingFileRetentionDays { get; set; } = MissingFileRetentionLimits.DefaultDays;

    /// <summary>
    /// How many days a file the Engine could not match waits before it is looked up again (Library setup spec D34). A provider or plugin
    /// being switched on or updated, and Retry now, look it up again sooner. Weekly by default.
    /// </summary>
    [JsonPropertyName("unmatched_retry_days")]
    public int UnmatchedRetryDays { get; set; } = UnmatchedRetryLimits.DefaultDays;

    /// <summary>
    /// How many files of a newly added folder must reach a final identification outcome (matched, unmatched or needs review) before
    /// the first check decides whether the folder is the right kind and place (Library setup spec D35). A folder with fewer files is
    /// decided once all of them have, and its ingestion has settled.
    /// </summary>
    [JsonPropertyName("first_check_files")]
    public int FirstCheckFiles { get; set; } = FirstCheckLimits.DefaultFiles;

    /// <summary>
    /// The share of those files that may be Unmatched before the folder pauses (Library setup spec D35): more than this share pauses
    /// it. Above 0 and below 1; 0.5 means more than half.
    /// </summary>
    [JsonPropertyName("first_check_unmatched_share")]
    public double FirstCheckUnmatchedShare { get; set; } = FirstCheckLimits.DefaultUnmatchedShare;

    /// <summary>Auto re-tag sweep parameters.</summary>
    [JsonPropertyName("retag_sweep")]
    public RetagSweepSettings RetagSweep { get; set; } = new();

    /// <summary>Database and generated-cache housekeeping parameters.</summary>
    [JsonPropertyName("storage_maintenance")]
    public StorageMaintenanceSettings StorageMaintenance { get; set; } = new();

    /// <summary>Cron expressions for background services.</summary>
    [JsonPropertyName("schedules")]
    public Dictionary<string, string> Schedules { get; set; } = new()
    {
        ["activity_pruning"] = "0 3 * * *",
        ["log_file_retention"] = "30 3 * * *",
        ["library_reconciliation"] = "0 5 * * *",
        ["missing_universe_sweep"] = "0 4 * * 0",
        ["rejected_file_cleanup"] = "0 4 * * *",
        ["universe_enrichment"] = "0 3 * * *",
        ["pass2_nightly_sweep"] = "0 2 * * *",
        ["retag_sweep"] = "0 3 * * *",
        ["storage_maintenance"] = "0 2 * * *",
        ["plugin_segment_detection"] = "0 2 * * *",
    };
}

/// <summary>The limits of <see cref="MaintenanceSettings.MissingFileRetentionDays"/>.</summary>
public static class MissingFileRetentionLimits
{
    /// <summary>The waiting period when nothing is configured.</summary>
    public const int DefaultDays = 90;

    /// <summary>The shortest waiting period.</summary>
    public const int MinDays = 7;

    /// <summary>The longest waiting period.</summary>
    public const int MaxDays = 3650;
}

/// <summary>The limits of <see cref="MaintenanceSettings.FirstCheckFiles"/> and <see cref="MaintenanceSettings.FirstCheckUnmatchedShare"/>.</summary>
public static class FirstCheckLimits
{
    /// <summary>The files tried before a new folder is judged when nothing is configured.</summary>
    public const int DefaultFiles = 50;

    /// <summary>The fewest files a first check may be set to try.</summary>
    public const int MinFiles = 1;

    /// <summary>The share of Unmatched files above which a new folder pauses when nothing is configured: more than half.</summary>
    public const double DefaultUnmatchedShare = 0.5;

    /// <summary>True when <paramref name="share"/> is above 0 and below 1.</summary>
    public static bool IsValidShare(double share) => share > 0 && share < 1;

    /// <summary>True when <paramref name="unmatched"/> of <paramref name="tried"/> files is more than <paramref name="share"/> (a setting outside the limits uses the default).</summary>
    public static bool ExceedsShare(int unmatched, int tried, double share) =>
        tried > 0 && unmatched > (IsValidShare(share) ? share : DefaultUnmatchedShare) * tried;
}

/// <summary>The limits of <see cref="MaintenanceSettings.UnmatchedRetryDays"/>.</summary>
public static class UnmatchedRetryLimits
{
    /// <summary>The days between lookups of an Unmatched file when nothing is configured: weekly.</summary>
    public const int DefaultDays = 7;

    /// <summary>The shortest wait.</summary>
    public const int MinDays = 1;

    /// <summary>The longest wait.</summary>
    public const int MaxDays = 365;

    /// <summary>When an Unmatched file is next looked up, <paramref name="configuredDays"/> (clamped to the limits) after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextRetryAt(DateTimeOffset now, int configuredDays) =>
        now.AddDays(Math.Clamp(configuredDays, MinDays, MaxDays));
}

public sealed class StorageMaintenanceSettings
{
    /// <summary>Enable the nightly storage maintenance hosted service.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Delete search result cache entries older than this many days.</summary>
    [JsonPropertyName("search_cache_max_age_days")]
    public int SearchCacheMaxAgeDays { get; set; } = 30;

    /// <summary>Delete unreferenced image cache rows/files older than this many days.</summary>
    [JsonPropertyName("image_cache_retention_days")]
    public int ImageCacheRetentionDays { get; set; } = 30;

    /// <summary>Maximum exact duplicate non-user-locked claims to compact per pass.</summary>
    [JsonPropertyName("claim_compaction_batch_size")]
    public int ClaimCompactionBatchSize { get; set; } = 5000;
}

public sealed class RetagSweepSettings
{
    /// <summary>Master switch for the sweep.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Local-time start of the off-hours retry window.</summary>
    [JsonPropertyName("off_hours_start")]
    public string OffHoursStart { get; set; } = "02:00";

    /// <summary>Local-time end of the off-hours retry window.</summary>
    [JsonPropertyName("off_hours_end")]
    public string OffHoursEnd { get; set; } = "06:00";

    /// <summary>Maximum retry attempts before review escalation.</summary>
    [JsonPropertyName("max_retry_attempts")]
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Number of stale assets to lease per worker tick.</summary>
    [JsonPropertyName("batch_size")]
    public int BatchSize { get; set; } = 50;

    /// <summary>Milliseconds to pause between writes within a batch.</summary>
    [JsonPropertyName("batch_delay_ms")]
    public int BatchDelayMs { get; set; } = 200;
}
