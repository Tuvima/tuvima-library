using MediaEngine.Contracts.Ingestion;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Ingestion;

namespace MediaEngine.Api.Services;

/// <summary>
/// Works out the live ingestion numbers once, from the per-file work rows, and publishes them as one snapshot
/// (<see cref="SignalREvents.IngestionQueueProgress"/>) whenever they change. It looks every second while files are being
/// worked on and every few seconds while the Library is idle. The Dashboard only displays what this sends.
/// </summary>
public sealed class IngestionQueueProgressService : BackgroundService, IIngestionQueueProgressService
{
    internal static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);

    private readonly IIngestionQueueProgressRepository _repository;
    private readonly IEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger<IngestionQueueProgressService> _logger;

    private IngestionQueueProgressState _state = IngestionQueueProgressState.Empty;
    private IngestionQueueProgressSnapshot _published;
    private IngestionQueueProgressSnapshot _current;

    public IngestionQueueProgressService(
        IIngestionQueueProgressRepository repository,
        IEventPublisher publisher,
        TimeProvider time,
        ILogger<IngestionQueueProgressService> logger)
    {
        _repository = repository;
        _publisher = publisher;
        _time = time;
        _logger = logger;
        _current = _published = IngestionQueueProgressSnapshot.Empty(_time.GetUtcNow());
    }

    /// <inheritdoc/>
    public IngestionQueueProgressDto Current => IngestionQueueProgressDtoMapper.ToDto(Volatile.Read(ref _current));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = IdleInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                delay = await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not work out the live ingestion numbers; trying again shortly.");
                delay = IdleInterval;
            }

            try
            {
                await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One look at the queue: reads the rows, works out the numbers, and publishes them if they changed.
    /// Returns how long to wait before the next look.
    /// </summary>
    internal async Task<TimeSpan> RefreshAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var rows = await _repository.ReadAsync(_state.PeriodStart, ct).ConfigureAwait(false);
        var result = IngestionQueueProgressCalculator.Calculate(rows, _state, now);

        if (result.StartedNewPeriod && _state.PeriodStart is null)
        {
            // The first read only found the files still being worked on. Now the period's start is known,
            // read everything since then so the first snapshot already counts the files finished meanwhile.
            rows = await _repository.ReadAsync(result.State.PeriodStart, ct).ConfigureAwait(false);
            result = IngestionQueueProgressCalculator.Calculate(rows, _state, now);
        }

        _state = result.State;
        Volatile.Write(ref _current, result.Snapshot);

        if (!result.Snapshot.SameNumbersAs(_published))
        {
            await _publisher.PublishAsync(
                SignalREvents.IngestionQueueProgress,
                IngestionQueueProgressDtoMapper.ToDto(result.Snapshot),
                ct).ConfigureAwait(false);
            _published = result.Snapshot;
        }

        return result.Snapshot.State == IngestionQueueRunState.Idle ? IdleInterval : BusyInterval;
    }
}

/// <summary>Turns the calculator's snapshot into the wire shape.</summary>
public static class IngestionQueueProgressDtoMapper
{
    public static IngestionQueueProgressDto ToDto(IngestionQueueProgressSnapshot snapshot) => new()
    {
        State = snapshot.State switch
        {
            IngestionQueueRunState.Working => "working",
            IngestionQueueRunState.Waiting => "waiting",
            _ => "idle",
        },
        FilesFound = snapshot.FilesFound,
        FilesDone = snapshot.FilesDone,
        FilesWaiting = snapshot.FilesWaiting,
        WaitingFor = snapshot.WaitingFor.ToList(),
        FilesMoreFound = snapshot.FilesMoreFound,
        Outcomes = new IngestionQueueOutcomesDto
        {
            Added = snapshot.Outcomes.Added,
            NeedsReview = snapshot.Outcomes.NeedsReview,
            Failed = snapshot.Outcomes.Failed,
            Skipped = snapshot.Outcomes.Skipped,
        },
        Tally = snapshot.Tally
            .Select(entry => new IngestionQueueTypeTallyDto { MediaType = entry.MediaType, Files = entry.Files })
            .ToList(),
        FilesUnsorted = snapshot.FilesUnsorted,
        StepsTotal = snapshot.StepsTotal,
        StepsCompleted = snapshot.StepsCompleted,
        Progress = snapshot.Progress,
        StartedAt = snapshot.StartedAt,
        LastActivityAt = snapshot.LastActivityAt,
        EstimatedSecondsRemaining = snapshot.EstimatedSecondsRemaining,
        UpdatedAt = snapshot.UpdatedAt,
    };
}
