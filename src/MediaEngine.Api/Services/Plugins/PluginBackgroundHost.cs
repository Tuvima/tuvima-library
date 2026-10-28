using System.Collections.Concurrent;
using System.Text.Json;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Runs the always-on work plugins declare (<see cref="IPluginBackgroundService"/>) while the plugin is switched on,
/// approved, loaded and allowed <c>background-work</c>. It stops the work (cancel, then a ten-second grace) when the plugin
/// is switched off, loses its approval, or the Engine stops. Work that crashes, or returns when it should not, restarts after
/// a growing pause (5 s, 30 s, 2 min, 10 min); the fifth crash inside an hour stops it for good and the plugin shows
/// Broken until it is switched off and on. Saving the plugin's settings restarts its work so it reads the new values. Work
/// that ignores the request to stop is kept in view as "stopping", and a new run for that plugin does not start until the
/// old one has really finished, so the same work never runs twice at once.
/// </summary>
internal sealed class PluginBackgroundHost : BackgroundService
{
    private const string StoppingState = "stopping";

    private sealed class Runner(string pluginId, int generation, string settingsFingerprint, CancellationTokenSource cancellation)
    {
        public string PluginId { get; } = pluginId;
        public int Generation { get; } = generation;

        /// <summary>The plugin's settings as this run read them; a different value means the work is restarted.</summary>
        public string SettingsFingerprint { get; } = settingsFingerprint;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Loop { get; set; } = Task.CompletedTask;
    }

    private readonly PluginCatalog _catalog;
    private readonly PluginHostStatusRegistry _statuses;
    private readonly IPluginExecutionContextFactory _contexts;
    private readonly TimeProvider _clock;
    private readonly PluginHostLimits _limits;
    private readonly ILogger<PluginBackgroundHost> _logger;
    private readonly Dictionary<string, Runner> _runners = new(StringComparer.OrdinalIgnoreCase);

    // Runs that were asked to stop and have not finished yet (they ignored the request). A new run for the same plugin waits for them.
    private readonly ConcurrentDictionary<string, Runner> _stopping = new(StringComparer.OrdinalIgnoreCase);

    public PluginBackgroundHost(
        PluginCatalog catalog,
        PluginHostStatusRegistry statuses,
        IPluginExecutionContextFactory contexts,
        PluginHostLimits limits,
        TimeProvider clock,
        ILogger<PluginBackgroundHost> logger)
    {
        _catalog = catalog;
        _statuses = statuses;
        _contexts = contexts;
        _limits = limits;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Plugin background work check failed; trying again shortly");
                }

                await Task.Delay(_limits.BackgroundCheckInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping; everything started below is stopped in the finally block.
        }
        finally
        {
            await StopAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One pass: starts work that should be running and stops work that should not.</summary>
    public async Task ReconcileAsync()
    {
        var wanted = new Dictionary<string, (PluginRegistration Registration, IPluginBackgroundService Service)>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in _catalog.ListInStartOrder())
        {
            var service = registration.Capabilities.OfType<IPluginBackgroundService>().FirstOrDefault();
            if (service is null)
            {
                continue;
            }

            var status = _statuses.Get(registration.Manifest.Id);
            if (registration.Enabled
                && registration.LoadError is null
                && registration.Manifest.Permissions.Contains(PluginPermissionIds.BackgroundWork, StringComparer.Ordinal)
                && _catalog.WaitingFor(registration.Manifest.Id) is null)
            {
                wanted[registration.Manifest.Id] = (registration, service);
            }
            else
            {
                status.SetBackgroundService(_stopping.ContainsKey(registration.Manifest.Id) ? StoppingState : null);
            }
        }

        foreach (var (pluginId, runner) in _runners.ToList())
        {
            var generation = _statuses.Get(pluginId).Generation;
            if (!wanted.TryGetValue(pluginId, out var current)
                || runner.Generation != generation
                || !string.Equals(runner.SettingsFingerprint, SettingsFingerprint(current.Registration), StringComparison.Ordinal))
            {
                // Switched off (or off and on again), or its settings were saved: stop this run; a fresh one starts below
                // if it is still wanted.
                await StopAsync(runner).ConfigureAwait(false);
                _runners.Remove(pluginId);
            }
        }

        foreach (var (pluginId, entry) in wanted)
        {
            var status = _statuses.Get(pluginId);
            if (_runners.ContainsKey(pluginId) || status.IsPaused(PluginHostMessages.BackgroundProblemKey))
            {
                continue;
            }

            if (_stopping.ContainsKey(pluginId))
            {
                // The previous run has not finished stopping; starting another would run the same work twice.
                status.SetBackgroundService(StoppingState);
                continue;
            }

            var runner = new Runner(pluginId, status.Generation, SettingsFingerprint(entry.Registration), new CancellationTokenSource());
            _runners[pluginId] = runner;
            status.SetBackgroundService("running");
            runner.Loop = Task.Run(() => RunLoopAsync(runner, entry.Service, status), CancellationToken.None);
        }
    }

