namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Every limit the plugin host services enforce, in one place. A plugin that goes over one is stopped or refused with a
/// plain status on its card; none of these numbers is a setting. Tests build a copy with shorter times
/// (<c>PluginHostLimits.Default with { ... }</c>).
/// </summary>
public sealed record PluginHostLimits
{
    public static PluginHostLimits Default { get; } = new();

    // ── Own records (plugin-records) ─────────────────────────────────────────

    /// <summary>The largest single record value, in bytes (64 KB).</summary>
    public int RecordValueMaxBytes { get; init; } = 64 * 1024;

    /// <summary>The most records one plugin may keep.</summary>
    public int RecordsPerPlugin { get; init; } = 10_000;

    /// <summary>The most space one plugin's records may take together, in bytes (16 MB).</summary>
    public long RecordBytesPerPlugin { get; init; } = 16L * 1024 * 1024;

    /// <summary>The longest a collection or key may be.</summary>
    public int RecordNameMaxLength { get; init; } = 128;

    // ── Recurring jobs (scheduled-work) ──────────────────────────────────────

    /// <summary>How often the scheduler looks for jobs that are due.</summary>
    public TimeSpan JobPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A run longer than this is cancelled and recorded as failed.</summary>
    public TimeSpan JobMaxRunTime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a cancelled job gets to wind down before the scheduler stops waiting for it.</summary>
    public TimeSpan JobCancelGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The most plugin jobs running at once across the whole Engine.</summary>
    public int MaxConcurrentJobs { get; init; } = 2;

    /// <summary>After this many time-limit stops in a row a job pauses until its plugin is switched off and on.</summary>
    public int JobLimitStopsBeforePause { get; init; } = 3;

    // ── Library events (library-events, watch-activity) ──────────────────────

    /// <summary>How often the dispatcher looks for new events.</summary>
    public TimeSpan EventPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>One event may keep a plugin's handler busy this long.</summary>
    public TimeSpan EventHandlerTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>After this many failures in a row the plugin's event subscription pauses until it is switched off and on.</summary>
    public int EventFailuresBeforePause { get; init; } = 5;

    /// <summary>How many events are read from the list at a time.</summary>
    public int EventPageSize { get; init; } = 100;

    // ── Always-on work (background-work) ─────────────────────────────────────

    /// <summary>How often the host checks which always-on work should be running.</summary>
    public TimeSpan BackgroundCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long always-on work gets to stop after it is cancelled.</summary>
    public TimeSpan BackgroundStopGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The pause before each restart after a crash: the first crash waits the first time, and so on; the last time repeats.</summary>
    public IReadOnlyList<TimeSpan> BackgroundRestartBackoff { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>The crash that makes it stop for good (counting crashes inside <see cref="BackgroundCrashWindow"/>).</summary>
    public int BackgroundCrashesBeforeStop { get; init; } = 5;

    /// <summary>Crashes older than this no longer count towards <see cref="BackgroundCrashesBeforeStop"/>.</summary>
    public TimeSpan BackgroundCrashWindow { get; init; } = TimeSpan.FromHours(1);

    // ── Separate programs (kind service) ─────────────────────────────────────

    /// <summary>How often a running program is asked whether it is healthy.</summary>
    public TimeSpan ServiceHealthInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A health or lookup question that takes longer than this counts as no answer.</summary>
    public TimeSpan ServiceRequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>This many unanswered health questions in a row and the program is restarted.</summary>
    public int ServiceHealthFailuresBeforeRestart { get; init; } = 3;

    /// <summary>How long a program gets to end by itself before it is stopped by force.</summary>
    public TimeSpan ServiceStopGrace { get; init; } = TimeSpan.FromSeconds(5);

    // ── Web connection (network.http) ────────────────────────────────────────

    /// <summary>The requests a minute a destination allows when its declaration sets no number.</summary>
    public int HttpDefaultRequestsPerMinute { get; init; } = 60;

    /// <summary>A request that would wait longer than this for its turn is refused.</summary>
    public TimeSpan HttpMaxRateLimitWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The most redirects followed for one request.</summary>
    public int HttpMaxRedirects { get; init; } = 5;

    /// <summary>How long a web-connection problem keeps the plugin on Needs attention after the last time it happened.</summary>
    public TimeSpan HttpProblemWindow { get; init; } = TimeSpan.FromMinutes(10);
}
