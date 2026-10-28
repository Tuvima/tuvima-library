using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>What one system activity prune removed.</summary>
public sealed record ActivityPruneResult(int DeletedForAge, int DeletedForCap, int RetentionDays, int MaxEvents, int DeletedPeopleActions = 0)
{
    public int Total => DeletedForAge + DeletedForCap + DeletedPeopleActions;
}

/// <summary>
/// Applies the system activity (Events) retention rule: entries older than <c>activity_retention_days</c> go first,
/// then, if more than <c>activity_max_events</c> remain, the oldest of those go. Personal activity is not touched.
/// </summary>
public sealed class ActivityRetentionPruner
{
    private readonly ISystemActivityRepository _activity;
    private readonly ISystemActivityRetention _retention;

    public ActivityRetentionPruner(ISystemActivityRepository activity, ISystemActivityRetention retention)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(retention);
        _activity = activity;
        _retention = retention;
    }

    public async Task<ActivityPruneResult> PruneAsync(MaintenanceSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var forAge = settings.ActivityRetentionDays > 0
            ? await _activity.PruneOlderThanAsync(settings.ActivityRetentionDays, ct).ConfigureAwait(false)
            : 0;
        var forCap = settings.ActivityMaxEvents > 0
            ? await _retention.PruneToMaxEventsAsync(settings.ActivityMaxEvents, ct).ConfigureAwait(false)
            : 0;

        // What people did with plugins has its own, longer retention (Plugin architecture spec C25).
        var forPeople = settings.PluginPeopleActionRetentionDays > 0
            ? await _retention.PruneActionTypeOlderThanAsync(SystemActionType.PluginPeopleAction, settings.PluginPeopleActionRetentionDays, ct).ConfigureAwait(false)
            : 0;

        return new ActivityPruneResult(forAge, forCap, settings.ActivityRetentionDays, settings.ActivityMaxEvents, forPeople);
    }
}
