using MediaEngine.AI.Configuration;
using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services;

/// <summary>
/// One-shot background service that runs the hardware benchmark 15 seconds after the
/// Engine starts.  The delay allows model auto-download to begin and the DI container
/// to fully settle before inference is attempted.
///
/// Results are cached in AiSettings.HardwareProfile so the benchmark is skipped on
/// subsequent restarts unless the tier is still set to "auto".
/// </summary>
public sealed class HardwareBenchmarkBackgroundService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(60);

    private readonly HardwareBenchmarkService _benchmark;
    private readonly ILogger<HardwareBenchmarkBackgroundService> _logger;
    private readonly OnboardingActivationGate? _onboardingGate;
    private readonly AiSettings _settings;

    public HardwareBenchmarkBackgroundService(
        HardwareBenchmarkService benchmark,
        AiSettings settings,
        ILogger<HardwareBenchmarkBackgroundService> logger,
        OnboardingActivationGate? onboardingGate = null)
    {
        _benchmark = benchmark;
        _settings = settings;
        _logger = logger;
        _onboardingGate = onboardingGate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!AnyAiFeatureEnabled(_settings))
        {
            _logger.LogInformation("Automatic hardware benchmarking is disabled because local AI is off");
            return;
        }

        if (_onboardingGate is not null && !_onboardingGate.IsComplete)
        {
            _logger.LogInformation("Hardware benchmarking is waiting for first-run setup to complete");
            await _onboardingGate.WaitAsync(stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "HardwareBenchmarkBackgroundService: waiting {Seconds}s before running benchmark",
            StartupDelay.TotalSeconds);

        await Task.Delay(StartupDelay, stoppingToken);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // Library background work waits for a quiet moment. The benchmark never used to wait, so keep trying until it has
        // run instead of giving up the first time the machine is busy.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _benchmark.BenchmarkAsync(ct: stoppingToken);
                break;
            }
            catch (OperationCanceledException)
            {
                // Shutdown before benchmark completed — no action needed.
                break;
            }
            catch (AiRequestDeferredException)
            {
                _logger.LogInformation(
                    "HardwareBenchmarkBackgroundService: the machine is busy; trying again in {Seconds}s",
                    BusyRetryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(BusyRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HardwareBenchmarkBackgroundService: benchmark failed unexpectedly");
                break;
            }
        }

        // Service is done — runs once on startup only.
    }

    private static bool AnyAiFeatureEnabled(AiSettings settings) => settings.IsLocalAiEnabled;
}
