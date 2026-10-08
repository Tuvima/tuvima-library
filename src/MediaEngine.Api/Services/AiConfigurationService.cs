using Cronos;
using MediaEngine.AI.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using System.Text.Json;

namespace MediaEngine.Api.Services;

/// <summary>Single live authority for AI configuration and schedule reload notifications.</summary>
public sealed class AiConfigurationService : IDisposable
{
    private readonly AiSettings _current;
    private readonly IConfigurationLoader _loader;
    private readonly object _sync = new();
    private CancellationTokenSource _changed = new();

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
        ValidateCron(proposed.Scheduling.VibeBatchCron, "scheduling.vibe_batch_cron", errors);
        ValidateCron(proposed.Scheduling.SeriesCheckCron, "scheduling.series_check_cron", errors);
        ValidateCron(proposed.Scheduling.DescriptionIntelligenceCron, "scheduling.description_intelligence_cron", errors);
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
            _current.MaxConcurrentInferences = proposed.MaxConcurrentInferences;
            _current.MinimumFreeDiskMB = proposed.MinimumFreeDiskMB;
            _current.Features = proposed.Features;
            _current.VibeVocabulary = proposed.VibeVocabulary;
            _current.Scheduling = proposed.Scheduling;
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

            var previous = _changed;
            _changed = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
        }

        return [];
    }

    public async Task WaitForDelayOrChangeAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        CancellationToken changedToken;
        lock (_sync)
        {
            changedToken = _changed.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, changedToken);
        try
        {
            await Task.Delay(delay, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (changedToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // A saved schedule intentionally wakes the worker so it can recompute.
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _changed.Cancel();
            _changed.Dispose();
        }
    }

    private static void ValidateCron(string value, string path, ICollection<AiConfigurationError> errors)
    {
        try
        {
            _ = CronExpression.Parse(value);
        }
        catch (CronFormatException ex)
        {
            errors.Add(new AiConfigurationError(path, ex.Message));
        }
    }
}
