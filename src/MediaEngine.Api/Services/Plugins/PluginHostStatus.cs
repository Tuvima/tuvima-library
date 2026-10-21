using System.Collections.Concurrent;
using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>How serious a plugin's host-service problem is. Only <see cref="Broken"/> shades the menu red.</summary>
internal enum PluginHostSeverity
{
    NeedsAttention = 1,
    Broken = 2,
}

/// <summary>
/// The plain words for each plugin host-service problem, so the services, the plugin card and the tests agree on them.
/// </summary>
internal static class PluginHostMessages
{
    public const string StorageProblemKey = "storage";
    public const string HttpRateProblemKey = "http-rate";
    public const string EventsProblemKey = "events";
    public const string BackgroundProblemKey = "background";

    public static string JobProblemKey(string jobKey) => $"job:{jobKey}";

    public static string StorageFull(PluginHostLimits limits) =>
        $"Reached its storage limit ({FormatBytes(limits.RecordBytesPerPlugin)})";

    public static string TooManyRecords(PluginHostLimits limits) =>
        $"Reached its limit of {limits.RecordsPerPlugin:N0} records";

    public static string ValueTooLarge(PluginHostLimits limits) =>
        $"A record was larger than {FormatBytes(limits.RecordValueMaxBytes)}";

    public static string JobPaused(string jobName, PluginHostLimits limits) =>
        $"{jobName} paused after running too long {limits.JobLimitStopsBeforePause} times";

    public static string JobTooLong(PluginHostLimits limits) =>
        $"Stopped: ran longer than {FormatDuration(limits.JobMaxRunTime)}";

    public static string EventsPaused(PluginHostLimits limits) =>
        $"Stopped receiving events after {limits.EventFailuresBeforePause} failures";

    public static string BackgroundStopped(PluginHostLimits limits) =>
        $"Stopped after crashing {limits.BackgroundCrashesBeforeStop} times in an hour";

    public static string RateLimited(int requestsPerMinute, string host) =>
        $"Hit its limit of {requestsPerMinute} requests a minute to {host}";

    public static string HostNotDeclared(string host) =>
        $"Tried to contact {host}, which it did not say it would contact";

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 when bytes % (1024L * 1024) == 0 => $"{bytes / (1024 * 1024)} MB",
        >= 1024 when bytes % 1024 == 0 => $"{bytes / 1024} KB",
        _ => $"{bytes} bytes",
    };

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalMinutes >= 1 && span.TotalMinutes % 1 == 0)
        {
            var minutes = (int)span.TotalMinutes;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        var seconds = (int)Math.Ceiling(span.TotalSeconds);
        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }
}

/// <summary>
/// What the Library's host services know about one plugin right now: each job's state, its event
/// subscription, its always-on work and the problems that make its card say Needs attention or Broken. Kept in memory
/// (a restart starts clean) and cleared when the plugin is switched on or off, which is also what un-pauses a plugin
/// that was stopped for going over a limit. Thread-safe.
/// </summary>
internal sealed class PluginHostStatus(TimeProvider clock)
{
    private sealed record Problem(string Key, PluginHostSeverity Severity, string Message, DateTimeOffset? ExpiresAt);

    private readonly object _gate = new();
    private readonly List<Problem> _problems = [];
    private readonly Dictionary<string, (string Name, string State)> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _paused = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _crashes = [];
    private string? _eventSubscription;
    private string? _backgroundService;
    private string? _lastProblem;
    private int _generation;

    /// <summary>Changes every time the status is cleared, so a service can tell its remembered state is out of date.</summary>
    public int Generation
    {
        get { lock (_gate) { return _generation; } }
    }

