using MediaEngine.Contracts.Ingestion;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Circuit-local, bounded activity notifications. File events never enter this queue.</summary>
public sealed class ActivityNotificationQueue
{
    private readonly Dictionary<Guid, string> _runs = new();
    private readonly Dictionary<Guid, ActivityNotification> _pending = new();
    private readonly Queue<ActivityNotification> _history = new();
    private bool _initialized;
    private DateTimeOffset _lastDelivery;
    public IReadOnlyList<ActivityNotification> History => _history.ToArray();

    public void Observe(IEnumerable<IngestionOperationsBatchDto> batches)
    {
        foreach (var batch in batches)
        {
            var state = batch.OutstandingOperations > 0 ? "running" : batch.Status.ToLowerInvariant();
            if (_runs.TryGetValue(batch.BatchId, out var previous) && previous == state)
            {
                continue;
            }
            _runs[batch.BatchId] = state;
            // A page load must not replay historical completions as new notifications.
            if (!_initialized)
            {
                continue;
            }
            var notification = state switch
            {
                "running" => new ActivityNotification(batch.BatchId, "Library update started", false),
                "completed" => new ActivityNotification(batch.BatchId,
                    batch.ReviewCount > 0 ? "Library update complete; some items need review" : "Library update complete", false),
                "failed" or "attention" => new ActivityNotification(batch.BatchId, "Library update needs attention", true),
                _ => null,
            };
            if (notification is null)
            {
                continue;
            }
            _pending[batch.BatchId] = notification; // Completion supersedes an undelivered start.
            _history.Enqueue(notification);
            while (_history.Count > 50)
            {
                _history.Dequeue();
            }
        }
        _initialized = true;
        while (_pending.Count > 20)
        {
            _pending.Remove(_pending.Keys.First());
        }
        while (_runs.Count > 100)
        {
            _runs.Remove(_runs.Keys.First());
        }
    }

    public ActivityNotification? Take(bool quiet, DateTimeOffset now)
    {
        if (quiet || _pending.Count == 0 || now - _lastDelivery < TimeSpan.FromSeconds(15))
        {
            return null;
        }
        var next = _pending.Values.First();
        _pending.Remove(next.RunId);
        _lastDelivery = now;
        return next;
    }
}

public sealed record ActivityNotification(Guid RunId, string Message, bool NeedsAttention);
