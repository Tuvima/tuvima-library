using MediaEngine.AI.Configuration;
using MediaEngine.AI.Features;
using MediaEngine.AI.Infrastructure;
using MediaEngine.AI.Whisper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.LocalAi;
using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaAiServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaAi(
        this IServiceCollection services,
        IConfigurationLoader configLoader)
    {
        var settings = configLoader.LoadAi<AiSettings>()
            ?? throw new InvalidOperationException(
                "config/ai.json is required and must contain valid AI settings.");
        var modelsDirectory = Environment.GetEnvironmentVariable("TUVIMA_MODELS_DIR");
        if (!string.IsNullOrEmpty(modelsDirectory))
        {
            settings.ModelsDirectory = modelsDirectory;
        }

        var runtimeDirectory = Environment.GetEnvironmentVariable("TUVIMA_AI_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtimeDirectory))
        {
            settings.NativeRuntimeDirectory = runtimeDirectory;
        }

        AiSettingsValidator.ValidateAndThrow(settings);
        services.AddSingleton<SharedAiRuntime>();
        var gpuDetector = new GpuBackendDetector(NullLogger<GpuBackendDetector>.Instance);
        var detected = gpuDetector.Detect();
        var benchmarkStore = new AiBenchmarkStateStore(settings, NullLogger<AiBenchmarkStateStore>.Instance);
        settings.HardwareProfile = benchmarkStore.LoadCurrent(detected.Backend, detected.GpuName);
        settings.ApplyEffectiveResourceProfile();
        services.AddSingleton(settings);
        services.AddSingleton<AiConfigurationService>();
        services.AddSingleton<ModelInventory>();
        services.AddSingleton<AiModelSelectionAdvisor>();
        services.AddSingleton<AiFeatureGate>();

        // The Local AI features are Official plugins: a feature is on exactly when its plugin is on, and the plugin
        // scheduler runs the scheduled passes (see AddTuvimaPlugins).
        services.AddSingleton<IAiFeatureSwitches, PluginAiFeatureSwitches>();
        services.AddSingleton<IVibeVocabularySource, PluginVibeVocabularySource>();
        services.AddSingleton<VibeBatchRunner>();
        services.AddSingleton<SeriesAlignmentRunner>();
        services.AddSingleton<DescriptionIntelligenceRunner>();
        services.AddSingleton(benchmarkStore);
        services.AddSingleton<IModelDownloadManager, ModelDownloadManager>();
        services.AddSingleton<IAiRuntimeStatus, AiRuntimeStatus>();
        services.AddSingleton<IModelLifecycleManager, ModelLifecycleManager>();
        services.AddTuvimaLocalAiEngineRoom();
        services.AddSingleton<WhisperInferenceService>();
        services.AddSingleton<IAudioTranscriptionService>(sp =>
            sp.GetRequiredService<WhisperInferenceService>());
        services.AddSingleton<AudioPreprocessor>();
        services.AddSingleton<ISmartLabeler, SmartLabeler>();
        services.AddSingleton<IMediaTypeAdvisor, MediaTypeAdvisor>();
        services.AddSingleton<ISeriesAligner, SeriesAligner>();
        services.AddSingleton<IVibeTagger, VibeTagger>();
        services.AddSingleton<ICoverArtHashService, CoverArtHashService>();
        services.AddSingleton<ITasteProfiler, TasteProfiler>();
        services.AddSingleton<IDescriptionIntelligenceService, DescriptionIntelligenceService>();
        services.AddSingleton(gpuDetector);
        services.AddSingleton<ResourceMonitorService>();
        services.AddSingleton<InteractiveRequestTracker>();
        services.AddSingleton<HardwareBenchmarkService>();
        return services;
    }
}
