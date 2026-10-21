using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Ai;

public sealed class AiHealthStatusDto
{
    [JsonPropertyName("models")]
    public List<AiModelStatusDto> Models { get; set; } = [];

    [JsonPropertyName("memoryUsedMB")]
    public int MemoryUsedMB { get; set; }

    [JsonPropertyName("memoryLimitMB")]
    public int MemoryLimitMB { get; set; }

    [JsonPropertyName("gpuAvailable")]
    public bool GpuAvailable { get; set; }

    [JsonPropertyName("memoryProfile")]
    public string MemoryProfile { get; set; } = "unknown";

    [JsonPropertyName("isReady")]
    public bool IsReady { get; set; }
}

public sealed class AiModelStatusDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("roleName")]
    public string RoleName { get; set; } = "";

    [JsonPropertyName("supported")]
    public bool Supported { get; set; } = true;

    [JsonPropertyName("modelType")]
    public string ModelType { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "NotDownloaded";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("modelFile")]
    public string ModelFile { get; set; } = "";

    [JsonPropertyName("sizeMB")]
    public int SizeMB { get; set; }

    [JsonPropertyName("downloadUrlHost")]
    public string? DownloadUrlHost { get; set; }

    [JsonPropertyName("downloadProgressPercent")]
    public int DownloadProgressPercent { get; set; }

    [JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("loaded")]
    public bool Loaded { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("memoryFootprintMB")]
    public int MemoryFootprintMB { get; set; }

    [JsonPropertyName("requiredHardwareTier")]
    public string RequiredHardwareTier { get; set; } = "low";

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("catalogKey")]
    public string? CatalogKey { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("family")]
    public string Family { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("license")]
    public string License { get; set; } = "";

    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = "";

    [JsonPropertyName("selectionTier")]
    public string SelectionTier { get; set; } = "";

    [JsonPropertyName("selectionStatus")]
    public string SelectionStatus { get; set; } = "";

    [JsonPropertyName("selectionRationale")]
    public string SelectionRationale { get; set; } = "";

    [JsonPropertyName("roleRequirement")]
    public string RoleRequirement { get; set; } = "";

    [JsonPropertyName("benchmarkSuite")]
    public string BenchmarkSuite { get; set; } = "";

    [JsonPropertyName("validationWarnings")]
    public List<string> ValidationWarnings { get; set; } = [];

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("diskStatus")]
    public string DiskStatus { get; set; } = "unknown";

    [JsonPropertyName("diskSizeMB")]
    public long DiskSizeMB { get; set; }

    [JsonPropertyName("memoryEnvelopeMB")]
    public int MemoryEnvelopeMB { get; set; }

    [JsonPropertyName("quantization")]
    public string Quantization { get; set; } = "";

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = "";

    [JsonPropertyName("checksumStatus")]
    public string ChecksumStatus { get; set; } = "unknown";

    [JsonPropertyName("configurationReady")]
    public bool ConfigurationReady { get; set; }

    [JsonPropertyName("runtimeReady")]
    public bool RuntimeReady { get; set; }

    [JsonPropertyName("validated")]
    public bool Validated { get; set; }

    [JsonPropertyName("canOperate")]
    public bool CanOperate { get; set; } = true;

    [JsonPropertyName("experimental")]
    public bool Experimental { get; set; }

    [JsonPropertyName("blockingReasons")]
    public List<string> BlockingReasons { get; set; } = [];
}

public sealed class AiConfigDto
{
    [JsonPropertyName("dev_skip_download")]
    public bool DevSkipDownload { get; set; }

    [JsonPropertyName("models_directory")]
    public string ModelsDirectory { get; set; } = "/models";

    [JsonPropertyName("native_runtime_directory")]
    public string NativeRuntimeDirectory { get; set; } = "";

    [JsonPropertyName("idle_unload_seconds")]
    public int IdleUnloadSeconds { get; set; } = 300;

    [JsonPropertyName("inference_timeout_seconds")]
    public int InferenceTimeoutSeconds { get; set; } = 60;

    [JsonPropertyName("minimum_free_disk_mb")]
    public int MinimumFreeDiskMB { get; set; } = 1024;

    [JsonPropertyName("resource_profile")]
    public string ResourceProfile { get; set; } = "standard";

    [JsonPropertyName("effective_resource_profile")]
    public string EffectiveResourceProfile { get; set; } = "standard";

    [JsonPropertyName("audio_pack_enabled")]
    public bool AudioPackEnabled { get; set; }

    [JsonPropertyName("enrichment_batch_size")]
    public int EnrichmentBatchSize { get; set; } = 10;

    /// <summary>How much Local AI each plugin may use per rolling hour, unless it has its own allowance.</summary>
    [JsonPropertyName("plugin_budget")]
    public AiPluginBudgetDto PluginBudget { get; set; } = new();

    /// <summary>Allowances that replace the default for single plugins, by plugin id.</summary>
    [JsonPropertyName("plugin_budget_overrides")]
    public Dictionary<string, AiPluginBudgetOverrideDto> PluginBudgetOverrides { get; set; } = new(StringComparer.Ordinal);

}

