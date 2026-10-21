using System.Text.Json.Serialization;
using MediaEngine.Domain.Enums;

namespace MediaEngine.AI.Configuration;

/// <summary>
/// Typed configuration model for <c>config/ai.json</c>.
/// </summary>
public sealed class AiSettings
{
    /// <summary>Dev-only flag to skip model download requirement. Not exposed in Dashboard.</summary>
    [JsonPropertyName("dev_skip_download")]
    public bool DevSkipDownload { get; set; }

    /// <summary>Directory where model files are stored (Docker: /models).</summary>
    [JsonPropertyName("models_directory")]
    public string ModelsDirectory { get; set; } = "/models";

    /// <summary>Shared native installation root; empty uses the local application-data root.
    /// The explicit value 'bundled' selects a deployment built with bundled native assets.</summary>
    [JsonPropertyName("native_runtime_directory")]
    public string NativeRuntimeDirectory { get; set; } = DefaultNativeRuntimeDirectory;

#if TUVIMA_BUNDLED_AI_RUNTIME
    public const string DefaultNativeRuntimeDirectory = "bundled";
#else
    public const string DefaultNativeRuntimeDirectory = "";
#endif

    /// <summary>Seconds of idle time before auto-unloading a model from memory.</summary>
    [JsonPropertyName("idle_unload_seconds")]
    public int IdleUnloadSeconds { get; set; } = 300;

    /// <summary>Maximum seconds to wait for an inference call before timeout.</summary>
    [JsonPropertyName("inference_timeout_seconds")]
    public int InferenceTimeoutSeconds { get; set; } = 60;

    /// <summary>Logical CPU cores that local inference must leave available to the Engine and OS.</summary>
    [JsonPropertyName("reserved_cpu_cores")]
    public int ReservedCpuCores { get; set; } = 2;

    /// <summary>Seconds without user-facing Engine traffic before background inference may start.</summary>
    [JsonPropertyName("background_quiet_seconds")]
    public int BackgroundQuietSeconds { get; set; } = 10;

    /// <summary>Free disk space retained after a model download completes.</summary>
    [JsonPropertyName("minimum_free_disk_mb")]
    public int MinimumFreeDiskMB { get; set; } = 1024;

    public int ResolveInferenceThreads(AiModelRole role)
    {
        var configured = Math.Max(1, Models.GetByRole(role).Threads);
        var proportionalReserve = Math.Max(2, (int)Math.Ceiling(Environment.ProcessorCount * 0.25));
        var reserve = Math.Max(ReservedCpuCores, proportionalReserve);
        var available = Math.Max(1, Environment.ProcessorCount - reserve);
        return Math.Min(configured, available);
    }

    /// <summary>The single text resource profile selected for this machine.</summary>
    private string _resourceProfile = AiResourceProfileNames.Standard;

    [JsonPropertyName("resource_profile")]
    public string ResourceProfile
    {
        get => _resourceProfile;
        set
        {
            _resourceProfile = value;
            Models = AiResourceProfileCatalog.CreateDefinitions(value);
        }
    }

    /// <summary>Whisper is an explicit optional feature pack and is never part of basic setup.</summary>
    [JsonPropertyName("audio_pack_enabled")]
    public bool AudioPackEnabled { get; set; }

    /// <summary>
    /// Runtime definitions are code-owned. Every logical text workload resolves to
    /// the one artifact selected by <see cref="ResourceProfile"/>.
    /// </summary>
    [JsonIgnore]
    public AiModelDefinitions Models { get; set; } =
        AiResourceProfileCatalog.CreateDefinitions(AiResourceProfileNames.Standard);

    [JsonIgnore]
    public string EffectiveResourceProfile
    {
        get
        {
            var availableRam = HardwareProfile.AvailableRamMb > 0
                ? HardwareProfile.AvailableRamMb
                : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            if (availableRam < 8192)
            {
                return AiResourceProfileNames.Essential;
            }

            if (string.Equals(ResourceProfile, AiResourceProfileNames.Advanced, StringComparison.OrdinalIgnoreCase)
                && !HardwareProfile.AdvancedEligible)
            {
                return AiResourceProfileNames.Standard;
            }

            return AiResourceProfileNames.IsSupported(ResourceProfile)
                ? ResourceProfile.ToLowerInvariant()
                : AiResourceProfileNames.Standard;
        }
    }

