using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>
/// A local reading of the machine the Engine runs on, returned by <c>GET /system/resources</c> for the Settings
/// System summary. Nothing in it leaves the machine; it is read from the running process and the local drives.
/// </summary>
public sealed class SystemResourcesDto
{
    /// <summary>The Engine's version, as reported by <c>GET /system/status</c>.</summary>
    [JsonPropertyName("engine_version")]
    public string EngineVersion { get; set; } = string.Empty;

    /// <summary>When the Engine process started.</summary>
    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>How long the Engine process has been running, in whole seconds.</summary>
    [JsonPropertyName("uptime_seconds")]
    public long UptimeSeconds { get; set; }

    /// <summary>How much of the machine's processor the Engine is using, 0 to 100.</summary>
    [JsonPropertyName("processor_percent")]
    public double ProcessorPercent { get; set; }

    /// <summary>Memory the Engine process is holding, in megabytes.</summary>
    [JsonPropertyName("memory_used_mb")]
    public long MemoryUsedMb { get; set; }

    /// <summary>Memory available to the Engine, in megabytes, as the Engine's resource monitor reports it.</summary>
    [JsonPropertyName("memory_total_mb")]
    public long MemoryTotalMb { get; set; }

    /// <summary>
    /// The drives holding the library and the data store. A drive that holds both appears once. A location whose
    /// drive cannot be read (for example a network share) is left out.
    /// </summary>
    [JsonPropertyName("drives")]
    public List<SystemDriveDto> Drives { get; set; } = [];

    /// <summary>Size of the data store file in bytes, or null when the file cannot be read.</summary>
    [JsonPropertyName("data_store_size_bytes")]
    public long? DataStoreSizeBytes { get; set; }
}

/// <summary>Disk space on one drive that holds the library, the data store, or both.</summary>
public sealed class SystemDriveDto
{
    /// <summary>The drive's root, for example <c>C:\</c> or <c>/</c>.</summary>
    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    /// <summary>The drive's volume label; empty when it has none.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("free_bytes")]
    public long FreeBytes { get; set; }

    /// <summary>True when the library root is on this drive.</summary>
    [JsonPropertyName("holds_library")]
    public bool HoldsLibrary { get; set; }

    /// <summary>True when the data store file's folder is on this drive.</summary>
    [JsonPropertyName("holds_data_store")]
    public bool HoldsDataStore { get; set; }
}
