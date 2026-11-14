using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class SystemActivityIndicatorTests : AsyncBunitContext
{
    private readonly UniverseStateContainer _state = new();

    public SystemActivityIndicatorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(api);
        Services.AddSingleton(_state);
        Services.AddSingleton(new PlaybackSessionController(null!, api));
        Services.AddSingleton<ActivityNotificationQueue>();
        Services.AddSingleton<ShellActivityState>();
    }

    [Fact]
    public void WorkingQueue_ShowsTheWorkingStateWithTheLivePageWording()
    {
        _state.PushIngestionQueueProgress(Queue("working", found: 508, done: 120, progress: 0.4, secondsLeft: 240));

        var cut = Render<SystemActivityIndicator>();

        var button = cut.Find("button.system-activity-indicator");
        Assert.Contains("is-working", button.ClassList);
        Assert.DoesNotContain("is-idle", button.ClassList);
        Assert.Equal("Found 508 files · 120 of 508 done · About 4 min left", Tooltip(cut));
        Assert.Equal("Working. Found 508 files · 120 of 508 done · About 4 min left", button.GetAttribute("aria-label"));
        Assert.Equal("Working", cut.Find(".sr-only").TextContent);
    }

    [Fact]
    public void WaitingQueue_StaysIdleButTheTooltipUsesTheLivePagesWaitingWords()
    {
        _state.PushIngestionQueueProgress(Queue("waiting", found: 10, done: 0, progress: 0.2, waiting: 10));

        var cut = Render<SystemActivityIndicator>();

        var button = cut.Find("button.system-activity-indicator");
        Assert.Contains("is-idle", button.ClassList);
        Assert.DoesNotContain("is-working", button.ClassList);
        Assert.Equal("Waiting for media tools · 10 files are waiting", Tooltip(cut));
    }

    [Fact]
    public void WorkingQueueWhoseSnapshotIsStale_ShowsIdleInsteadOfWorking()
    {
        _state.PushIngestionQueueProgress(
            Queue("working", found: 508, done: 120, progress: 0.4, secondsLeft: 240),
            DateTimeOffset.UtcNow.AddSeconds(-16));

        var cut = Render<SystemActivityIndicator>();

        var button = cut.Find("button.system-activity-indicator");
        Assert.Contains("is-idle", button.ClassList);
        Assert.DoesNotContain("is-working", button.ClassList);
        Assert.Equal("Library is up to date", Tooltip(cut));
    }

    [Fact]
    public void WorkingQueueWhoseSnapshotIsJustUnderTheLimit_StillShowsWorking()
    {
        _state.PushIngestionQueueProgress(
            Queue("working", found: 508, done: 120, progress: 0.4, secondsLeft: 240),
            DateTimeOffset.UtcNow.AddSeconds(-10));

        var cut = Render<SystemActivityIndicator>();

        Assert.Contains("is-working", cut.Find("button.system-activity-indicator").ClassList);
    }

    [Theory]
    [InlineData(EngineConnectionState.Reconnecting)]
    [InlineData(EngineConnectionState.LiveUpdatesDisconnected)]
    [InlineData(EngineConnectionState.Offline)]
    public void WorkingQueueWithAFreshSnapshotWhileLiveUpdatesAreOff_StillShowsWorking(EngineConnectionState down)
    {
        _state.PushIngestionQueueProgress(Queue("working", found: 508, done: 120, progress: 0.4, secondsLeft: 240));
        _state.SetEngineConnectionState(down);

        var cut = Render<SystemActivityIndicator>();

        Assert.Contains("is-working", cut.Find("button.system-activity-indicator").ClassList);
    }

    [Fact]
    public async Task ActivityState_ReadsTheQueueAgainWhileItLooksBusyAndStopsLookingBusyWhenItIsIdle()
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionQueueProgressAsync), _ =>
                Task.FromResult<IngestionQueueProgressDto?>(Queue("idle", found: 0, done: 0, progress: 1))));
        using var activity = new ShellActivityState(
            _state,
            new PlaybackSessionController(null!, api),
            api,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShellActivityState>.Instance);
        _state.PushIngestionQueueProgress(
            Queue("working", found: 508, done: 120, progress: 0.4),
            DateTimeOffset.UtcNow.AddSeconds(-14));
        Assert.True(activity.Current.IsWorking);

        await activity.InitializeAsync();

        Assert.False(activity.Current.IsWorking);
        Assert.Equal("idle", _state.IngestionQueueProgress!.State);
    }

    [Fact]
    public async Task ActivityState_WithLiveUpdatesOffAndNoSnapshotYet_ReadsTheQueueAndShowsWorking()
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionQueueProgressAsync), _ =>
                Task.FromResult<IngestionQueueProgressDto?>(Queue("working", found: 36, done: 15, progress: 0.6))));
        using var activity = new ShellActivityState(
            _state,
            new PlaybackSessionController(null!, api),
            api,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShellActivityState>.Instance);
        _state.SetEngineConnectionState(EngineConnectionState.LiveUpdatesDisconnected);
        Assert.False(activity.Current.IsWorking);

        await activity.InitializeAsync();

        Assert.True(activity.Current.IsWorking);
    }

    [Fact]
    public async Task ActivityState_WithLiveUpdatesOffAndAnIdleSnapshot_ReadsTheQueueOnTheTimerAndShowsWorkingOnceTheEngineReportsIt()
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionQueueProgressAsync), _ =>
                Task.FromResult<IngestionQueueProgressDto?>(Queue("working", found: 36, done: 15, progress: 0.6))));
        using var activity = new ShellActivityState(
            _state,
            new PlaybackSessionController(null!, api),
            api,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShellActivityState>.Instance);
        _state.PushIngestionQueueProgress(Queue("idle", found: 0, done: 0, progress: 1));
        _state.SetEngineConnectionState(EngineConnectionState.LiveUpdatesDisconnected);
        Assert.False(activity.Current.IsWorking);

        activity.OnTimerTick();

        Assert.True(await WaitUntilAsync(() => activity.Current.IsWorking));
        Assert.Equal("working", _state.IngestionQueueProgress!.State);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public void IdleQueue_ShowsTheIdleStateAndTheUpToDateLine()
    {
        _state.PushIngestionQueueProgress(Queue("idle", found: 0, done: 0, progress: 1));

        var cut = Render<SystemActivityIndicator>();

        var button = cut.Find("button.system-activity-indicator");
        Assert.Contains("is-idle", button.ClassList);
        Assert.Equal("Library is up to date", Tooltip(cut));
        Assert.Equal("Idle. Library is up to date", button.GetAttribute("aria-label"));
        Assert.Equal("Idle", cut.Find(".sr-only").TextContent);
        Assert.Empty(cut.FindAll(".system-activity-indicator__count"));
    }

    [Fact]
    public void NoSnapshotYet_ShowsTheIdleState()
    {
        var cut = Render<SystemActivityIndicator>();

        Assert.Contains("is-idle", cut.Find("button.system-activity-indicator").ClassList);
        Assert.Equal("Library is up to date", Tooltip(cut));
    }

    [Fact]
    public void EnrichmentAlone_ShowsTheWorkingState()
    {
        _state.PushIngestionQueueProgress(Queue("idle", found: 0, done: 0, progress: 1));
        _state.PushUniverseEnrichmentProgress(new UniverseEnrichmentProgressEvent("Q1", "Dune", 1, 4, "people"));

        var cut = Render<SystemActivityIndicator>();

        Assert.Contains("is-working", cut.Find("button.system-activity-indicator").ClassList);
        Assert.Equal("Enriching Dune (25%)", Tooltip(cut));
    }

    [Fact]
    public void MediaOperationAlone_ShowsTheWorkingState()
    {
        _state.SetMediaOperationActivity(
        [
            new MediaOperationChangedEvent(Guid.NewGuid(), "writeback.metadata", "writeback", "running", null, 0, 0, 0, DateTimeOffset.UtcNow),
        ]);

        var cut = Render<SystemActivityIndicator>();

        Assert.Contains("is-working", cut.Find("button.system-activity-indicator").ClassList);
        Assert.Equal("Writing media metadata", Tooltip(cut));
    }

    [Fact]
    public void WorkingQueueWithOtherWork_ListsTheIngestionLineFirstAndCountsBoth()
    {
        _state.PushIngestionQueueProgress(Queue("working", found: 5, done: 1, progress: 0.2, secondsLeft: 30));
        _state.PushUniverseEnrichmentProgress(new UniverseEnrichmentProgressEvent("Q1", "Dune", 1, 4, "people"));

        var cut = Render<SystemActivityIndicator>();

        Assert.Equal("Found 5 files · 1 of 5 done · Less than a minute left • Enriching Dune (25%)", Tooltip(cut));
        Assert.Equal("2", cut.Find(".system-activity-indicator__count").TextContent);
    }

    [Fact]
    public void PlaybackWithAnIdleLibrary_StaysIdleAndDoesNotLightTheIndicator()
    {
        _state.PushIngestionQueueProgress(Queue("idle", found: 0, done: 0, progress: 1));
        var activity = Services.GetRequiredService<ShellActivityState>();
        activity.ReportVideoPlayback(Guid.NewGuid(), "A film", 12, 600, isPlaying: true, ended: false);

        var cut = Render<SystemActivityIndicator>();

        Assert.True(activity.IsPlaybackActive);
        var button = cut.Find("button.system-activity-indicator");
        Assert.Contains("is-idle", button.ClassList);
        Assert.Equal("Library is up to date", Tooltip(cut));
    }

    [Fact]
    public void Click_AlwaysOpensTheLivePageWhenTheAdministratorMayOpenIt()
    {
        var cut = Render<SystemActivityIndicator>(p => p.Add(c => c.CanOpenSystemActivity, true));

        cut.Find("button.system-activity-indicator").Click();

        Assert.EndsWith("/settings/library-activity", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    [Fact]
    public void IngestionText_ShowsHowLongItHasBeenRunningWhenThereIsNoTrustedEstimate()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var queue = Queue("working", found: 508, done: 120, progress: 0.3);
        queue.StartedAt = now.AddMinutes(-4);

        Assert.Equal("Found 508 files · 120 of 508 done · Running for 4 min", SystemActivityText.Ingestion(queue, now));

        queue.StartedAt = now.AddSeconds(-20);
        Assert.Equal("Found 508 files · 120 of 508 done · Running for less than a minute", SystemActivityText.Ingestion(queue, now));
    }

    [Fact]
    public void IdleText_AddsTheLastActivityTimeWhenKnown()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var queue = Queue("idle", found: 0, done: 0, progress: 1);
        queue.LastActivityAt = now.AddMinutes(-3);

        Assert.Equal("Library is up to date · Last activity 3 minutes ago", SystemActivityText.Idle(queue, now));
        Assert.Equal("Library is up to date", SystemActivityText.Idle(null, now));
    }

    [Fact]
    public void IndicatorStyles_UseTheBlueAndGreenTokensForStateAndTheWarningTokenOnlyForNeedsAttention()
    {
        var css = File.ReadAllText(RepoFile("SystemActivityIndicator.razor.css"));
        var markup = File.ReadAllText(RepoFile("SystemActivityIndicator.razor"));

        Assert.Contains("var(--tl-accent-secondary)", css);
        Assert.Contains("var(--tl-status-success)", css);
        foreach (var forbidden in new[]
                 {
                     "is-playback", "is-warning", "is-active", "--playback-accent", "--tl-accent-primary",
                     "#ff9657", "amber", "orange",
                 })
        {
            Assert.DoesNotContain(forbidden, css, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, markup, StringComparison.OrdinalIgnoreCase);
        }

        // Needs attention (the last update ended with failures) is the one place the warning tone is used.
        var warningUses = css.Split("--tl-status-warning").Length - 1;
        Assert.Equal(1, warningUses);
        Assert.Contains(".is-attention {" + (css.Contains("\r\n") ? "\r\n" : "\n") + "    color: var(--tl-status-warning);", css);

        Assert.DoesNotContain("ShellActivityKind.Playback", markup);
        Assert.DoesNotContain("PlaybackSessionController", markup);
    }

    // A library update that ends with failures turns the indicator amber (never a pop-up) until Activity is opened or a later update is clean.
    private void FinishARun(int failed)
    {
        Services.GetRequiredService<ShellActivityState>();
        _state.PushIngestionQueueProgress(new IngestionQueueProgressDto { State = "idle", UpdatedAt = DateTimeOffset.UtcNow });
        _state.PushIngestionQueueProgress(Queue("working", found: 10, done: 2, progress: 0.2));
        _state.PushIngestionQueueProgress(new IngestionQueueProgressDto
        {
            State = "idle",
            UpdatedAt = DateTimeOffset.UtcNow,
            Outcomes = new IngestionQueueOutcomesDto { Failed = failed },
        });
    }

    [Fact]
    public void ARunThatEndedWithFailures_TurnsTheIdleIndicatorAmberAndSaysSoInItsName()
    {
        FinishARun(failed: 2);

        var cut = Render<SystemActivityIndicator>(p => p.Add(i => i.CanOpenSystemActivity, true));

        var button = cut.Find("button.system-activity-indicator");
        Assert.Contains("is-attention", button.ClassList);
        Assert.DoesNotContain("is-idle", button.ClassList);
        Assert.Equal("Needs attention", cut.Find(".sr-only").TextContent);
        Assert.StartsWith("Needs attention. The last library update needs attention.", button.GetAttribute("aria-label"));
        Assert.Equal("The last library update needs attention. Open Activity to see what happened.", Tooltip(cut));
    }

    [Fact]
    public void ARunThatEndedWithFailures_ShowsNothingExtraToSomeoneWhoCannotOpenActivity()
    {
        FinishARun(failed: 1);

        var cut = Render<SystemActivityIndicator>();

        Assert.Contains("is-idle", cut.Find("button.system-activity-indicator").ClassList);
    }

    [Fact]
    public void OpeningActivityFromTheIndicator_ClearsTheNeedsAttentionState()
    {
        FinishARun(failed: 1);
        var cut = Render<SystemActivityIndicator>(p => p.Add(i => i.CanOpenSystemActivity, true));
        Assert.Contains("is-attention", cut.Find("button.system-activity-indicator").ClassList);

        cut.Find("button.system-activity-indicator").Click();

        Assert.False(Services.GetRequiredService<ActivityNotificationQueue>().NeedsAttention);
        cut.WaitForAssertion(() => Assert.DoesNotContain("is-attention", cut.Find("button.system-activity-indicator").ClassList));
    }

    [Fact]
    public void OpeningTheActivityPage_ClearsTheNeedsAttentionState()
    {
        FinishARun(failed: 1);
        var activity = Services.GetRequiredService<ShellActivityState>();
        var cut = Render<SystemActivityIndicator>(p => p.Add(i => i.CanOpenSystemActivity, true));
        Assert.Contains("is-attention", cut.Find("button.system-activity-indicator").ClassList);

        // The Activity page does this when it opens (LibraryActivityPage.OnInitializedAsync).
        activity.AcknowledgeAttention();

        cut.WaitForAssertion(() => Assert.Contains("is-idle", cut.Find("button.system-activity-indicator").ClassList));
        Assert.Contains("Shell.AcknowledgeAttention()", File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Settings", "LibraryActivity", "LibraryActivityPage.razor")));
    }

    [Fact]
    public void ALaterRunThatEndsCleanly_ClearsTheNeedsAttentionState()
    {
        FinishARun(failed: 1);
        var cut = Render<SystemActivityIndicator>(p => p.Add(i => i.CanOpenSystemActivity, true));
        Assert.Contains("is-attention", cut.Find("button.system-activity-indicator").ClassList);

        _state.PushIngestionQueueProgress(Queue("working", found: 10, done: 2, progress: 0.2));
        _state.PushIngestionQueueProgress(new IngestionQueueProgressDto { State = "idle", UpdatedAt = DateTimeOffset.UtcNow });

        cut.WaitForAssertion(() => Assert.Contains("is-idle", cut.Find("button.system-activity-indicator").ClassList));
    }

    // Library activity D32: the top bar's ring and the progress card's bar show one number, and both draw the one shared activity icon.
    [Theory]
    [InlineData(0.4, 40)]
    [InlineData(0.996, 99)]
    public void TheTopBarRingAndTheProgressCardShowTheSamePercent_AndTheSameSharedIcon(double progress, int expected)
    {
        var queue = Queue("working", found: 538, done: 427, progress: progress);
        _state.PushIngestionQueueProgress(queue);

        var bar = Render<SystemActivityIndicator>();
        var card = Render<MediaEngine.Web.Components.Settings.LibraryActivity.LibraryActivityProgressCard>(parameters => parameters
            .Add(c => c.Queue, queue));

        var ring = bar.Find("button.system-activity-indicator .app-sync-icon svg.tl-progress-circular");
        Assert.Equal(expected.ToString(), ring.GetAttribute("aria-valuenow"));
        Assert.Equal($"{expected}%", string.Join(' ', card.Find(".app-progress-bar__value").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        Assert.NotNull(bar.Find(".app-sync-icon .is-spinning"));
        Assert.NotNull(card.Find(".app-sync-icon .is-spinning"));
    }

    private static string Tooltip(IRenderedComponent<SystemActivityIndicator> cut) =>
        cut.Find("[data-playback-tooltip]").GetAttribute("data-playback-tooltip")!;

    private static IngestionQueueProgressDto Queue(
        string state, int found, int done, double progress, int? secondsLeft = null, int waiting = 0)
    {
        return new IngestionQueueProgressDto
        {
            State = state,
            FilesFound = found,
            FilesDone = done,
            FilesWaiting = waiting,
            Progress = progress,
            EstimatedSecondsRemaining = secondsLeft,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string RepoFile(string name) => Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Navigation", name);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
