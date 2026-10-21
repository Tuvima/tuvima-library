using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services;

/// <summary>Downloads only the selected text profile and an explicitly enabled audio pack.</summary>
public sealed class ModelAutoDownloadService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private readonly IModelDownloadManager _downloadManager;
    private readonly AiSettings _settings;
    private readonly IAiFeatureSwitches _switches;
    private readonly ILogger<ModelAutoDownloadService> _logger;
    private readonly OnboardingActivationGate? _onboardingGate;

    public ModelAutoDownloadService(
        IModelDownloadManager downloadManager,
        AiSettings settings,
        IAiFeatureSwitches switches,
        ILogger<ModelAutoDownloadService> logger,
        OnboardingActivationGate? onboardingGate = null)
    {
        _downloadManager = downloadManager;
        _settings = settings;
        _switches = switches;
        _logger = logger;
        _onboardingGate = onboardingGate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_onboardingGate is not null && !_onboardingGate.IsComplete)
            {
                _logger.LogInformation("Automatic AI downloads are waiting for first-run setup to complete");
                await _onboardingGate.WaitAsync(stoppingToken).ConfigureAwait(false);
            }

            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            if (_settings.DevSkipDownload)
            {
                _logger.LogInformation("Automatic AI downloads are disabled for development");
                return;
            }

            if (!_switches.AnyOn && !_settings.AudioPackEnabled)
            {
                _logger.LogInformation("Automatic AI downloads are disabled because every local AI feature is off");
                return;
            }

            if (_switches.AnyOn)
            {
                await DownloadIfNeededAsync(AiModelRole.TextQuality, stoppingToken).ConfigureAwait(false);
            }

            if (_settings.AudioPackEnabled)
            {
                await DownloadIfNeededAsync(AiModelRole.Audio, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automatic AI model download failed");
        }
    }

    private async Task DownloadIfNeededAsync(AiModelRole role, CancellationToken ct)
    {
        var status = _downloadManager.GetStatus(role);
        if (status.State is AiModelState.Ready or AiModelState.Loaded)
        {
            return;
        }

        _logger.LogInformation(
            "Downloading selected AI artifact for {Role}: {File} ({SizeMB} MB)",
            role,
            status.ModelFile,
            status.SizeMB);
        await _downloadManager.StartDownloadAsync(role, ct).ConfigureAwait(false);
        var result = await _downloadManager.WaitForCompletionAsync(role, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("AI artifact download ended as {Outcome}: {Error}", result.Outcome, result.ErrorMessage);
        }
    }
}
