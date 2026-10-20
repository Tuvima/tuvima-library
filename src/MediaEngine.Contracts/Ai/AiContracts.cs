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

/// <summary>What one Local AI feature is allowed to do and where its data goes, in plain words written by the Engine.</summary>
public sealed class AiFeatureInfoDto
{
    [JsonPropertyName("allowed_to")]
    public List<string> AllowedTo { get; set; } = [];

    /// <summary>Where the feature sends data; null when nothing leaves this machine.</summary>
    [JsonPropertyName("sends_data_to")]
    public string? SendsDataTo { get; set; }
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

    [JsonPropertyName("features")]
    public Dictionary<string, bool> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Read-only: the plain words the Engine uses for each feature, keyed like <see cref="Features"/>. Filled by
    /// <c>GET /ai/config</c>; ignored when saving.
    /// </summary>
    [JsonPropertyName("feature_info")]
    public Dictionary<string, AiFeatureInfoDto> FeatureInfo { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Save-only: for each feature switched from off to on, the <c>statement_hash</c> of the approval the person just
    /// confirmed (feature key to hash). Without it a newly switched-on feature is refused with an approval-required 409.
    /// </summary>
    [JsonPropertyName("approvals")]
    public Dictionary<string, string> Approvals { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("vibe_vocabulary")]
    public Dictionary<string, List<string>> VibeVocabulary { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("scheduling")]
    public Dictionary<string, object?> Scheduling { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("enrichment_batch_size")]
    public int EnrichmentBatchSize { get; set; } = 10;

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