    public void ApplyEffectiveResourceProfile() =>
        Models = AiResourceProfileCatalog.CreateDefinitions(EffectiveResourceProfile);

    /// <summary>Reusable catalog of current, candidate, experimental, and escalation models.</summary>
    [JsonIgnore]
    public Dictionary<string, AiModelCatalogEntry> ModelCatalog { get; set; } = AiModelCatalogDefaults.CreateCatalog();

    /// <summary>Small-first validation gates for each functional model role.</summary>
    [JsonIgnore]
    public Dictionary<string, AiRoleRequirement> RoleRequirements { get; set; } = AiModelCatalogDefaults.CreateRoleRequirements();

    /// <summary>Number of entities to process per batch run in background enrichment services.</summary>
    [JsonPropertyName("enrichment_batch_size")]
    public int EnrichmentBatchSize { get; set; } = 10;

    /// <summary>How much Local AI each plugin may use per rolling hour, and how long a plugin request may wait for a turn.</summary>
    [JsonPropertyName("plugin_budget")]
    public AiPluginBudget PluginBudget { get; set; } = new();

    /// <summary>Per-plugin replacements for <see cref="PluginBudget"/>, keyed by plugin id. Unset fields keep the default.</summary>
    [JsonPropertyName("plugin_budget_overrides")]
    public Dictionary<string, AiPluginBudgetOverride> PluginBudgetOverrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The budget that applies to one plugin: its override where it has one, otherwise the default.</summary>
    public AiPluginBudget GetPluginBudget(string pluginId)
    {
        if (!PluginBudgetOverrides.TryGetValue(pluginId, out var overrides))
        {
            return PluginBudget;
        }

        return new AiPluginBudget
        {
            RequestsPerHour = overrides.RequestsPerHour ?? PluginBudget.RequestsPerHour,
            BusySecondsPerHour = overrides.BusySecondsPerHour ?? PluginBudget.BusySecondsPerHour,
            MaxWaitSeconds = overrides.MaxWaitSeconds ?? PluginBudget.MaxWaitSeconds,
        };
    }

    /// <summary>Machine-local runtime state. Never serialized into deployable AI configuration.</summary>
    [JsonIgnore]
    public HardwareProfile HardwareProfile { get; set; } = new();

    public AiModelCatalogEntry? GetCatalogEntryForRole(Domain.Enums.AiModelRole role)
    {
        var definition = Models.GetByRole(role);
        if (!string.IsNullOrWhiteSpace(definition.CatalogKey)
            && ModelCatalog.TryGetValue(definition.CatalogKey, out var configured))
        {
            return configured;
        }

        return ModelCatalog.Values.FirstOrDefault(entry =>
            string.Equals(entry.File, definition.File, StringComparison.OrdinalIgnoreCase));
    }

    public AiRoleRequirement? GetRequirementForRole(Domain.Enums.AiModelRole role)
    {
        var key = AiModelDefinitions.ToRoleKey(role);
        return RoleRequirements.TryGetValue(key, out var requirement) ? requirement : null;
    }
}

/// <summary>Model definitions by role (text_fast, text_quality, audio).</summary>
public sealed class AiModelDefinitions
{
    [JsonPropertyName("text_fast")]
    public AiModelDefinition TextFast { get; set; } = new()
    {
        CatalogKey = "qwen3_0_6b_q8",
        Description = "On-demand text tasks (search parsing, TL;DR, Why Factor)",
        File = "Qwen3-0.6B-Q8_0.gguf",
        DownloadUrl = "https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q8_0.gguf",
        Sha256 = "9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031",
        SizeMB = 610,
        ContextLength = 4096,
        MaxTokens = 256,
        Temperature = 0.1,
    };

    [JsonPropertyName("text_quality")]
    public AiModelDefinition TextQuality { get; set; } = new()
    {
        CatalogKey = "qwen3_1_7b_q5",
        Description = "Batch/scheduled tasks (ingestion manifest, vibe tags, disambiguation)",
        File = "Qwen3-1.7B-Q5_K_M.gguf",
        DownloadUrl = "https://huggingface.co/unsloth/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-Q5_K_M.gguf",
        Sha256 = "b0949de5b2e06cbed6aa96517f9bd8afb334584b6f95ee83479292ff4bdd8ed3",
        SizeMB = 1260,
        ContextLength = 8192,
        MaxTokens = 512,
        Temperature = 0.1,
    };

