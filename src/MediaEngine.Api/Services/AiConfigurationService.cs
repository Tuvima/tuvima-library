using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services;

/// <summary>Single live authority for the Local AI settings. Which AI features run is decided by their plugins, not here.</summary>
public sealed class AiConfigurationService
{
    private readonly AiSettings _current;
    private readonly IConfigurationLoader _loader;
    private readonly object _sync = new();

    public AiConfigurationService(AiSettings current, IConfigurationLoader loader)
    {
        _current = current;
        _loader = loader;
    }

    public AiSettings Current => _current;

    public IReadOnlyList<AiConfigurationError> Save(AiSettings proposed)
    {
        proposed.HardwareProfile = _current.HardwareProfile;
        var errors = AiSettingsValidator.Validate(proposed).ToList();
        if (proposed.ModelsDirectory != _current.ModelsDirectory || proposed.NativeRuntimeDirectory != _current.NativeRuntimeDirectory)
        {
            errors.Add(new AiConfigurationError("models_directory", "Storage paths require an Engine restart. Update the configured storage paths or environment before restarting."));
        }
        if (string.Equals(proposed.ResourceProfile, AiResourceProfileNames.Advanced, StringComparison.OrdinalIgnoreCase)
            && !_current.HardwareProfile.AdvancedEligible)
        {
            errors.Add(new AiConfigurationError(
                "resource_profile",
                "Advanced requires a successful benchmark for the current machine."));
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        lock (_sync)
        {
            _current.DevSkipDownload = proposed.DevSkipDownload;
            _current.ModelsDirectory = proposed.ModelsDirectory;
            _current.NativeRuntimeDirectory = proposed.NativeRuntimeDirectory;
            _current.ResourceProfile = proposed.ResourceProfile;
            _current.ApplyEffectiveResourceProfile();
            _current.AudioPackEnabled = proposed.AudioPackEnabled;
            _current.IdleUnloadSeconds = proposed.IdleUnloadSeconds;
            _current.InferenceTimeoutSeconds = proposed.InferenceTimeoutSeconds;
            _current.MinimumFreeDiskMB = proposed.MinimumFreeDiskMB;
            _current.EnrichmentBatchSize = proposed.EnrichmentBatchSize;
            // Environment overrides describe this process, not portable persisted configuration.
            var persisted = JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(_current, MediaEngineJson.Web), MediaEngineJson.Web)!;
            var configured = _loader.LoadAi<AiSettings>();
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TUVIMA_MODELS_DIR")))
            {
                persisted.ModelsDirectory = configured?.ModelsDirectory ?? new AiSettings().ModelsDirectory;
            }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TUVIMA_AI_RUNTIME_DIR")))
            {
                persisted.NativeRuntimeDirectory = configured?.NativeRuntimeDirectory ?? "";
            }
            _loader.SaveAi(persisted);
        }

        return [];
    }
}
