using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The live Ingestion page and the Overview show the Engine's queue snapshot as sent: the same headline, tally, status
/// and time words, no arithmetic of their own.
/// </summary>
public sealed class IngestionQueueProgressDisplayTests : AsyncBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly UniverseStateContainer _state = new();
    private readonly List<string> _calls = [];
    private Func<Task<IngestionPresentationSnapshotDto?>> _presentation =
        () => Task.FromResult<IngestionPresentationSnapshotDto?>(new IngestionPresentationSnapshotDto());
    private Func<Task<IngestionQueueProgressDto?>> _queue = () => Task.FromResult<IngestionQueueProgressDto?>(null);

    public IngestionQueueProgressDisplayTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionPresentationAsync), _ =>
            {
                _calls.Add("presentation");
                return _presentation();
            });
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionQueueProgressAsync), _ =>
            {
                _calls.Add("queue");
                return _queue();
            });
        });
        Services.AddSingleton(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(_state);
        Services.AddSingleton(provider => new UIOrchestratorService(
            api,
            _state,
            new ActiveProfileSessionService(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton(provider => new IngestionLiveDashboardState(
            api,
            provider.GetRequiredService<UIOrchestratorService>(),
            _state));
        Services.AddSingleton(provider => new ContextSidebarPreferences(
            provider.GetRequiredService<UIOrchestratorService>(),
            new NoPlaybackPreferences()));
        Services.AddSingleton<ContextSidebarCoordinator>();
        Render<AppPopoverHost>();
    }

    [Fact]
    public void Working_ShowsFilesFoundAndTheTallyByTypeExactlyAsTheSnapshotSays()
    {
        var cut = RenderTab(Working());

        Assert.Equal("Found 508 files", cut.Find(".ingestion-status h2").TextContent);
        Assert.Equal(
            "212 movies, 140 episodes, 96 songs, 31 books, 29 still being sorted",
            cut.Find(".ingestion-status__tally").TextContent);
        Assert.DoesNotContain("0 items", cut.Find(".ingestion-status").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Working_WithOneFileOfOneType_UsesSingularNouns()
    {
        var snapshot = Working();
        snapshot.FilesFound = 1;
        snapshot.FilesDone = 0;
        snapshot.FilesUnsorted = 0;
        snapshot.Tally = [new IngestionQueueTypeTallyDto { MediaType = "Audiobooks", Files = 1 }];

        var cut = RenderTab(snapshot);

        Assert.Equal("Found 1 file", cut.Find(".ingestion-status h2").TextContent);
        Assert.Equal("1 audiobook file", cut.Find(".ingestion-status__tally").TextContent);
    }

    [Fact]
    public void Working_WithAnUnknownMediaType_NamesItInLowerCase()
    {
        var snapshot = Working();
        snapshot.Tally = [new IngestionQueueTypeTallyDto { MediaType = "Photos", Files = 4 }];
        snapshot.FilesUnsorted = 0;

        var cut = RenderTab(snapshot);

        Assert.Equal("4 photos", cut.Find(".ingestion-status__tally").TextContent);
    }

    [Fact]
    public void Working_WithFilesThatJoinedLater_ShowsTheMoreFoundLine()
    {
        var snapshot = Working();
        snapshot.FilesMoreFound = 120;

        var cut = RenderTab(snapshot);

        Assert.Equal("120 more found", cut.Find(".ingestion-status__more").TextContent);
    }

    [Fact]
    public void Working_WithoutMoreFiles_ShowsNoMoreFoundLine()
    {
        var cut = RenderTab(Working());

        Assert.Empty(cut.FindAll(".ingestion-status__more"));
    }

    [Fact]
    public void Working_ShowsOneBlueBarAtTheEnginesProgressAndTheDoneLine()
    {
        var cut = RenderTab(Working());

        var track = cut.Find(".ingestion-status .app-progress-bar__track");
        Assert.Equal("50", track.GetAttribute("aria-valuenow"));
        Assert.Single(cut.FindAll(".ingestion-status .app-progress-bar"));
        Assert.Empty(cut.FindAll(".app-progress-bar--indeterminate"));
        Assert.Contains("--app-progress-accent: var(--tl-accent-secondary)", cut.Find(".ingestion-status .app-progress-bar").GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal("254 of 508 done · 3 need review · 1 failed", cut.Find(".ingestion-status__line").TextContent);
    }

    [Fact]
    public void Working_WithoutAReliableEstimate_ShowsHowLongItHasBeenRunning()
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = null;
        snapshot.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4).AddSeconds(-10);

        var cut = RenderTab(snapshot);

        Assert.StartsWith("Running for 4m", cut.Find(".ingestion-status__meta").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("left", cut.Find(".ingestion-status").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Working_WithAnEstimate_ShowsTheEstimateInsteadOfElapsedTime()
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = 330;

        var cut = RenderTab(snapshot);

        Assert.Equal("About 6 min left", cut.Find(".ingestion-status__meta").TextContent);
        Assert.DoesNotContain("Running for", cut.Find(".ingestion-status").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "Less than a minute left")]
    [InlineData(59, "Less than a minute left")]
    [InlineData(60, "About 1 min left")]
    [InlineData(61, "About 2 min left")]
    [InlineData(600, "About 10 min left")]
    [InlineData(3600, "About 1 hr left")]
    [InlineData(7800, "About 2 hr 10 min left")]
    public void TimeLine_WithAnEstimate_RoundsUpToWholeMinutes(int seconds, string expected)
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = seconds;

        Assert.Equal(expected, IngestionQueueProgressText.TimeLine(snapshot, Now));
    }

    [Fact]
    public void TimeLine_WithNeitherEstimateNorStart_ShowsNothing()
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = null;
        snapshot.StartedAt = null;

        Assert.Null(IngestionQueueProgressText.TimeLine(snapshot, Now));
    }

    [Fact]
    public void TimeLine_WithoutAnEstimate_FormatsElapsedHoursAndMinutes()
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = null;
        snapshot.StartedAt = Now.AddMinutes(-135);

        Assert.Equal("Running for 2h 15m", IngestionQueueProgressText.TimeLine(snapshot, Now));
    }

    [Fact]
    public void Waiting_NamesTheMissingToolsCountsTheWaitingFilesAndLinksToTheToolsPage()
    {
        var snapshot = new IngestionQueueProgressDto
        {
            State = "waiting",
            FilesFound = 40,
            FilesDone = 10,
            FilesWaiting = 30,
            WaitingFor = ["ffmpeg"],
            Progress = 0.25,
        };

        var cut = RenderTab(snapshot);

        var panel = cut.Find(".ingestion-status");
        Assert.Equal("Waiting for media tools", cut.Find(".ingestion-status h2").TextContent);
        Assert.Equal("30 files are waiting", cut.Find(".ingestion-status__line").TextContent);
        var button = cut.Find(".ingestion-status a");
        Assert.Equal("/settings/tools", button.GetAttribute("href"));
        Assert.Contains("Tools and requirements", button.TextContent, StringComparison.Ordinal);
        Assert.Equal("Paused", cut.Find(".ingestion-status .app-progress-bar__label").TextContent);
        Assert.Equal("25", cut.Find(".ingestion-status .app-progress-bar__track").GetAttribute("aria-valuenow"));
        Assert.DoesNotContain("0 items", panel.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("left", panel.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", panel.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Waiting_ForAnotherTool_NamesThatTool()
    {
        var snapshot = new IngestionQueueProgressDto { State = "waiting", FilesFound = 3, FilesWaiting = 3, WaitingFor = ["pdfium"], Progress = 0.1 };

        var cut = RenderTab(snapshot);

        Assert.Equal("Waiting for pdfium", cut.Find(".ingestion-status h2").TextContent);
    }

    [Fact]
    public void Idle_SaysTheLibraryIsUpToDateWithAGreenDotAndTheLastActivity()
    {
        var snapshot = new IngestionQueueProgressDto
        {
            State = "idle",
            Progress = 1,
            LastActivityAt = DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(-5),
        };

        var cut = RenderTab(snapshot);

        Assert.Equal("Library is up to date", cut.Find(".ingestion-status h2").TextContent);
        Assert.Single(cut.FindAll(".ingestion-status__dot"));
        Assert.Equal("Last activity 5 minutes ago", cut.Find(".ingestion-status__meta").TextContent);
        Assert.Empty(cut.FindAll(".ingestion-status .app-progress-bar"));
    }

    [Fact]
    public void NoSnapshotYetWhileLoading_ShowsSkeletonsInsteadOfASpinnerPage()
    {
        var pending = new TaskCompletionSource<IngestionPresentationSnapshotDto?>();
        _presentation = () => pending.Task;

        var cut = Render<IngestionTasksTab>();

        Assert.Equal(3, cut.FindAll(".ingestion-status .app-skeleton").Count);
        Assert.Empty(cut.FindAll(".ingestion-status h2"));
        Assert.Equal("true", cut.Find(".ingestion-status").GetAttribute("aria-busy"));
        pending.SetResult(new IngestionPresentationSnapshotDto());
    }

    [Fact]
    public void EngineUnreachable_SaysSoWithRetryAndKeepsTheLastKnownNumbersGreyed()
    {
        _state.PushIngestionQueueProgress(Working());
        _presentation = () => Task.FromException<IngestionPresentationSnapshotDto?>(new HttpRequestException("down"));
        _queue = () => Task.FromResult<IngestionQueueProgressDto?>(null);

        var cut = Render<IngestionTasksTab>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("The Library's engine isn't responding", cut.Find(".ingestion-status h2").TextContent);
            Assert.Contains("Retry", cut.Find(".ingestion-status button").TextContent, StringComparison.Ordinal);
            var stale = cut.Find(".ingestion-status__stale");
            Assert.Contains("Found 508 files", stale.TextContent, StringComparison.Ordinal);
            Assert.Contains("212 movies", stale.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EngineUnreachable_RetryRunsTheLoadAgainAndRecovers()
    {
        var failing = true;
        _presentation = () => failing
            ? Task.FromException<IngestionPresentationSnapshotDto?>(new HttpRequestException("down"))
            : Task.FromResult<IngestionPresentationSnapshotDto?>(new IngestionPresentationSnapshotDto());
        _queue = () => Task.FromResult<IngestionQueueProgressDto?>(failing ? null : Working());

        var cut = Render<IngestionTasksTab>();
        cut.WaitForAssertion(() => Assert.Equal("The Library's engine isn't responding", cut.Find(".ingestion-status h2").TextContent));
        var loadsBeforeRetry = _calls.Count(call => call == "presentation");

        failing = false;
        cut.Find(".ingestion-status button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(_calls.Count(call => call == "presentation") > loadsBeforeRetry);
            Assert.Equal("Found 508 files", cut.Find(".ingestion-status h2").TextContent);
        });
    }

    [Fact]
    public void QueueSnapshotPushedLater_UpdatesTheOpenPage()
    {
        _queue = () => Task.FromResult<IngestionQueueProgressDto?>(Working());
        var cut = Render<IngestionTasksTab>();
        cut.WaitForAssertion(() => Assert.Equal("Found 508 files", cut.Find(".ingestion-status h2").TextContent));

        var next = Working();
        next.FilesFound = 628;
        next.FilesMoreFound = 120;
        next.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(5);
        _state.PushIngestionQueueProgress(next);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Found 628 files", cut.Find(".ingestion-status h2").TextContent);
            Assert.Equal("120 more found", cut.Find(".ingestion-status__more").TextContent);
        }, TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Overview_ShowsTheSameStatusAndTimeWordsAsTheLivePageForTheSameSnapshot()
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = 330;
        _queue = () => Task.FromResult<IngestionQueueProgressDto?>(snapshot);

        var live = Render<IngestionTasksTab>();
        live.WaitForAssertion(() => Assert.Equal("Found 508 files", live.Find(".ingestion-status h2").TextContent));
        var statusLine = live.Find(".ingestion-status__line").TextContent;
        var timeLine = live.Find(".ingestion-status__meta").TextContent;

        var overview = Render<OverviewTab>();

        overview.WaitForAssertion(() =>
        {
            var row = overview.Find(".admin-system-status__row small").TextContent;
            Assert.Contains(statusLine, row, StringComparison.Ordinal);
            Assert.Contains(timeLine, row, StringComparison.Ordinal);
            Assert.DoesNotContain("Calculating", overview.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Overview_WithoutAnEstimate_ShowsElapsedTimeNeverCalculating()
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = null;
        snapshot.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        _state.PushIngestionQueueProgress(snapshot);

        var overview = Render<OverviewTab>();

        overview.WaitForAssertion(() =>
        {
            Assert.Contains("Running for 3m", overview.Find(".admin-system-status__row small").TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Calculating", overview.Markup, StringComparison.Ordinal);
        });
    }

    private IRenderedComponent<IngestionTasksTab> RenderTab(IngestionQueueProgressDto queue)
    {
        _queue = () => Task.FromResult<IngestionQueueProgressDto?>(queue);
        var cut = Render<IngestionTasksTab>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ingestion-status h2")));
        return cut;
    }

    private static IngestionQueueProgressDto Working() => new()
    {
        State = "working",
        FilesFound = 508,
        FilesDone = 254,
        Progress = 0.5,
        FilesUnsorted = 29,
        Tally =
        [
            new IngestionQueueTypeTallyDto { MediaType = "Movies", Files = 212 },
            new IngestionQueueTypeTallyDto { MediaType = "TV", Files = 140 },
            new IngestionQueueTypeTallyDto { MediaType = "Music", Files = 96 },
            new IngestionQueueTypeTallyDto { MediaType = "Books", Files = 31 },
        ],
        Outcomes = new IngestionQueueOutcomesDto { Added = 250, NeedsReview = 3, Failed = 1 },
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
        LastActivityAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class NoPlaybackPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<MediaEngine.Contracts.Playback.UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<MediaEngine.Contracts.Playback.UserPlaybackSettingsDto?>(null);

        public void UpdateCache(MediaEngine.Contracts.Playback.UserPlaybackSettingsDto settings)
        {
        }

        public void Invalidate()
        {
        }
    }
}
