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
        Assert.Equal("Found 508 files · 120 done · About 4 min left", Tooltip(cut));
        Assert.Equal("Working. Found 508 files · 120 done · About 4 min left", button.GetAttribute("aria-label"));
        Assert.Equal("Working", cut.Find(".sr-only").TextContent);
    }

    [Fact]
    public void WaitingQueue_ShowsTheIdleStateBecauseTheBannerCarriesMissingTools()
    {
        _state.PushIngestionQueueProgress(Queue("waiting", found: 10, done: 0, progress: 0.2, waiting: 10));

        var cut = Render<SystemActivityIndicator>();

        var button = cut.Find("button.system-activity-indicator");
        Assert.Contains("is-idle", button.ClassList);
        Assert.DoesNotContain("is-working", button.ClassList);
        Assert.Equal("Library is up to date", Tooltip(cut));
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

        Assert.Equal("Found 5 files · 1 done · About 1 min left • Enriching Dune (25%)", Tooltip(cut));
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

        Assert.EndsWith("/settings/ingestion", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    [Fact]
    public void IngestionText_ShowsHowLongItHasBeenRunningWhenThereIsNoTrustedEstimate()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var queue = Queue("working", found: 508, done: 120, progress: 0.3);
        queue.StartedAt = now.AddMinutes(-4);

        Assert.Equal("Found 508 files · 120 done · Running for 4 min", SystemActivityText.Ingestion(queue, now));

        queue.StartedAt = now.AddSeconds(-20);
        Assert.Equal("Found 508 files · 120 done · Running for under a minute", SystemActivityText.Ingestion(queue, now));
    }

    [Fact]
    public void IdleText_AddsTheLastActivityTimeWhenKnown()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var queue = Queue("idle", found: 0, done: 0, progress: 1);
        queue.LastActivityAt = now.AddMinutes(-3);

        Assert.Equal("Library is up to date · last activity 3 min ago", SystemActivityText.Idle(queue, now));
        Assert.Equal("Library is up to date", SystemActivityText.Idle(null, now));
    }

    [Fact]
    public void IndicatorStyles_UseOnlyTheBlueAndGreenTokensForState()
    {
        var css = File.ReadAllText(RepoFile("SystemActivityIndicator.razor.css"));
        var markup = File.ReadAllText(RepoFile("SystemActivityIndicator.razor"));

        Assert.Contains("var(--tl-accent-secondary)", css);
        Assert.Contains("var(--tl-status-success)", css);
        foreach (var forbidden in new[]
                 {
                     "is-playback", "is-warning", "is-active", "--playback-accent", "--tl-status-warning", "--tl-accent-primary",
                     "#ff9657", "amber", "orange",
                 })
        {
            Assert.DoesNotContain(forbidden, css, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, markup, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("ShellActivityKind.Playback", markup);
        Assert.DoesNotContain("PlaybackSessionController", markup);
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
