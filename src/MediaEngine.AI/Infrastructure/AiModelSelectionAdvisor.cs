using MediaEngine.AI.Configuration;
using MediaEngine.Domain.Enums;

namespace MediaEngine.AI.Infrastructure;

/// <summary>Produces the one truthful runtime plan for the configured machine.</summary>
public sealed class AiModelSelectionAdvisor
{
    private readonly AiSettings _settings;
    private readonly ModelInventory? _inventory;
    private readonly IAiRuntimeStatus? _runtime;

    /// <summary>
    /// <paramref name="inventory"/> and <paramref name="runtime"/> supply the live facts about this machine (is the model
    /// file there, does the native runtime load). Without them those facts are reported as not ready, never assumed.
    /// </summary>
    public AiModelSelectionAdvisor(AiSettings settings, ModelInventory? inventory = null, IAiRuntimeStatus? runtime = null)
    {
        _settings = settings;
        _inventory = inventory;
        _runtime = runtime;
    }

    public AiExecutionPlan GetExecutionPlan()
    {
        var configured = Normalize(_settings.ResourceProfile);
        var recommended = GetRecommendedProfile();
        var constrained = GetRecommendedProfile() == AiResourceProfileNames.Essential;
        var advancedBlocked = configured == AiResourceProfileNames.Advanced
            && !_settings.HardwareProfile.AdvancedEligible;
        var effective = constrained ? AiResourceProfileNames.Essential : advancedBlocked ? recommended : configured;
        if (effective == AiResourceProfileNames.Advanced && !_settings.HardwareProfile.AdvancedEligible)
        {
            effective = AiResourceProfileNames.Standard;
        }

        var model = AiResourceProfileCatalog.CreateText(effective);
        return new AiExecutionPlan(
            configured,
            effective,
            recommended,
            model.CatalogKey ?? "",
            _settings.AudioPackEnabled,
            !advancedBlocked,
            advancedBlocked
                ? ["Advanced requires a successful current-machine benchmark meeting its throughput and memory gate."]
                : []);
    }

    public string GetRecommendedProfile()
    {
        if (_settings.HardwareProfile.AdvancedEligible)
        {
            return AiResourceProfileNames.Advanced;
        }

        var ram = _settings.HardwareProfile.AvailableRamMb;
        if (ram <= 0)
        {
            ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        }

        return ram < 8192 ? AiResourceProfileNames.Essential : AiResourceProfileNames.Standard;
    }

    public AiModelSelectionDecision GetDecision(AiModelRole role)
    {
        var definition = _settings.Models.GetByRole(role);
        var catalog = _settings.GetCatalogEntryForRole(role);
        var plan = GetExecutionPlan();
        var isAudio = role == AiModelRole.Audio;
        var enabled = !isAudio || plan.AudioPackEnabled;
        var warnings = new List<string>();
        if (isAudio && !plan.AudioPackEnabled)
        {
            warnings.Add("The optional Whisper feature pack is disabled.");
        }

        warnings.AddRange(plan.BlockingReasons);

        // Readiness is read from this machine, not assumed: the definition is complete, the native runtime loads, and the
        // model file is on disk. "Can enable" is whether this machine may use the model (so it can be offered for
        // download); the runtime and file facts decide whether work can run now (see AiFeatureGate).
        var configurationReady = catalog is not null
            && catalog.Readiness.ConfigurationReady
            && !string.IsNullOrWhiteSpace(definition.File)
            && !string.IsNullOrWhiteSpace(definition.DownloadUrl);
        var runtimeReady = enabled && _runtime is not null
            && (isAudio ? _runtime.AudioRuntimeLoads : _runtime.TextRuntimeLoads);
        var validated = _inventory?.GetState(role) is AiModelState.Ready or AiModelState.Loaded;
        if (enabled && catalog is not null && !runtimeReady)
        {
            warnings.Add(isAudio ? "The speech AI runtime is not installed." : "The AI runtime is not installed.");
        }

        var canEnable = enabled && catalog is not null
            && configurationReady
            && (!string.Equals(_settings.ResourceProfile, AiResourceProfileNames.Advanced, StringComparison.OrdinalIgnoreCase)
                || _settings.HardwareProfile.AdvancedEligible);
        var status = !enabled ? "disabled" : canEnable ? "ready" : "blocked";

        return new AiModelSelectionDecision(
            AiModelDefinitions.ToRoleKey(role),
            definition.CatalogKey,
            catalog?.DisplayName ?? definition.CatalogKey ?? "Unconfigured",
            catalog?.Family ?? "",
            catalog?.Provider ?? "",
            catalog?.License ?? "",
            catalog?.Runtime ?? "",
            status,
            catalog?.SelectionTier ?? "",
            catalog?.Validation.BenchmarkSuite ?? "",
            catalog?.SelectionRationale ?? "",
            isAudio ? "Optional audio feature pack" : $"{plan.EffectiveProfile} text profile",
            warnings,
            enabled,
            canEnable,
            false,
            catalog?.Quantization ?? "",
            catalog?.SizeMB ?? definition.SizeMB,
            catalog?.MemoryEnvelopeMB ?? definition.SizeMB,
            definition.ContextLength,
            catalog?.SourceUrl ?? "",
            !string.IsNullOrWhiteSpace(catalog?.Sha256),
            configurationReady,
            runtimeReady,
            validated,
            warnings);
    }

    public static bool HasCapability(AiModelCapabilities capabilities, string capability) => capability.ToLowerInvariant() switch
    {
        "text_input" => capabilities.TextInput,
        "audio_input" => capabilities.AudioInput,
        "image_input" => capabilities.ImageInput,
        "text_output" => capabilities.TextOutput,
        "structured_json" => capabilities.StructuredJson,
        "gbnf" => capabilities.Gbnf,
        "timestamp_segments" => capabilities.TimestampSegments,
        "word_timestamps" => capabilities.WordTimestamps,
        "sync_grade" => capabilities.SyncGrade,
        "multilingual" => capabilities.Multilingual,
        "cjk" => capabilities.Cjk,
        _ => false,
    };

    private static string Normalize(string? profile) => profile?.ToLowerInvariant() switch
    {
        AiResourceProfileNames.Essential => AiResourceProfileNames.Essential,
        AiResourceProfileNames.Advanced => AiResourceProfileNames.Advanced,
        _ => AiResourceProfileNames.Standard,
    };
}

public sealed record AiExecutionPlan(
    string ConfiguredProfile,
    string EffectiveProfile,
    string RecommendedProfile,
    string TextCatalogKey,
    bool AudioPackEnabled,
    bool ConfiguredProfileEligible,
    IReadOnlyList<string> BlockingReasons);

public sealed record AiModelSelectionDecision(
    string Role,
    string? CatalogKey,
    string DisplayName,
    string Family,
    string Provider,
    string License,
    string Runtime,
    string Status,
    string SelectionTier,
    string BenchmarkSuite,
    string Rationale,
    string Requirement,
    IReadOnlyList<string> Warnings,
    bool Enabled,
    bool CanEnable,
    bool Experimental,
    string Quantization,
    int SizeMB,
    int MemoryEnvelopeMB,
    int MaxContextLength,
    string SourceUrl,
    bool ChecksumConfigured,
    bool ConfigurationReady,
    bool RuntimeReady,
    bool Validated,
    IReadOnlyList<string> BlockingReasons);