/// <summary>
/// Hardware profile returned by GET /ai/profile and POST /ai/benchmark.
/// </summary>
public sealed class HardwareProfileDto
{
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "not_run";

    [JsonPropertyName("tier")]
    public string Tier { get; set; } = "auto";

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "cpu";

    [JsonPropertyName("gpu_name")]
    public string? GpuName { get; set; }

    [JsonPropertyName("tokens_per_second")]
    public double? TokensPerSecond { get; set; }

    [JsonPropertyName("available_ram_mb")]
    public long AvailableRamMb { get; set; }

    [JsonPropertyName("benchmarked_at")]
    public DateTime? BenchmarkedAt { get; set; }

    [JsonPropertyName("machine_fingerprint")]
    public string? MachineFingerprint { get; set; }

    [JsonPropertyName("benchmark_model")]
    public string? BenchmarkModel { get; set; }

    [JsonPropertyName("failure_code")]
    public string? FailureCode { get; set; }

    [JsonPropertyName("failure_message")]
    public string? FailureMessage { get; set; }

    [JsonPropertyName("advanced_eligible")]
    public bool AdvancedEligible { get; set; }
}

/// <summary>
/// Live resource snapshot returned by GET /ai/resources.
/// </summary>
public sealed class ResourceSnapshotDto
{
    [JsonPropertyName("total_ram_mb")]
    public long TotalRamMb { get; set; }

    [JsonPropertyName("free_ram_mb")]
    public long FreeRamMb { get; set; }

    [JsonPropertyName("engine_ram_mb")]
    public long EngineRamMb { get; set; }

    [JsonPropertyName("cpu_pressure")]
    public double CpuPressure { get; set; }

    [JsonPropertyName("transcoding_active")]
    public bool TranscodingActive { get; set; }
}

/// <summary>
/// AI enrichment queue progress returned by GET /ai/enrichment/progress.
/// </summary>
public sealed class EnrichmentProgressDto
{
    [JsonPropertyName("pending_count")]
    public int PendingCount { get; set; }

    [JsonPropertyName("completed_count")]
    public int CompletedCount { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>A plugin's Local AI allowance, counted over the last rolling hour.</summary>
public sealed class AiPluginBudgetDto
{
    [JsonPropertyName("requests_per_hour")]
    public int RequestsPerHour { get; set; } = 120;

    [JsonPropertyName("busy_seconds_per_hour")]
    public int BusySecondsPerHour { get; set; } = 600;

    [JsonPropertyName("max_wait_seconds")]
    public int MaxWaitSeconds { get; set; } = 120;
}

/// <summary>A change to the default allowance for one plugin; a missing field keeps the default.</summary>
public sealed class AiPluginBudgetOverrideDto
{
    [JsonPropertyName("requests_per_hour")]
    public int? RequestsPerHour { get; set; }

    [JsonPropertyName("busy_seconds_per_hour")]
    public int? BusySecondsPerHour { get; set; }

    [JsonPropertyName("max_wait_seconds")]
    public int? MaxWaitSeconds { get; set; }
}

/// <summary>One line of the Usage page: what one caller asked of one ability over the period.</summary>
public sealed class AiUsageRowDto
{
    /// <summary>person, ingestion, library or plugin.</summary>
    [JsonPropertyName("caller_kind")]
    public string CallerKind { get; set; } = "";

    [JsonPropertyName("caller_id")]
    public string CallerId { get; set; } = "";

    /// <summary>The plugin's name when the caller is a plugin that is installed, otherwise the key.</summary>
    [JsonPropertyName("caller_name")]
    public string CallerName { get; set; } = "";

    /// <summary>The ability in plain words, for example "Write text (quick)".</summary>
    [JsonPropertyName("ability")]
    public string Ability { get; set; } = "";

    [JsonPropertyName("requests")]
    public int Requests { get; set; }

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; set; }

    [JsonPropertyName("busy_seconds")]
    public double BusySeconds { get; set; }

    [JsonPropertyName("last_used_at")]
    public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>A plugin's standing against its hourly allowance, shown beside its usage.</summary>
public sealed class AiPluginBudgetStandingDto
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("plugin_name")]
    public string PluginName { get; set; } = "";

    [JsonPropertyName("requests_this_hour")]
    public int RequestsThisHour { get; set; }

    [JsonPropertyName("requests_per_hour")]
    public int RequestsPerHour { get; set; }

    [JsonPropertyName("busy_seconds_this_hour")]
    public double BusySecondsThisHour { get; set; }

    [JsonPropertyName("busy_seconds_per_hour")]
    public int BusySecondsPerHour { get; set; }

    [JsonPropertyName("over_budget")]
    public bool OverBudget { get; set; }
}

/// <summary>Response for <c>GET /ai/usage</c>.</summary>
public sealed class AiUsageResponse
{
    [JsonPropertyName("since")]
    public DateTimeOffset Since { get; set; }

    [JsonPropertyName("days")]
    public int Days { get; set; }

    [JsonPropertyName("rows")]
    public List<AiUsageRowDto> Rows { get; set; } = [];

    [JsonPropertyName("plugins")]
    public List<AiPluginBudgetStandingDto> Plugins { get; set; } = [];
}