    [JsonPropertyName("text_scholar")]
    public AiModelDefinition TextScholar { get; set; } = new()
    {
        Description = "Deep enrichment (description intelligence, complex analysis) — scheduled/overnight",
        CatalogKey = "qwen3_4b_q4",
        File = "Qwen3-4B-Q4_K_M.gguf",
        DownloadUrl = "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
        Sha256 = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
        SizeMB = 2382,
        ContextLength = 16384,
        MaxTokens = 1024,
        Temperature = 0.1,
    };

    [JsonPropertyName("audio")]
    public AiModelDefinition Audio { get; set; } = new()
    {
        CatalogKey = "whisper_medium",
        Description = "Whisper transcription and language detection",
        File = "ggml-medium.bin",
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin",
        Sha256 = "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
        SizeMB = 1463,
        Language = "auto",
    };

    [JsonPropertyName("text_cjk")]
    public AiModelDefinition TextCjk { get; set; } = new()
    {
        CatalogKey = "qwen3_4b_q4",
        Description = "Multilingual model with strong CJK support for Chinese, Japanese, and Korean content analysis",
        File = "Qwen3-4B-Q4_K_M.gguf",
        DownloadUrl = "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
        Sha256 = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
        SizeMB = 2382,
        ContextLength = 8192,
        MaxTokens = 512,
        Temperature = 0.1,
    };

    /// <summary>Get the definition for a given role.</summary>
    public AiModelDefinition GetByRole(Domain.Enums.AiModelRole role) => role switch
    {
        Domain.Enums.AiModelRole.TextFast => TextFast,
        Domain.Enums.AiModelRole.TextQuality => TextQuality,
        Domain.Enums.AiModelRole.TextScholar => TextScholar,
        Domain.Enums.AiModelRole.Audio => Audio,
        Domain.Enums.AiModelRole.TextCjk => TextCjk,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static string ToRoleKey(Domain.Enums.AiModelRole role) => role switch
    {
        Domain.Enums.AiModelRole.TextFast => "text_fast",
        Domain.Enums.AiModelRole.TextQuality => "text_quality",
        Domain.Enums.AiModelRole.TextScholar => "text_scholar",
        Domain.Enums.AiModelRole.Audio => "audio",
        Domain.Enums.AiModelRole.TextCjk => "text_cjk",
        _ => role.ToString(),
    };
}

/// <summary>Definition of a single AI model.</summary>
public sealed class AiModelDefinition
{
    [JsonPropertyName("catalog_key")]
    public string? CatalogKey { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("size_mb")]
    public int SizeMB { get; set; }

    [JsonPropertyName("context_length")]
    public int ContextLength { get; set; } = 2048;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 256;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.1;

    [JsonPropertyName("gpu_layers")]
    public int GpuLayers { get; set; }

    [JsonPropertyName("threads")]
    public int Threads { get; set; } = 4;

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("translate")]
    public bool Translate { get; set; }
}

public sealed class AiModelCatalogEntry
{
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("family")]
    public string Family { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("license")]
    public string License { get; set; } = "";

    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "candidate";

    [JsonPropertyName("selection_tier")]
    public string SelectionTier { get; set; } = "candidate";

    [JsonPropertyName("intended_roles")]
    public List<string> IntendedRoles { get; set; } = [];

    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("source_url")]
    public string SourceUrl { get; set; } = "";

    [JsonPropertyName("size_mb")]
    public int SizeMB { get; set; }

    [JsonPropertyName("parameters_b")]
    public double ParametersB { get; set; }

    [JsonPropertyName("effective_parameters_b")]
    public double? EffectiveParametersB { get; set; }

    [JsonPropertyName("quantization")]
    public string Quantization { get; set; } = "";

    [JsonPropertyName("context_length")]
    public int ContextLength { get; set; }

    [JsonPropertyName("memory_envelope_mb")]
    public int MemoryEnvelopeMB { get; set; }

    [JsonPropertyName("max_context_length")]
    public int MaxContextLength { get; set; }

    [JsonPropertyName("experimental")]
    public bool Experimental { get; set; }

    [JsonPropertyName("compatibility")]
    public AiModelCompatibility Compatibility { get; set; } = new();

    [JsonPropertyName("readiness")]
    public AiModelReadiness Readiness { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public AiModelCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("validation")]
    public AiModelValidationProfile Validation { get; set; } = new();

    [JsonPropertyName("selection_rationale")]
    public string SelectionRationale { get; set; } = "";

    [JsonPropertyName("integration_notes")]
    public string IntegrationNotes { get; set; } = "";
}

