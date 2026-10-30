using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Service;

/// <summary>Where a running plugin program can be reached, and the key (null for a program the Library did not start).</summary>
internal sealed record ServiceEndpoint(Uri BaseAddress, string? Key);

/// <summary>
/// Starts and supervises the plugins that are separate programs (developer-tools WP18) while the plugin is switched on and
/// approved. A program the Library starts gets a free port on this machine only, a key limited to its plugin, and is asked
/// "are you healthy?" every few seconds. A program that exits, stops answering or grows past its memory limit is stopped
/// and restarted after a growing pause (the same schedule as other always-on plugin work); the fifth crash inside an hour
/// stops it for good and the plugin shows Broken until it is switched off and on. A program that is already running
/// elsewhere (an <c>address</c>) is only watched, never started or stopped.
/// </summary>
internal sealed class PluginServiceHost : BackgroundService
{
    private sealed class Runner(string pluginId, int generation, CancellationTokenSource cancellation)
    {
        public string PluginId { get; } = pluginId;
        public int Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Loop { get; set; } = Task.CompletedTask;
        public ServiceEndpoint? Endpoint { get; set; }
    }

    private static readonly HashSet<string> InheritedVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USERPROFILE", "TEMP", "TMP", "TMPDIR", "SystemRoot", "ComSpec", "PATHEXT", "LANG", "LANGUAGE", "TZ",
        "LOCALAPPDATA", "APPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "USER", "LOGNAME",
    };

    private readonly PluginCatalog _catalog;
    private readonly PluginHostStatusRegistry _statuses;
    private readonly PluginKeyRegistry _keys;
    private readonly PluginHostLimits _limits;
    private readonly TimeProvider _clock;
    private readonly Func<string?> _libraryUrl;
    private readonly ILogger<PluginServiceHost> _logger;
    private readonly HttpClient _http;
    private readonly Dictionary<string, Runner> _runners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ServiceEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public PluginServiceHost(
        PluginCatalog catalog,
        PluginHostStatusRegistry statuses,
        PluginKeyRegistry keys,
        PluginHostLimits limits,
        TimeProvider clock,
        Func<string?> libraryUrl,
        ILogger<PluginServiceHost> logger)
    {
        _catalog = catalog;
        _statuses = statuses;
        _keys = keys;
        _limits = limits;
        _clock = clock;
        _libraryUrl = libraryUrl;
        _logger = logger;

        // Programs are on this machine or at an address the administrator approved; a system proxy must never sit between.
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = limits.ServiceRequestTimeout,
        };
    }

    /// <summary>Where the plugin's program can be reached right now, or null when it is not running and healthy.</summary>
    public ServiceEndpoint? GetEndpoint(string pluginId) => _endpoints.TryGetValue(pluginId, out var endpoint) ? endpoint : null;

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
                    _logger.LogWarning(ex, "Plugin program check failed; trying again shortly");
                }

                await Task.Delay(_limits.BackgroundCheckInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping; every program is stopped below.
        }
        finally
        {
            await StopAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One pass: starts programs that should be running and stops the ones that should not.</summary>
    public async Task ReconcileAsync()
    {
        var wanted = new Dictionary<string, ServiceDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in _catalog.List())
        {
            var capability = registration.Capabilities.OfType<ServiceLookupCapability>().FirstOrDefault();
            if (capability is null)
            {
                continue;
            }

            if (registration.Enabled && registration.LoadError is null)
            {
                wanted[registration.Manifest.Id] = capability.Definition;
            }
            else
            {
                _statuses.Get(registration.Manifest.Id).SetBackgroundService(null);
            }
        }

        foreach (var (pluginId, runner) in _runners.ToList())
        {
            if (!wanted.ContainsKey(pluginId)
                || runner.Generation != _statuses.Get(pluginId).Generation
                || (runner.Loop.IsCompleted && !_statuses.Get(pluginId).IsPaused(PluginHostMessages.BackgroundProblemKey)))
            {
                await StopAsync(runner).ConfigureAwait(false);
                _runners.Remove(pluginId);
            }
        }

        foreach (var (pluginId, definition) in wanted)
        {
            var status = _statuses.Get(pluginId);
            if (_runners.ContainsKey(pluginId) || status.IsPaused(PluginHostMessages.BackgroundProblemKey))
            {
                continue;
            }

            var directory = PluginDirectoryOf(_catalog.Get(pluginId));
            var runner = new Runner(pluginId, status.Generation, new CancellationTokenSource());
            _runners[pluginId] = runner;
            status.SetBackgroundService("starting");
            runner.Loop = Task.Run(() => RunLoopAsync(runner, definition, directory, status), CancellationToken.None);
        }
    }

    /// <summary>Stops every program (used when the Engine stops, and by tests).</summary>
    public async Task StopAllAsync()
    {
        await Task.WhenAll(_runners.Values.ToList().Select(StopAsync)).ConfigureAwait(false);
        _runners.Clear();
    }

    private static string? PluginDirectoryOf(PluginRegistration? registration) =>
        registration?.ManifestPath is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;

    private async Task StopAsync(Runner runner)
    {
        await runner.Cancellation.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(runner.Loop, Task.Delay(_limits.BackgroundStopGrace + _limits.ServiceStopGrace)).ConfigureAwait(false);
        _endpoints.TryRemove(runner.PluginId, out _);
        _keys.Revoke(runner.PluginId);
        _statuses.Get(runner.PluginId).SetBackgroundService(null);
        runner.Cancellation.Dispose();
    }

    private async Task RunLoopAsync(Runner runner, ServiceDefinition definition, string? pluginDirectory, PluginHostStatus status)
    {
        try
        {
            await RunLoopCoreAsync(runner, definition, pluginDirectory, status).ConfigureAwait(false);
        }
        catch (Exception ex) when (!runner.Cancellation.IsCancellationRequested)
        {
            // Whatever went wrong, the card must not stay on "running": say so, and let the next check start it again.
            _logger.LogError(ex, "Plugin {PluginId} program supervision failed", runner.PluginId);
            status.SetLastProblem("Its program could not be supervised: " + ex.Message);
            status.SetBackgroundService("restarting");
        }
        catch (OperationCanceledException)
        {
            // Asked to stop.
        }
    }

    private async Task RunLoopCoreAsync(Runner runner, ServiceDefinition definition, string? pluginDirectory, PluginHostStatus status)
    {
        var token = runner.Cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            string crash;
            Process? process = null;
            ServiceEndpoint? endpoint = null;
            try
            {
                var last = new LastLine();
                if (definition.IsSupervised)
                {
                    var port = FreePort();
                    var key = _keys.Issue(runner.PluginId);
                    endpoint = new ServiceEndpoint(new Uri($"http://127.0.0.1:{port}"), key);
                    process = Launch(runner.PluginId, definition, pluginDirectory, port, key, last);
                }
                else
                {
                    endpoint = new ServiceEndpoint(definition.Address!, null);
                }

                crash = await WatchAsync(runner, definition, endpoint!, process, last, status, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin {PluginId} program could not be started", runner.PluginId);
                crash = ex.Message;
            }
            finally
            {
                // Only this run's own endpoint and key: a slow-to-finish old run must not take its replacement's away.
                if (endpoint is not null)
                {
                    _endpoints.TryRemove(new KeyValuePair<string, ServiceEndpoint>(runner.PluginId, endpoint));
                    if (endpoint.Key is not null)
                    {
                        _keys.Revoke(runner.PluginId, endpoint.Key);
                    }
                }

                await EndProcessAsync(process).ConfigureAwait(false);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            var crashes = status.RecordCrash(_clock.GetUtcNow(), _limits.BackgroundCrashWindow);
            status.SetLastProblem($"Its program stopped working: {crash}");
            if (crashes >= _limits.BackgroundCrashesBeforeStop)
            {
                status.Pause(PluginHostMessages.BackgroundProblemKey);
                status.SetProblem(PluginHostMessages.BackgroundProblemKey, PluginHostSeverity.Broken, PluginHostMessages.BackgroundStopped(_limits));
                status.SetBackgroundService("stopped");
                _logger.LogError("Plugin {PluginId} program stopped after {Crashes} crashes inside {Window}", runner.PluginId, crashes, _limits.BackgroundCrashWindow);
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
        }
    }

    /// <summary>Waits for the program to become healthy, then keeps checking it. Returns the plain reason it needs a restart.</summary>
    private async Task<string> WatchAsync(
        Runner runner,
        ServiceDefinition definition,
        ServiceEndpoint endpoint,
        Process? process,
        LastLine last,
        PluginHostStatus status,
        CancellationToken token)
    {
        var started = _clock.GetUtcNow();
        var healthy = false;
        var failures = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (process is { HasExited: true })
            {
                return $"it exited with code {process.ExitCode}{last.Suffix}";
            }

            if (process is not null && ExceedsMemory(process, definition.MaxMemoryMb))
            {
                return $"it used more than {definition.MaxMemoryMb} MB of memory";
            }

            var answered = await IsHealthyAsync(endpoint, definition, token).ConfigureAwait(false);
            if (answered)
            {
                failures = 0;
                if (!healthy)
                {
                    healthy = true;
                    runner.Endpoint = endpoint;
                    _endpoints[runner.PluginId] = endpoint;
                    status.SetBackgroundService("running");
                    status.ClearProblem(PluginHostMessages.BackgroundProblemKey);
                }
            }
            else if (!healthy)
            {
                if (_clock.GetUtcNow() - started > TimeSpan.FromSeconds(definition.StartupSeconds))
                {
                    return $"it did not answer within {definition.StartupSeconds} seconds of starting{last.Suffix}";
                }
            }
            else if (++failures >= _limits.ServiceHealthFailuresBeforeRestart)
            {
                _endpoints.TryRemove(new KeyValuePair<string, ServiceEndpoint>(runner.PluginId, endpoint));
                return "it stopped answering health checks";
            }

            await Task.Delay(healthy ? _limits.ServiceHealthInterval : TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsHealthyAsync(ServiceEndpoint endpoint, ServiceDefinition definition, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ServiceUrls.Build(endpoint.BaseAddress, definition.HealthPath));
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool ExceedsMemory(Process process, int maxMb)
    {
        try
        {
            // The program and everything it started count together, so a launcher cannot hide the real work from the limit.
            return ProcessTreeMemory.Measure(process) > (long)maxMb * 1024 * 1024;
        }
        catch (InvalidOperationException)
        {
            return false; // it ended between the checks; the exit is reported on the next pass
        }
    }

    private Process Launch(string pluginId, ServiceDefinition definition, string? pluginDirectory, int port, string key, LastLine last)
    {
        var command = definition.Command!;
        var startInfo = new ProcessStartInfo
        {
            FileName = (command.Contains('/') || command.Contains('\\')) && pluginDirectory is not null
                ? Path.GetFullPath(Path.Combine(pluginDirectory, command))
                : command,
            WorkingDirectory = pluginDirectory ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in definition.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The program gets a small, fixed environment, not the Engine's: no data-store paths, keys or tokens the Engine runs with.
        var inherited = startInfo.Environment.Where(pair => InheritedVariables.Contains(pair.Key) || pair.Key.StartsWith("LC_", StringComparison.Ordinal)).ToList();
        startInfo.Environment.Clear();
        foreach (var (name, value) in inherited)
        {
            startInfo.Environment[name] = value;
        }

        startInfo.RedirectStandardInput = true;
        startInfo.Environment["TUVIMA_PLUGIN_ID"] = pluginId;
        startInfo.Environment["TUVIMA_PLUGIN_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["TUVIMA_PLUGIN_KEY"] = key;
        if (_libraryUrl() is { } libraryUrl)
        {
            startInfo.Environment["TUVIMA_LIBRARY_URL"] = libraryUrl;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => last.Record(e.Data);
        process.ErrorDataReceived += (_, e) => last.Record(e.Data);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"the program \"{command}\" did not start");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException($"the program \"{command}\" could not be started on this machine ({ex.Message})");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _logger.LogInformation("Started program for plugin {PluginId} (process {ProcessId}) on port {Port}", pluginId, process.Id, port);
        return process;
    }

    private async Task EndProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var grace = new CancellationTokenSource(_limits.ServiceStopGrace);
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Plugin program was already gone or would not stop cleanly");
        }
        finally
        {
            process.Dispose();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The last line a program printed, kept so a crash message can say why.</summary>
    private sealed class LastLine
    {
        private volatile string? _line;

        public void Record(string? line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                _line = line.Length > 200 ? line[..200] : line;
            }
        }

        public string Suffix => _line is null ? string.Empty : $" (last message: {_line})";
    }
}
