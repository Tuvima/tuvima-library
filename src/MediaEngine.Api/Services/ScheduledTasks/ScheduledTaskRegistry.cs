using System.Collections.Concurrent;

namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>In-memory <see cref="IScheduledTaskRegistry"/>. Thread-safe: tasks report from their own background threads.</summary>
public sealed class ScheduledTaskRegistry : IScheduledTaskRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<IScheduledTaskRunHistory> _history;
    private readonly TimeProvider _clock;

    public ScheduledTaskRegistry(IEnumerable<IScheduledTaskRunHistory> history, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(clock);
        _history = history.ToArray();
        _clock = clock;
        StartedAt = clock.GetLocalNow();
    }

    /// <inheritdoc/>
    public DateTimeOffset StartedAt { get; }

    /// <inheritdoc/>
    public ScheduledTaskHandle Register(ScheduledTaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _entries.AddOrUpdate(
            definition.Id,
            _ => new Entry(definition),
            (_, existing) => existing with { Definition = definition });
        return new ScheduledTaskHandle(this, definition.Id);
    }

    /// <inheritdoc/>
    public void RecordRun(string taskId, ScheduledTaskOutcome outcome, string? reason = null)
    {
        var run = new ScheduledTaskRun(_clock.GetLocalNow(), outcome, outcome == ScheduledTaskOutcome.Failed ? reason : null);
        Update(taskId, entry => entry with { LastRun = run });
    }

    /// <inheritdoc/>
    public void SetNextRun(string taskId, DateTimeOffset? nextRun) =>
        Update(taskId, entry => entry with { NextRun = nextRun });

    /// <inheritdoc/>
    public IReadOnlyList<ScheduledTaskSnapshot> GetSnapshot() => _entries.Values
        .Select(entry => new ScheduledTaskSnapshot(entry.Definition, Newest(entry), entry.NextRun))
        .OrderBy(snapshot => snapshot.Definition.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    private ScheduledTaskRun? Newest(Entry entry)
    {
        var best = entry.LastRun;
        foreach (var source in _history)
        {
            var recorded = source.GetLastRun(entry.Definition.Id);
            if (recorded is not null && (best is null || recorded.At > best.At))
            {
                best = recorded;
            }
        }

        return best;
    }

    private void Update(string taskId, Func<Entry, Entry> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        // A report for a task that never registered is a programming slip, not something to fail a background loop over.
        while (_entries.TryGetValue(taskId, out var current))
        {
            if (_entries.TryUpdate(taskId, change(current), current))
            {
                return;
            }
        }
    }

    private sealed record Entry(ScheduledTaskDefinition Definition, ScheduledTaskRun? LastRun = null, DateTimeOffset? NextRun = null);
}
