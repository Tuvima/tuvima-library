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
    private string? _lastState;
    private DateTimeOffset _lastDelivery;
    public IReadOnlyList<ActivityNotification> History => _history.ToArray();

    public void Observe(IngestionQueueProgressDto queue)
    {
        var previous = _lastState;
        _lastState = queue.State;

        // A page load must not replay work already under way as a new notification.
        if (previous is null || previous == queue.State)
        {
            return;
        }

        var notification = (previous, queue.State) switch
        {
            (not WorkingState, WorkingState) => new ActivityNotification("Library update started", false),
            (WorkingState, IdleState) when queue.Outcomes.Failed > 0 =>
                new ActivityNotification("Library update needs attention", true),
            (WorkingState, IdleState) when queue.Outcomes.NeedsReview > 0 =>
                new ActivityNotification("Library update complete; some items need review", false),
            (WorkingState, IdleState) => new ActivityNotification("Library update complete", false),
            _ => null,
        };
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