    /// <summary>Stops everything (used when the Engine stops, and by tests).</summary>
    public async Task StopAllAsync()
    {
        foreach (var runner in _runners.Values.ToList())
        {
            await StopAsync(runner).ConfigureAwait(false);
        }

        _runners.Clear();
    }

    // Keys are sorted so the same settings always give the same text.
    private static string SettingsFingerprint(PluginRegistration registration) =>
        JsonSerializer.Serialize(registration.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value));

    private async Task StopAsync(Runner runner)
    {
        await runner.Cancellation.CancelAsync().ConfigureAwait(false);
        var finished = await Task.WhenAny(runner.Loop, Task.Delay(_limits.BackgroundStopGrace)).ConfigureAwait(false);
        var status = _statuses.Get(runner.PluginId);
        if (finished == runner.Loop)
        {
            runner.Cancellation.Dispose();
            status.SetBackgroundService(null);
            return;
        }

        _logger.LogWarning(
            "Plugin {PluginId} did not stop its background work within {Seconds} seconds of being asked to",
            runner.PluginId, (int)_limits.BackgroundStopGrace.TotalSeconds);

        // It ignored the request: keep it in view, and start nothing new for this plugin until it really ends.
        _stopping[runner.PluginId] = runner;
        status.SetBackgroundService(StoppingState);
        _ = runner.Loop.ContinueWith(
            task =>
            {
                _ = task.Exception; // observed so a late failure is not an unhandled task error
                _stopping.TryRemove(new KeyValuePair<string, Runner>(runner.PluginId, runner));
                runner.Cancellation.Dispose();
                if (string.Equals(status.Describe().BackgroundService, StoppingState, StringComparison.Ordinal))
                {
                    status.SetBackgroundService(null);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunLoopAsync(Runner runner, IPluginBackgroundService service, PluginHostStatus status)
    {
        var token = runner.Cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            string crash;
            try
            {
                await using var context = _contexts.Create(runner.PluginId, "background");
                await service.RunAsync(context, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                crash = "it stopped when it should have kept running";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin {PluginId} background work crashed", runner.PluginId);
                crash = ex.Message;
            }

            if (token.IsCancellationRequested)
            {
                return; // it was asked to stop; whatever it did on the way out is not a crash
            }

            var crashes = status.RecordCrash(_clock.GetUtcNow(), _limits.BackgroundCrashWindow);
            status.SetLastProblem($"Its background work crashed: {crash}");
            if (crashes >= _limits.BackgroundCrashesBeforeStop)
            {
                status.Pause(PluginHostMessages.BackgroundProblemKey);
                status.SetProblem(PluginHostMessages.BackgroundProblemKey, PluginHostSeverity.Broken, PluginHostMessages.BackgroundStopped(_limits));
                status.SetBackgroundService("stopped");
                _logger.LogError("Plugin {PluginId} background work stopped after {Crashes} crashes inside {Window}", runner.PluginId, crashes, _limits.BackgroundCrashWindow);
                return;
            }

            status.SetBackgroundService("restarting");
            var backoff = _limits.BackgroundRestartBackoff;
            try
            {
                await Task.Delay(backoff[Math.Min(crashes - 1, backoff.Count - 1)], token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }

            status.SetBackgroundService("running");
        }
    }
}