    /// <summary>Forgets everything: problems, pauses, counters and states. Called when the plugin is switched on or off.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _problems.Clear();
            _jobs.Clear();
            _counters.Clear();
            _paused.Clear();
            _crashes.Clear();
            _eventSubscription = null;
            _backgroundService = null;
            _lastProblem = null;
            _generation++;
        }
    }

    /// <summary>Sets (or replaces) a problem. A problem with a <paramref name="lifetime"/> fades out unless it is set again.</summary>
    public void SetProblem(string key, PluginHostSeverity severity, string message, TimeSpan? lifetime = null)
    {
        lock (_gate)
        {
            _problems.RemoveAll(problem => problem.Key == key);
            _problems.Add(new Problem(key, severity, message, lifetime is { } span ? clock.GetUtcNow() + span : null));
            _lastProblem = message;
        }
    }

    public void ClearProblem(string key)
    {
        lock (_gate)
        {
            _problems.RemoveAll(problem => problem.Key == key);
        }
    }

    /// <summary>Records something that went wrong without changing the plugin's health word.</summary>
    public void SetLastProblem(string message)
    {
        lock (_gate)
        {
            _lastProblem = message;
        }
    }

    public void SetJobState(string jobKey, string jobName, string state)
    {
        lock (_gate)
        {
            _jobs[jobKey] = (jobName, state);
        }
    }

    public void SetEventSubscription(string? state)
    {
        lock (_gate)
        {
            _eventSubscription = state;
        }
    }

    public void SetBackgroundService(string? state)
    {
        lock (_gate)
        {
            _backgroundService = state;
        }
    }

    /// <summary>Marks a part of the plugin (a job, events, always-on work) as stopped until the plugin is switched off and on.</summary>
    public void Pause(string key)
    {
        lock (_gate)
        {
            _paused.Add(key);
        }
    }

    public bool IsPaused(string key)
    {
        lock (_gate)
        {
            return _paused.Contains(key);
        }
    }

    /// <summary>Adds one to a named counter and returns the new count.</summary>
    public int Increment(string counter)
    {
        lock (_gate)
        {
            return _counters[counter] = _counters.GetValueOrDefault(counter) + 1;
        }
    }

    public void ClearCounter(string counter)
    {
        lock (_gate)
        {
            _counters.Remove(counter);
        }
    }

    /// <summary>Notes a crash of the always-on work and returns how many crashes happened inside <paramref name="window"/>.</summary>
    public int RecordCrash(DateTimeOffset when, TimeSpan window)
    {
        lock (_gate)
        {
            _crashes.Add(when);
            _crashes.RemoveAll(crash => crash < when - window);
            return _crashes.Count;
        }
    }

    /// <summary>The plugin's host-service state as the plugin list sends it. The caller fills in the stored-record use, which lives in the data store.</summary>
    public PluginHostServicesDto Describe()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            _problems.RemoveAll(problem => problem.ExpiresAt is { } expires && expires <= now);

            var worst = _problems
                .OrderByDescending(problem => problem.Severity)
                .FirstOrDefault();
            return new PluginHostServicesDto
            {
                State = worst is null ? "working" : worst.Severity == PluginHostSeverity.Broken ? "broken" : "needs_attention",
                Problem = worst?.Message,
                LastProblem = _lastProblem,
                Jobs = _jobs
                    .OrderBy(job => job.Value.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(job => new PluginHostJobDto { Key = job.Key, Name = job.Value.Name, State = job.Value.State })
                    .ToList(),
                EventSubscription = _eventSubscription,
                BackgroundService = _backgroundService,
            };
        }
    }
}

/// <summary>
/// One <see cref="PluginHostStatus"/> per plugin, created on first use. It clears a plugin's status whenever that plugin is
/// switched on or off.
/// </summary>
internal sealed class PluginHostStatusRegistry
{
    private readonly ConcurrentDictionary<string, PluginHostStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;

    public PluginHostStatusRegistry(PluginCatalog catalog, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _clock = clock ?? TimeProvider.System;
        catalog.EnabledChanged += pluginId => Get(pluginId).Reset();
    }

    public PluginHostStatus Get(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _statuses.GetOrAdd(pluginId, _ => new PluginHostStatus(_clock));
    }
}