public sealed class AiModelCapabilities
{
    [JsonPropertyName("text_input")]
    public bool TextInput { get; set; }

    [JsonPropertyName("audio_input")]
    public bool AudioInput { get; set; }

    [JsonPropertyName("image_input")]
    public bool ImageInput { get; set; }

    [JsonPropertyName("text_output")]
    public bool TextOutput { get; set; } = true;

    [JsonPropertyName("structured_json")]
    public bool StructuredJson { get; set; }

    [JsonPropertyName("gbnf")]
    public bool Gbnf { get; set; }

    [JsonPropertyName("timestamp_segments")]
    public bool TimestampSegments { get; set; }

    [JsonPropertyName("word_timestamps")]
    public bool WordTimestamps { get; set; }

    [JsonPropertyName("sync_grade")]
    public bool SyncGrade { get; set; }

    [JsonPropertyName("multilingual")]
    public bool Multilingual { get; set; }

    [JsonPropertyName("cjk")]
    public bool Cjk { get; set; }

    [JsonPropertyName("experimental_multimodal")]
    public bool ExperimentalMultimodal { get; set; }

    [JsonPropertyName("embedding_output")]
    public bool EmbeddingOutput { get; set; }

    [JsonPropertyName("function_calling")]
    public bool FunctionCalling { get; set; }

    [JsonPropertyName("tool_calling")]
    public bool ToolCalling { get; set; }
}

public sealed class AiModelCompatibility
{
    [JsonPropertyName("supported_backends")]
    public List<string> SupportedBackends { get; set; } = [];

    [JsonPropertyName("minimum_runtime_version")]
    public string? MinimumRuntimeVersion { get; set; }

    [JsonPropertyName("requires_mmproj")]
    public bool RequiresMmproj { get; set; }

    [JsonPropertyName("requires_audio_encoder")]
    public bool RequiresAudioEncoder { get; set; }
}

public sealed class AiModelReadiness
{
    [JsonPropertyName("configuration_ready")]
    public bool ConfigurationReady { get; set; }

    [JsonPropertyName("runtime_ready")]
    public bool RuntimeReady { get; set; }

    [JsonPropertyName("validated")]
    public bool Validated { get; set; }

    [JsonPropertyName("blocking_reasons")]
    public List<string> BlockingReasons { get; set; } = [];
}

public sealed class AiModelValidationProfile
{
    [JsonPropertyName("target_warm_latency_ms")]
    public int TargetWarmLatencyMs { get; set; }

    [JsonPropertyName("max_warm_latency_ms")]
    public int MaxWarmLatencyMs { get; set; }

    [JsonPropertyName("min_json_validity_rate")]
    public double MinJsonValidityRate { get; set; } = 0.99;

    [JsonPropertyName("min_task_pass_rate")]
    public double MinTaskPassRate { get; set; } = 0.9;

    [JsonPropertyName("max_hallucination_rate")]
    public double MaxHallucinationRate { get; set; } = 0.02;

    [JsonPropertyName("max_wer")]
    public double? MaxWer { get; set; }

    [JsonPropertyName("max_timestamp_drift_ms")]
    public int? MaxTimestampDriftMs { get; set; }

    [JsonPropertyName("benchmark_suite")]
    public string BenchmarkSuite { get; set; } = "";
}

