using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// One-shot startup recovery for durable identity jobs interrupted by an Engine
/// restart. The database is the queue; this service only clears leases from the
/// previous process and wakes the current workers.
/// </summary>
public sealed class HydrationStartupSweepService : BackgroundService
{
    private readonly IIdentityJobRepository _jobs;
    private readonly IIdentityPipelineSignal _signal;
    private readonly ILogger<HydrationStartupSweepService> _logger;

    public HydrationStartupSweepService(
        IIdentityJobRepository jobs,
        IIdentityPipelineSignal signal,
        ILogger<HydrationStartupSweepService> logger)
    {
        _jobs = jobs;
        _signal = signal;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var recovered = await _jobs.RecoverInterruptedJobsAsync(cancellationToken).ConfigureAwait(false);

            if (recovered == 0)
            {
                _logger.LogInformation("Identity startup recovery found no interrupted jobs.");
            }
            else
            {
                _logger.LogInformation(
                    "Identity startup recovery requeued {Count} interrupted job(s).",
                    recovered);

                _signal.Signal(IdentityPipelineSignalKind.Retail);
                _signal.Signal(IdentityPipelineSignalKind.WikidataBridge);
                _signal.Signal(IdentityPipelineSignalKind.Hydration);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity startup recovery failed.");
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
