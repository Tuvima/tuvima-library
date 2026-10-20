using MediaEngine.Contracts.Ingestion;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Circuit-local, bounded activity notifications, driven by the Engine's live ingestion numbers: one when the library
/// starts working, one when it finishes. File events never enter this queue.
/// </summary>
public sealed class ActivityNotificationQueue
{
    private const string WorkingState = "working";
    private const string IdleState = "idle";

    private readonly Queue<ActivityNotification> _history = new();
    private ActivityNotification? _pending;
    private bool _seen;
    private string? _lastState;
    private DateTimeOffset? _lastRunStart;
    private bool _runOpen;
    private DateTimeOffset _lastDelivery;
    public IReadOnlyList<ActivityNotification> History => _history.ToArray();

    /// <summary>
    /// Follows runs, not single looks: the page looks only every few seconds, so a run is recognised by the Engine's
    /// period start (<see cref="IngestionQueueProgressDto.StartedAt"/>) as well as by state changes. A run that started
    /// and finished between two looks still gets its completion; waiting for a missing tool neither completes a run nor
    /// starts a new one, so working, waiting, idle completes once and waiting, working does not announce a second start.
    /// </summary>
    public void Observe(IngestionQueueProgressDto queue)
    {
        // A page load must not replay work already under way as a new notification.
        if (!_seen)
        {
            _seen = true;
            _lastState = queue.State;
            _lastRunStart = queue.StartedAt;
            _runOpen = queue.State == WorkingState;
            return;
        }

        var newRun = queue.StartedAt is { } started && started != _lastRunStart;
        _lastRunStart = queue.StartedAt ?? _lastRunStart;
        var previous = _lastState;
        _lastState = queue.State;

        ActivityNotification? notification = null;
        if (queue.State == WorkingState)
        {
            if (!_runOpen && (previous != WorkingState || newRun))
            {
                notification = new ActivityNotification("Library update started", false);
            }

            _runOpen = true;
        }
        else if (queue.State == IdleState)
        {
            if (_runOpen || newRun)
            {
                notification = queue.Outcomes.Failed > 0
                    ? new ActivityNotification("Library update needs attention", true)
                    : queue.Outcomes.NeedsReview > 0
                        ? new ActivityNotification("Library update complete; some items need review", false)
                        : new ActivityNotification("Library update complete", false);
            }

            _runOpen = false;
        }

        if (notification is null)
        {
            return;
        }

        _pending = notification; // Completion supersedes an undelivered start.
        _history.Enqueue(notification);
        while (_history.Count > 50)
        {
            _history.Dequeue();
        }
    }

    public ActivityNotification? Take(bool quiet, DateTimeOffset now)
    {
        if (quiet || _pending is null || now - _lastDelivery < TimeSpan.FromSeconds(15))
        {
            return null;
        }

        var next = _pending;
        _pending = null;
        _lastDelivery = now;
        return next;
    }
}

public sealed record ActivityNotification(string Message, bool NeedsAttention);