public sealed class AiRoleRequirement
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("selection_policy")]
    public string SelectionPolicy { get; set; } = "Use the smallest model that passes all gates.";

    [JsonPropertyName("required_capabilities")]
    public List<string> RequiredCapabilities { get; set; } = [];

    [JsonPropertyName("preferred_catalog_keys")]
    public List<string> PreferredCatalogKeys { get; set; } = [];

    [JsonPropertyName("fallback_catalog_keys")]
    public List<string> FallbackCatalogKeys { get; set; } = [];

    [JsonPropertyName("max_default_size_mb")]
    public int MaxDefaultSizeMB { get; set; }

    [JsonPropertyName("target_warm_latency_ms")]
    public int TargetWarmLatencyMs { get; set; }

    [JsonPropertyName("max_background_latency_ms")]
    public int MaxBackgroundLatencyMs { get; set; }

    [JsonPropertyName("min_json_validity_rate")]
    public double MinJsonValidityRate { get; set; } = 0.99;

    [JsonPropertyName("min_task_pass_rate")]
    public double MinTaskPassRate { get; set; } = 0.9;

    [JsonPropertyName("benchmark_suite")]
    public string BenchmarkSuite { get; set; } = "";

    [JsonPropertyName("memory_envelope_mb")]
    public int MemoryEnvelopeMB { get; set; }

    [JsonPropertyName("max_context_length")]
    public int MaxContextLength { get; set; }

    [JsonPropertyName("experimental_allowed")]
    public bool ExperimentalAllowed { get; set; }
}

/// <summary>A plugin's Local AI allowance. Counted over the last rolling hour from the durable usage records.</summary>
public sealed class AiPluginBudget
{
    /// <summary>Requests a plugin may make per rolling hour.</summary>
    [JsonPropertyName("requests_per_hour")]
    public int RequestsPerHour { get; set; } = 120;

    /// <summary>Seconds of running time a plugin may use per rolling hour.</summary>
    [JsonPropertyName("busy_seconds_per_hour")]
    public int BusySecondsPerHour { get; set; } = 600;

    /// <summary>Seconds a plugin request may wait for its turn before it times out.</summary>
    [JsonPropertyName("max_wait_seconds")]
    public int MaxWaitSeconds { get; set; } = 120;
}

/// <summary>A per-plugin change to the default allowance; a missing field keeps the default.</summary>
public sealed class AiPluginBudgetOverride
{
    [JsonPropertyName("requests_per_hour")]
    public int? RequestsPerHour { get; set; }

    [JsonPropertyName("busy_seconds_per_hour")]
    public int? BusySecondsPerHour { get; set; }

    [JsonPropertyName("max_wait_seconds")]
    public int? MaxWaitSeconds { get; set; }
}

/// <summary>Per-category controlled vocabulary for vibe tagging. The default words are the vibe tags plugin's starting settings.</summary>
public sealed class AiVibeVocabulary
{
    [JsonPropertyName("books")]
    public List<string> Books { get; set; } =
    [
        "page-turner", "slow-burn", "meditative", "dark", "uplifting", "haunting", "heartwarming", "atmospheric", "cozy",
        "gritty", "lyrical", "philosophical", "whimsical", "witty", "epic", "intimate", "cerebral"
    ];

    [JsonPropertyName("movies_tv")]
    public List<string> MoviesTv { get; set; } =
    [
        "slow-paced", "action-packed", "meditative", "dark", "uplifting", "haunting", "heartwarming", "tense", "nostalgic",
        "atmospheric", "gritty", "dreamlike", "visually-stunning", "humorous", "quirky", "epic", "intimate"
    ];

    [JsonPropertyName("music")]
    public List<string> Music { get; set; } =
    [
        "driving", "mellow", "energetic", "hypnotic", "dark", "uplifting", "melancholic", "nostalgic", "haunting",
        "ambient", "lush", "ethereal", "warm", "playful", "soulful", "groovy", "dreamy", "intimate"
    ];

    [JsonPropertyName("comics")]
    public List<string> Comics { get; set; } =
    [
        "action-packed", "meditative", "dark", "uplifting", "haunting", "nostalgic", "atmospheric", "gritty", "noir",
        "surreal", "colorful", "whimsical", "humorous", "epic", "intimate"
    ];

    /// <summary>Get vocabulary for a media category string.</summary>
    public IReadOnlyList<string> GetForCategory(string category) => category.ToLowerInvariant() switch
    {
        "books" or "audiobooks" => Books,
        "movies" or "tv" or "movies_tv" => MoviesTv,
        "music" => Music,
        "comics" => Comics,
        _ => Books, // default fallback
    };
}
