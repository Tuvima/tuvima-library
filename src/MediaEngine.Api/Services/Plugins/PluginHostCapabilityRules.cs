using System.Text.RegularExpressions;
using MediaEngine.Api.Services.Events;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Checks a plugin's host-service capabilities (recurring jobs, event subscriptions, always-on work) when it loads, so a
/// mistake shows up as a plain load error on the plugin's card instead of a job that quietly never runs.
/// </summary>
internal static partial class PluginHostCapabilityRules
{
    private const int MaxJobNameLength = 80;

    /// <summary>The plain load-error message for the first problem, or null when the capabilities are fine.</summary>
    public static string? Validate(IReadOnlyList<IPluginCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        try
        {
            var jobKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var job in capabilities.OfType<IPluginScheduledJob>())
            {
                if (string.IsNullOrEmpty(job.Key) || !JobKeyPattern().IsMatch(job.Key))
                {
                    return "A plugin job needs a key of 1 to 64 lower-case letters, digits, hyphens or underscores.";
                }

                if (!jobKeys.Add(job.Key))
                {
                    return $"Plugin job '{job.Key}' is listed more than once.";
                }

                if (string.IsNullOrWhiteSpace(job.DisplayName) || job.DisplayName.Length > MaxJobNameLength)
                {
                    return $"Plugin job '{job.Key}' needs a display name of 1 to {MaxJobNameLength} characters.";
                }

                if (job.DefaultSchedule is null)
                {
                    return $"Plugin job '{job.Key}' has no schedule.";
                }

                if (PluginJobSchedules.Describe(job.DefaultSchedule, out var scheduleProblem) is null)
                {
                    return $"Plugin job '{job.Key}' has a schedule that cannot be used: {scheduleProblem}";
                }
            }

            foreach (var subscriber in capabilities.OfType<IPluginEventSubscriber>())
            {
                if (subscriber.EventTypes is null || subscriber.EventTypes.Count == 0)
                {
                    return "A plugin event subscription needs at least one event type.";
                }

                var unknown = subscriber.EventTypes.FirstOrDefault(type => !ApplicationEventRegistry.IsKnownType(type));
                if (unknown is not null)
                {
                    return $"Plugin subscribes to the unknown event type '{unknown}'.";
                }
            }

            if (capabilities.OfType<IPluginBackgroundService>().Count() > 1)
            {
                return "A plugin can run only one always-on service.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A plugin's own properties threw while being read; report it as the load error rather than failing the catalog.
            return $"Plugin host capabilities could not be read: {ex.Message}";
        }

        return null;
    }

    /// <summary>True when a plugin declaring <paramref name="permissions"/> may be told about <paramref name="eventType"/>.</summary>
    public static bool PermissionCoversEvent(IReadOnlyList<string> permissions, string eventType)
    {
        var needed = ApplicationEventRegistry.IsWatchActivity(eventType)
            ? PluginPermissionIds.WatchActivity
            : PluginPermissionIds.LibraryEvents;
        return permissions.Contains(needed, StringComparer.Ordinal);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobKeyPattern();
}

/// <summary>
/// Works out when a plugin job is due. A job's schedule is its default, unless the administrator wrote an override for the
/// job's key in the plugin's configuration file.
/// </summary>
internal static class PluginJobSchedules
{
    /// <summary>
    /// The schedule in force for a job: the override from the plugin's configuration when it is valid, otherwise the job's
    /// default. A bad override is ignored and <paramref name="overrideProblem"/> says why.
    /// </summary>
    public static PluginSchedule Effective(
        IPluginScheduledJob job,
        IReadOnlyDictionary<string, string>? overrides,
        out string? overrideProblem)
    {
        overrideProblem = null;
        if (overrides is not null && overrides.TryGetValue(job.Key, out var text) && !string.IsNullOrWhiteSpace(text))
        {
            if (PluginSchedule.TryParse(text, out var parsed) && parsed is not null && Describe(parsed, out overrideProblem) is not null)
            {
                return parsed;
            }

            overrideProblem ??= "It is not a cron expression or an interval of at least 5 minutes.";
            overrideProblem = $"The schedule \"{text.Trim()}\" for job '{job.Key}' was ignored: {overrideProblem}";
        }

        return job.DefaultSchedule;
    }

    /// <summary>
    /// A short plain description of the schedule ("every 30 minutes", or the cron text), or null with
    /// <paramref name="problem"/> when it cannot be used.
    /// </summary>
    public static string? Describe(PluginSchedule schedule, out string? problem)
    {
        problem = null;
        if (schedule.Interval is { } interval)
        {
            if (interval < PluginSchedule.MinimumInterval)
            {
                problem = "an interval must be at least 5 minutes.";
                return null;
            }

            return DescribeInterval(interval);
        }

        if (CronScheduler.NextOccurrence(schedule.Cron) is null)
        {
            problem = "the cron expression could not be read.";
            return null;
        }

        return schedule.Cron;
    }

    /// <summary>The next time the job is due after <paramref name="after"/>, or null for a cron expression that never fires again.</summary>
    public static DateTimeOffset? NextDue(PluginSchedule schedule, DateTimeOffset after) =>
        schedule.Interval is { } interval
            ? after + interval
            : CronScheduler.NextOccurrence(schedule.Cron, after)?.ToUniversalTime();

    private static string DescribeInterval(TimeSpan interval)
    {
        if (interval.TotalDays >= 1 && interval.TotalDays % 1 == 0)
        {
            var days = (int)interval.TotalDays;
            return days == 1 ? "every day" : $"every {days} days";
        }

        if (interval.TotalHours >= 1 && interval.TotalHours % 1 == 0)
        {
            var hours = (int)interval.TotalHours;
            return hours == 1 ? "every hour" : $"every {hours} hours";
        }

        return $"every {(int)Math.Round(interval.TotalMinutes)} minutes";
    }
}
