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
