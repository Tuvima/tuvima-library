using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.Collections.ListImport;

/// <summary>
/// Fetches a newer copy of an imported list from where it came from (Collections and lists spec D5). A file import has nowhere
/// to fetch from, so only the web list sources (a later packet) implement this: each registers one fetcher that says which
/// sources it serves and returns the list as it is now. The refresh job asks every registered fetcher; when none serves a
/// source the list's stored entries are matched against the library again instead, which picks up titles added since.
/// </summary>
public interface IListSourceFetcher
{
    /// <summary>True when this fetcher serves the source (for example its address is a TMDb list address).</summary>
    bool CanFetch(CollectionListSource source);

    /// <summary>The list as it is now, or null when the source could not be reached (the refresh then tries again later).</summary>
    Task<ParsedList?> FetchAsync(CollectionListSource source, CancellationToken ct);

    /// <summary>
    /// True when the list was read with a profile's own account on its site and that account is no longer signed in (the person
    /// signed out, or the site stopped accepting it), so the list cannot refresh until they sign in again.
    /// </summary>
    Task<bool> NeedsSignInAsync(CollectionListSource source, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// The refresh job for imported lists. Each pass finds the list collections whose refresh (daily or weekly) is due, refreshes
/// them one at a time under a scheduled-task run record, and moves the next due time on. Due times are stored with the list
/// source, so a restart resumes where it left off and a missed refresh simply runs at the next pass.
/// </summary>
public sealed class CollectionListRefreshWorker : BackgroundService
{
    private const int BatchSize = 10;
    private static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(15);

    private readonly ICollectionListRepository _lists;
    private readonly ListImportService _service;
    private readonly IEnumerable<IListSourceFetcher> _fetchers;
    private readonly IRecurringTaskRecorder _recorder;
    private readonly TimeProvider _time;
    private readonly ILogger<CollectionListRefreshWorker> _logger;

    public CollectionListRefreshWorker(
        ICollectionListRepository lists,
        ListImportService service,
        IEnumerable<IListSourceFetcher> fetchers,
        IRecurringTaskRecorder recorder,
        ILogger<CollectionListRefreshWorker> logger,
        TimeProvider? time = null)
    {
        _lists = lists;
        _service = service;
        _fetchers = fetchers;
        _recorder = recorder;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PassInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _recorder.RecordAsync(
                    ScheduledTaskKeys.CollectionListRefresh,
                    ct => RunOnceAsync(ct),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The imported list refresh pass failed");
            }
        }
    }

    /// <summary>One pass: refreshes every list collection that is due. Returns how many were refreshed.</summary>
    public async Task<int?> RunOnceAsync(CancellationToken ct)
    {
        var due = await _lists.GetDueAsync(_time.GetUtcNow(), BatchSize, ct).ConfigureAwait(false);
        var refreshed = 0;
        foreach (var collectionId in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var source = await _lists.GetSourceAsync(collectionId, ct).ConfigureAwait(false);
                if (source is null)
                {
                    continue;
                }

                ParsedList? fresh = null;
                var fetcher = _fetchers.FirstOrDefault(candidate => candidate.CanFetch(source));
                if (fetcher is not null)
                {
                    fresh = await fetcher.FetchAsync(source, ct).ConfigureAwait(false);
                    if (fresh is null)
                    {
                        // The source is down: the collection keeps what it has and the refresh tries again at the next pass.
                        _logger.LogInformation("List source for collection {CollectionId} could not be reached; will try again", collectionId);
                        continue;
                    }
                }

                if (await _service.RefreshAsync(collectionId, fresh, allowedWorkIds: null, ct).ConfigureAwait(false) is not null)
                {
                    refreshed++;
                    CollectionLanePolicy.MarkChanged();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One list failing must not stop the others; the due time is unchanged, so it is tried again next pass.
                _logger.LogError(ex, "Refreshing imported list collection {CollectionId} failed", collectionId);
            }
        }

        return refreshed;
    }
}
