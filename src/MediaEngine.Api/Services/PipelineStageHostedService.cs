using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Owns the common lifecycle for one durable identity-pipeline stage.
/// The database remains the source of truth; signals only shorten the idle wait.
/// </summary>
/// <typeparam name="TWorker">The scoped worker that processes one pipeline stage.</typeparam>
public abstract class PipelineStageHostedService<TWorker> : BackgroundService
    where TWorker : notnull
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReclaimInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IIdentityPipelineSignal _signal;
    private readonly EnrichmentPipelineExecutionGate _executionGate;
    private DateTimeOffset _nextReclaimAt = DateTimeOffset.UtcNow;

    protected PipelineStageHostedService(
        IServiceScopeFactory scopeFactory,
        IIdentityPipelineSignal signal,
        EnrichmentPipelineExecutionGate executionGate,
        ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _executionGate = executionGate;
        Logger = logger;
    }

    protected ILogger Logger { get; }

    protected abstract IdentityPipelineSignalKind WakeSignal { get; }

    protected abstract TimeSpan StuckJobThreshold { get; }

    protected abstract IdentityJobState ProcessingState { get; }

    protected virtual TimeSpan? UniverseStuckJobThreshold => null;

    protected virtual IdentityPipelineSignalKind? DownstreamSignal => null;

    protected abstract Task<int> PollAsync(TWorker worker, CancellationToken stoppingToken);

    protected abstract void LogStarted();

    protected abstract void LogPollError(Exception exception);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();

        while (!stoppingToken.IsCancellationRequested)
        {
            CancellationToken resetCancellationToken = default;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                int processed;

                using (var executionLease = await _executionGate.EnterAsync(GetType().Name, stoppingToken).ConfigureAwait(false))
                {
                    resetCancellationToken = executionLease.PauseCancellationToken;
                    using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        stoppingToken,
                        resetCancellationToken);
                    var pollToken = pollCancellation.Token;

                    // Reclaim jobs stuck in intermediate states every 30 seconds.
                    if (DateTimeOffset.UtcNow >= _nextReclaimAt)
                    {
                        var jobRepository = scope.ServiceProvider.GetRequiredService<IIdentityJobRepository>();
                        var reclaimed = await jobRepository.ReclaimStuckJobsAsync(
                            ProcessingState,
                            StuckJobThreshold,
                            pollToken);
                        if (UniverseStuckJobThreshold is { } universeThreshold)
                        {
                            reclaimed += await jobRepository.ReclaimStuckJobsAsync(
                                IdentityJobState.UniverseEnriching,
                                universeThreshold,
                                pollToken);
                        }
                        if (reclaimed > 0)
                        {
                            Logger.LogInformation(
                                "{Service}: reclaimed {Count} stuck job(s)",
                                GetType().Name,
                                reclaimed);
                        }

                        _nextReclaimAt = DateTimeOffset.UtcNow.Add(ReclaimInterval);
                    }

                    var worker = scope.ServiceProvider.GetRequiredService<TWorker>();
                    processed = await PollAsync(worker, pollToken);
                    if (processed > 0 && DownstreamSignal is { } downstreamSignal)
                    {
                        _signal.Signal(downstreamSignal);
                    }
                }

                // Back off when idle.
                var delay = processed > 0 ? PollInterval : IdleInterval;
                await _signal.WaitAsync(WakeSignal, delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (resetCancellationToken.IsCancellationRequested)
            {
                // A destructive development reset canceled this poll. The next
                // loop waits at the gate until the replacement database is ready.
                continue;
            }
            catch (Exception exception)
            {
                LogPollError(exception);
                await _signal.WaitAsync(WakeSignal, IdleInterval, stoppingToken);
            }
        }
    }
}
