using MediaEngine.Api.Services.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Plugin always-on work: it starts when the plugin is switched on and approved, stops when it is switched off or the Engine
/// stops, restarts after a crash with a growing pause, and stops for good (Broken) at the fifth crash inside an hour.
/// </summary>
public sealed class PluginBackgroundHostTests : IDisposable
{
    private static readonly PluginHostLimits Quick = PluginHostLimits.Default with
    {
        BackgroundRestartBackoff = [TimeSpan.FromMilliseconds(10)],
        BackgroundStopGrace = TimeSpan.FromSeconds(2),
    };

    private readonly List<IDisposable> _disposables = [];
    private readonly List<PluginBackgroundHost> _hosts = [];

    private HostServicesHarness NewHost(PluginHostLimits? limits = null, params HostServicesTestPlugin[] plugins)
    {
        var host = new HostServicesHarness(limits ?? Quick, plugins);
        _disposables.Add(host);
        return host;
    }

    private PluginBackgroundHost NewBackground(HostServicesHarness host)
    {
        var background = host.NewBackgroundHost();
        _hosts.Add(background);
        return background;
    }

    [Fact]
    public async Task AlwaysOnWork_StartsWhenThePluginIsSwitchedOn_AndNotBefore()
    {
        var plugin = new HostServicesTestPlugin();
        var service = plugin.RunInBackground((_, _, token) => Task.Delay(Timeout.Infinite, token));
        var host = NewHost(plugins: plugin);
        var background = NewBackground(host);

        await background.ReconcileAsync();
        await Task.Delay(100);
        Assert.Equal(0, service.Starts);

        host.Enable();
        await background.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => service.Starts == 1, "the work to start");
        Assert.Equal("running", host.Statuses.Get(plugin.Manifest.Id).Describe().BackgroundService);
    }

    [Fact]
    public async Task AlwaysOnWork_IsToldToStopWhenThePluginIsSwitchedOff()
    {
        var plugin = new HostServicesTestPlugin();
        var service = plugin.RunInBackground((_, _, token) => Task.Delay(Timeout.Infinite, token));
        var host = NewHost(plugins: plugin);
        host.Enable();
        var background = NewBackground(host);
        await background.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => service.Starts == 1, "the work to start");

        host.Disable();
        await background.ReconcileAsync();

        Assert.Equal(1, service.Cancelled);
        Assert.Null(host.Statuses.Get(plugin.Manifest.Id).Describe().BackgroundService);
        await Task.Delay(100);
        Assert.Equal(1, service.Starts);
    }

    [Fact]
    public async Task AlwaysOnWork_ThatIgnoresTheRequestToStop_IsNotStartedAgainUntilItHasReallyEnded_AndTheCardSaysStopping()
    {
        var limits = Quick with { BackgroundStopGrace = TimeSpan.FromMilliseconds(100) };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new HostServicesTestPlugin();
        // The first run never looks at the stop request; a later run behaves.
        var service = plugin.RunInBackground((start, _, token) => start == 1 ? release.Task : Task.Delay(Timeout.Infinite, token));
        var host = NewHost(limits, plugin);
        host.Enable();
        var background = NewBackground(host);
        await background.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => service.Starts == 1, "the work to start");

        host.Disable();
        await background.ReconcileAsync();
        host.Enable();
        await background.ReconcileAsync();
        await Task.Delay(150);
        await background.ReconcileAsync();

        Assert.Equal(1, service.Starts);
        Assert.Equal("stopping", host.Statuses.Get(plugin.Manifest.Id).Describe().BackgroundService);

        release.SetResult();
        await HostServicesHarness.EventuallyAsync(
            () =>
            {
                background.ReconcileAsync().GetAwaiter().GetResult();
                return service.Starts == 2;
            },
            "a fresh run once the old one has ended");
        Assert.Equal("running", host.Statuses.Get(plugin.Manifest.Id).Describe().BackgroundService);
    }

    [Fact]
    public async Task SavingThePluginsSettings_RestartsItsAlwaysOnWork_WithTheNewValue()
    {
        var plugin = new HostServicesTestPlugin();
        var seen = new List<string>();
        var service = plugin.RunInBackground((_, context, token) =>
        {
            lock (seen)
            {
                seen.Add(context.Settings.TryGetValue("mode", out var mode) ? mode.GetString() ?? "" : "(none)");
            }

            return Task.Delay(Timeout.Infinite, token);
        });
        var host = NewHost(plugins: plugin);
        host.Enable();
        var background = NewBackground(host);
        await background.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => service.Starts == 1, "the work to start");

        // Nothing changed, so nothing restarts.
        await background.ReconcileAsync();
        await Task.Delay(100);
        Assert.Equal(1, service.Starts);

        host.Catalog.SaveSettings(plugin.Manifest.Id, new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["mode"] = System.Text.Json.JsonDocument.Parse("\"fast\"").RootElement.Clone(),
        });
        await background.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => service.Starts == 2, "the work to restart with the new settings");

        lock (seen)
        {
            Assert.Equal(["(none)", "fast"], seen);
        }

        Assert.Equal(1, service.Cancelled);
    }

    [Fact]
    public async Task AlwaysOnWork_IsToldToStopWhenTheEngineStops()
    {
        var plugin = new HostServicesTestPlugin();
        var service = plugin.RunInBackground((_, _, token) => Task.Delay(Timeout.Infinite, token));
        var host = NewHost(plugins: plugin);
        host.Enable();
        var background = NewBackground(host);
        await background.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => service.Starts == 1, "the work to start");

        await background.StopAllAsync();

        Assert.Equal(1, service.Cancelled);
    }

    [Fact]
    public async Task APluginWithoutTheBackgroundWorkPermission_IsNeverStarted()
    {
        var plugin = new HostServicesTestPlugin(permissions: [PluginPermissionIds.PluginRecords]);
        var service = plugin.RunInBackground((_, _, token) => Task.Delay(Timeout.Infinite, token));
        var host = NewHost(plugins: plugin);
        host.Enable();
        var background = NewBackground(host);

        await background.ReconcileAsync();
        await Task.Delay(150);

        Assert.Equal(0, service.Starts);
        Assert.Null(host.Statuses.Get(plugin.Manifest.Id).Describe().BackgroundService);
    }

    [Fact]
    public async Task ACrash_RestartsTheWorkAfterAGrowingPause()
    {
        var limits = Quick with { BackgroundRestartBackoff = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)] };
        var plugin = new HostServicesTestPlugin();
        var starts = new List<DateTime>();
        var service = plugin.RunInBackground((start, _, token) =>
        {
            lock (starts)
            {
                starts.Add(DateTime.UtcNow);
            }

            return start <= 2 ? throw new InvalidOperationException("The feed vanished") : Task.Delay(Timeout.Infinite, token);
        });
        var host = NewHost(limits, plugin);
        host.Enable();
        var background = NewBackground(host);

        await background.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => service.Starts == 3, "two restarts");

        lock (starts)
        {
            Assert.True(starts[1] - starts[0] >= TimeSpan.FromMilliseconds(220), "first pause is the first backoff");
            Assert.True(starts[2] - starts[1] >= TimeSpan.FromMilliseconds(470), "second pause is the second backoff");
        }

        var description = host.Statuses.Get(plugin.Manifest.Id).Describe();
        Assert.Equal("working", description.State);
        Assert.Equal("Its background work crashed: The feed vanished", description.LastProblem);
        Assert.Equal("running", description.BackgroundService);
    }

    [Fact]
    public void TheDefaultPausesAreFiveSecondsThirtySecondsTwoMinutesAndTenMinutes()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)],
            PluginHostLimits.Default.BackgroundRestartBackoff);
        Assert.Equal(5, PluginHostLimits.Default.BackgroundCrashesBeforeStop);
        Assert.Equal(TimeSpan.FromHours(1), PluginHostLimits.Default.BackgroundCrashWindow);
        Assert.Equal(TimeSpan.FromSeconds(10), PluginHostLimits.Default.BackgroundStopGrace);
    }

    [Fact]
    public async Task WorkThatReturnsWhenItShouldKeepRunning_CountsAsACrashAndRestarts()
    {
        var plugin = new HostServicesTestPlugin();
        var service = plugin.RunInBackground((start, _, token) => start == 1 ? Task.CompletedTask : Task.Delay(Timeout.Infinite, token));
        var host = NewHost(plugins: plugin);
        host.Enable();
        var background = NewBackground(host);

        await background.ReconcileAsync();

        await HostServicesHarness.EventuallyAsync(() => service.Starts == 2, "a restart after the early return");
        Assert.Equal(
            "Its background work crashed: it stopped when it should have kept running",
            host.Statuses.Get(plugin.Manifest.Id).Describe().LastProblem);
    }

    [Fact]
    public async Task TheFifthCrashInAnHour_StopsTheWorkForGood_TheCardIsBroken_AndSwitchingOffAndOnStartsItAgain()
    {
        var plugin = new HostServicesTestPlugin();
        var service = plugin.RunInBackground((_, _, _) => throw new InvalidOperationException("Crashed"));
        var host = NewHost(plugins: plugin);
        host.Enable();
        var background = NewBackground(host);
        await background.ReconcileAsync();

        await HostServicesHarness.EventuallyAsync(
            () => host.Statuses.Get(plugin.Manifest.Id).Describe().State == "broken", "the work to be stopped");

        var description = host.Statuses.Get(plugin.Manifest.Id).Describe();
        Assert.Equal("Stopped after crashing 5 times in an hour", description.Problem);
        Assert.Equal("stopped", description.BackgroundService);
        Assert.Equal(5, service.Starts);
        await background.ReconcileAsync();
        await Task.Delay(150);
        Assert.Equal(5, service.Starts);

        host.Disable();
        host.Enable();
        await background.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => service.Starts > 5, "the work to start again after a fresh switch-on");
    }

    [Fact]
    public async Task CrashesMoreThanAnHourApart_AreNotCountedTogether()
    {
        var plugin = new HostServicesTestPlugin();
        HostServicesHarness? host = null;
        var service = plugin.RunInBackground((_, _, _) =>
        {
            host!.Clock.Advance(TimeSpan.FromMinutes(61));
            throw new InvalidOperationException("Crashed");
        });
        host = NewHost(plugins: plugin);
        host.Enable();
        var background = NewBackground(host);

        await background.ReconcileAsync();

        await HostServicesHarness.EventuallyAsync(() => service.Starts >= 7, "several crashes, each an hour apart");
        Assert.NotEqual("broken", host.Statuses.Get(plugin.Manifest.Id).Describe().State);
    }

    [Fact]
    public async Task TheContext_StaysUsableForTheWholeRun_AndIsCleanedUpWhenTheRunEnds()
    {
        var plugin = new HostServicesTestPlugin(permissions: [.. HostServicesTestPlugin.AllHostPermissions, PluginPermissionIds.PluginStorage]);
        string? workingDirectory = null;
        var wrote = false;
        var service = plugin.RunInBackground(async (_, context, token) =>
        {
            context.Storage.CreateDirectory("scratch");
            workingDirectory = context.Storage.WorkingDirectory;
            await Task.Delay(300, token);
            await context.Records.PutAsync("progress", "last", """{"ok":true}""", token);
            wrote = Directory.Exists(Path.Combine(workingDirectory, "scratch"));
            await Task.Delay(Timeout.Infinite, token);
        });
        var host = NewHost(plugins: plugin);
        host.Enable();
        var background = NewBackground(host);

        await background.ReconcileAsync();
        await HostServicesHarness.EventuallyAsync(() => wrote, "the work to use its context after a pause");
        Assert.True(Directory.Exists(workingDirectory));
        Assert.NotNull(await host.RecordStoreFor(plugin.Manifest.Id).GetAsync("progress", "last"));

        await background.StopAllAsync();
        Assert.Equal(1, service.Cancelled);
        Assert.False(Directory.Exists(workingDirectory));
    }

    public void Dispose()
    {
        foreach (var background in _hosts)
        {
            background.StopAllAsync().GetAwaiter().GetResult();
        }

        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }
}
