using MediaEngine.Contracts.Universe;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Where the Universe page is in loading its one Engine read.</summary>
public enum UniversePageStatus
{
    Loading,
    Loaded,
    NotFound,
    Unavailable,
}

/// <summary>
/// The state of one open Universe page: the page read, whether it is loading, missing or unreachable, and when it may
/// be read again because enrichment moved. The page owns one of these for as long as it is open.
/// </summary>
/// <remarks>
/// The Engine's enrichment event carries no Universe, so it can only say "something was learned". The page answers it by
/// reading its own overview again, at most once per <see cref="RefreshInterval"/>, never once per event. An event that
/// arrives inside that window schedules one read for when the window ends, so the last thing learned is never missed.
/// </remarks>
public sealed class UniversePageState(IEngineApiClient api, TimeProvider? clock = null)
{
    /// <summary>The fewest time between two reads triggered by enrichment events.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset? _lastReadAt;
    private DateTimeOffset? _lastEventHandledAt;
    private bool _refreshScheduled;

    public UniversePageStatus Status { get; private set; } = UniversePageStatus.Loading;

    public UniversePageDto? Page { get; private set; }

    /// <summary>
    /// Reads the page. A first read (or a retry) shows the loading state and may end in not-found or unreachable. A quiet
    /// read (after an edit or an enrichment event) keeps what is on screen and only replaces it with a newer page; if the
    /// Engine cannot be reached the page that is already shown stays.
    /// </summary>
    public async Task LoadAsync(string qid, bool quiet, CancellationToken ct = default)
    {
        if (!quiet || Page is null)
        {
            Status = UniversePageStatus.Loading;
            Page = null;
        }

        var result = await api.GetUniversePageAsync(qid, ct).ConfigureAwait(false);
        _lastReadAt = _clock.GetUtcNow();
        switch (result.Status)
        {
            case UniversePageLoadStatus.Loaded when result.Page is not null:
                Page = result.Page;
                Status = UniversePageStatus.Loaded;
                break;
            case UniversePageLoadStatus.NotFound:
                Page = null;
                Status = UniversePageStatus.NotFound;
                break;
            default:
                if (Page is null)
                {
                    Status = UniversePageStatus.Unavailable;
                }

                break;
        }
    }

    /// <summary>
    /// Decides what an enrichment event received at <paramref name="eventReceivedAt"/> means for this page. Null: do
    /// nothing (no page yet, the event was already answered, or a read is already scheduled). Otherwise the time to wait
    /// before reading again (zero when the last read is old enough); the caller then calls <see cref="RefreshStarted"/>.
    /// </summary>
    public TimeSpan? RequestEnrichmentRefresh(DateTimeOffset? eventReceivedAt)
    {
        if (eventReceivedAt is null
            || Page is null
            || _refreshScheduled
            || eventReceivedAt == _lastEventHandledAt)
        {
            return null;
        }

        _lastEventHandledAt = eventReceivedAt;
        _refreshScheduled = true;
        if (_lastReadAt is not { } lastRead)
        {
            return TimeSpan.Zero;
        }

        var wait = RefreshInterval - (_clock.GetUtcNow() - lastRead);
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>The scheduled read is starting, so a later event may schedule the next one.</summary>
    public void RefreshStarted() => _refreshScheduled = false;
}
