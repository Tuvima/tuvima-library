using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Settings.LibraryActivity;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The built Activity page against the approved mockup (spec library-activity, screens 2 to 4, issue 690): the Live tag, the area chips,
/// one compact row of History controls, month headings that read "November 2026 · 3 batches", left-aligned entries with a subtitle, real
/// pictures with a neutral stand-in, and the progress card's ring.
/// </summary>
public sealed class LibraryActivityMatchesMockupTests : AsyncBunitContext
{
    private static readonly Guid Work = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private readonly UniverseStateContainer _state = new();
    private IReadOnlyList<ActivityRunDto> _runs = [];
    private IReadOnlyList<MediaEngine.Contracts.Settings.LibraryFolderDto> _libraries = [new() { Id = "movies", Name = "Movies" }];

    public LibraryActivityMatchesMockupTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionPresentationAsync), _ => Task.FromResult<IngestionPresentationSnapshotDto?>(new IngestionPresentationSnapshotDto()));
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionQueueProgressAsync), _ => Task.FromResult<IngestionQueueProgressDto?>(new IngestionQueueProgressDto { State = "idle" }));
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<MediaEngine.Contracts.Settings.LibrariesConfigurationDto?>(
                new MediaEngine.Contracts.Settings.LibrariesConfigurationDto { Libraries = [.. _libraries] }));
            stub.SetHandler(nameof(IEngineApiClient.GetActivityRunsAsync), _ => Task.FromResult<ActivityRunsDto?>(new ActivityRunsDto(_runs, false, DateTimeOffset.UtcNow.AddMinutes(-12))));
            stub.SetHandler(nameof(IEngineApiClient.GetActivityRunGroupsAsync), _ => Task.FromResult<ActivityRunGroupsDto?>(new ActivityRunGroupsDto(
                Guid.NewGuid(),
                [new ActivityGroupDto("movie:dune", "Dune", "Movies", 1, $"/engine-image/stream/entity/work/{Work:D}/cover?size=s", $"/details/work/{Work:D}?context=watch")],
                false)));
        });
        Services.AddSingleton(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(_state);
        Services.AddSingleton(provider =>
        {
            var orchestrator = new UIOrchestratorService(
                api,
                _state,
                new ActiveProfileSessionService(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
                provider.GetRequiredService<IConfiguration>(),
                NullLogger<UIOrchestratorService>.Instance);
            // A normal circuit: the live connection to the Engine is up.
            orchestrator.SetEngineConnectionState(EngineConnectionState.Online);
            return orchestrator;
        });
        Services.AddSingleton(provider => new IngestionLiveDashboardState(api, provider.GetRequiredService<UIOrchestratorService>(), _state));
        Services.AddSingleton(new PlaybackSessionController(null!, api));
        Services.AddSingleton<ActivityNotificationQueue>();
        Services.AddSingleton<ShellActivityState>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
    }

    [Fact]
    public void AnUpdateThatEndsWithFailuresWhileActivityIsOpen_DoesNotTurnTheIndicatorAmber()
    {
        var queue = Services.GetRequiredService<ActivityNotificationQueue>();
        Services.GetRequiredService<ShellActivityState>();
        var cut = Render<LibraryActivityPage>();

        _state.PushIngestionQueueProgress(new IngestionQueueProgressDto { State = "idle", UpdatedAt = DateTimeOffset.UtcNow });
        _state.PushIngestionQueueProgress(new IngestionQueueProgressDto { State = "working", FilesFound = 10, FilesDone = 2, Progress = 0.2, UpdatedAt = DateTimeOffset.UtcNow });
        _state.PushIngestionQueueProgress(new IngestionQueueProgressDto
        {
            State = "idle",
            UpdatedAt = DateTimeOffset.UtcNow,
            Outcomes = new IngestionQueueOutcomesDto { Failed = 1 },
        });

        cut.WaitForAssertion(() => Assert.False(queue.NeedsAttention));
    }

    [Fact]
    public void TheHeader_SaysLiveInGreenWhileConnected_AndNotLiveWhenTheEngineCannotBeReached()
    {
        var cut = Render<LibraryActivityPage>();

        var live = cut.Find("[data-testid='lact-live']");
        Assert.Equal("true", live.GetAttribute("data-live"));
        Assert.Contains("is-live", live.ClassName, StringComparison.Ordinal);
        Assert.Equal("Live", live.TextContent.Trim());

        Services.GetRequiredService<UIOrchestratorService>().SetEngineConnectionState(EngineConnectionState.Offline);
        cut.Render();

        live = cut.Find("[data-testid='lact-live']");
        Assert.Equal("false", live.GetAttribute("data-live"));
        Assert.Equal("Not live", live.TextContent.Trim());
    }

    [Fact]
    public void TheAreaChips_AreOutlinedInTheirAreaColour_AndAllLibrariesIsAPlainOnOffChipWithNoArrow()
    {
        var cut = Render<LibraryActivityPage>();

        var chips = cut.FindAll("[data-testid='lact-chips'] button");
        Assert.Equal(["All libraries", "Watch", "Listen", "Read"], chips.Select(chip => chip.TextContent.Trim()).ToArray());
        Assert.Contains("lact-chip--all", chips[0].ClassName, StringComparison.Ordinal);
        Assert.All(chips, chip => Assert.Contains("lact-chip", chip.ClassName, StringComparison.Ordinal));
        Assert.Contains("lib-area--watch", chips[1].ClassName, StringComparison.Ordinal);
        Assert.Contains("lib-area--listen", chips[2].ClassName, StringComparison.Ordinal);
        Assert.Contains("lib-area--read", chips[3].ClassName, StringComparison.Ordinal);
        Assert.Empty(chips[0].QuerySelectorAll("svg"));
        Assert.Empty(chips[1].QuerySelectorAll("svg"));
        Assert.Equal("true", chips[0].GetAttribute("aria-pressed"));

        chips[1].Click();

        chips = cut.FindAll("[data-testid='lact-chips'] button");
        Assert.Equal("true", chips[1].GetAttribute("aria-pressed"));
        Assert.Equal("false", chips[0].GetAttribute("aria-pressed"));
    }

    [Fact]
    public void ThePageDrawsItsOwnTitleOnlyWhenItsHostDoesNotNameIt_SoTheTitleAppearsOnceInEachPlace()
    {
        var standalone = Render<LibraryActivityPage>();
        Assert.Equal("Activity", Assert.Single(standalone.FindAll("h1")).TextContent.Trim());

        var hosted = Render<LibraryActivityPage>(parameters => parameters.Add(page => page.ShowTitle, false));
        Assert.Empty(hosted.FindAll("h1"));
        Assert.Single(hosted.FindAll("[data-testid='lact-live']"));
    }

    [Fact]
    public void BothHosts_TheSettingsFrameAndALibrarysActivityTab_NameThePageThemselves()
    {
        var root = FindRepoRoot();
        var settings = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Pages", "Settings.razor"));
        var shell = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Settings", "Libraries", "LibraryShell.razor"));

        Assert.Contains("<LibraryActivityPage ShowTitle=\"false\" />", settings, StringComparison.Ordinal);
        Assert.Contains("<LibraryActivityPage LibraryId=\"@Library.Id\" ShowTitle=\"false\" />", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoLibrariesAtAll_AnEmptyHistoryOffersAddLibrary()
    {
        _libraries = [];

        var cut = Render<LibraryActivityPage>();

        cut.WaitForAssertion(() => Assert.Contains("Nothing has been added yet", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Add library", cut.Find(".lact-history .app-page-state").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithLibraries_AnEmptyHistoryDoesNotOfferAddLibrary()
    {
        var cut = Render<LibraryActivityPage>();

        cut.WaitForAssertion(() => Assert.Contains("Nothing has been added in this time", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Add library", cut.Find(".lact-history").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHistoryControls_SitInOneRowBesideTheHeading_WithNoLabelsAboveAndLast30DaysChosen()
    {
        var cut = Render<LibraryActivityPage>();

        var head = cut.Find(".lact-history__head");
        Assert.Equal("History", cut.Find("#lact-history-title").TextContent.Trim());
        var row = head.QuerySelector(".app-filter-row.lact-controls");
        Assert.NotNull(row);
        Assert.Single(row!.QuerySelectorAll(".lact-controls__search"));
        Assert.Equal(3, row.QuerySelectorAll(".lact-controls__select").Length);
        Assert.Empty(cut.FindAll(".lact-history .app-field__label"));
        Assert.Contains("Search history", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Last 30 days", row.QuerySelector(".lact-controls__select--range")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Newest first", row.QuerySelector(".lact-controls__select--sort")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AMonthHeading_ReadsTheMonthAndYearThenAMutedBatchCount_AndCollapsesOnClick()
    {
        var now = DateTimeOffset.Now;
        _runs = [Run(now.AddMinutes(-30)), Run(now.AddMinutes(-90))];

        var cut = Render<LibraryActivityPage>();

        var toggle = cut.WaitForElement("[data-testid='lact-month-toggle']");
        var expected = $"{now:MMMM yyyy} · 2 batches";
        Assert.Equal(expected, string.Join(' ', toggle.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        Assert.DoesNotContain("batches" + "batches", toggle.TextContent, StringComparison.Ordinal);
        Assert.Contains("lact-group__count", toggle.QuerySelector("span")!.ClassName, StringComparison.Ordinal);
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        Assert.Equal(2, cut.FindAll("[data-testid='lact-entry']").Count);

        toggle.Click();

        Assert.Empty(cut.FindAll("[data-testid='lact-entry']"));
        Assert.Equal("false", cut.Find("[data-testid='lact-month-toggle']").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void AnOlderYear_StartsCollapsedWithItsBatchCount()
    {
        _runs = [Run(new DateTimeOffset(DateTimeOffset.Now.Year - 1, 6, 5, 12, 0, 0, DateTimeOffset.Now.Offset))];

        var cut = Render<LibraryActivityPage>();

        var toggle = cut.WaitForElement("[data-testid='lact-year-toggle']");
        Assert.Equal($"{DateTimeOffset.Now.Year - 1} · 1 batch", string.Join(' ', toggle.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("[data-testid='lact-entry']"));
    }

    [Fact]
    public void AnEntry_IsOneLeftAlignedRow_WithASubtitleCoversCountsAndCompleted()
    {
        _runs =
        [
            Run(DateTimeOffset.Now.AddMinutes(-30), [new ActivityRunCountDto("Movies", 12), new ActivityRunCountDto("TV", 85), new ActivityRunCountDto("Music", 51)],
                [new ActivityCoverDto($"/engine-image/stream/entity/work/{Work:D}/cover?size=t", "Movies", "Dune"), new ActivityCoverDto(null, "Movies", "No art")]),
        ];

        var cut = Render<LibraryActivityPage>();

        var entry = cut.WaitForElement("[data-testid='lact-entry']");
        Assert.Contains("lact-box__toggle", entry.QuerySelector("[data-testid='lact-entry-toggle']")!.ClassName, StringComparison.Ordinal);
        Assert.Equal("Added 85 episodes · 51 tracks · 12 movies", entry.QuerySelector(".lact-box__what strong")!.TextContent.Trim());
        Assert.Equal("Movies, TV and Music", entry.QuerySelector("[data-testid='lact-entry-subtitle']")!.TextContent.Trim());
        Assert.Equal("85 episodes · 51 tracks · 12 movies", entry.QuerySelector(".lact-box__count")!.TextContent.Trim());
        Assert.Equal("Completed", entry.QuerySelector("[data-testid='lact-entry-status']")!.TextContent.Trim());
        // The row order is what, covers, counts, status, chevron: left to right, never one centred stack.
        var order = entry.QuerySelectorAll(".lact-box__toggle > span").Select(span => (span.ClassName ?? string.Empty).Split(' ')[0]).ToArray();
        Assert.Equal(["lact-box__what", "lact-box__covers", "lact-box__count", "lact-box__status", "lact-box__chevron"], order);
    }

    [Fact]
    public void ACoverOnTheDashboardsOwnOrigin_IsDrawnAsAnImage_AndANoCoverOrBrokenOneAsANeutralStandIn()
    {
        _runs =
        [
            Run(DateTimeOffset.Now.AddMinutes(-30), [new ActivityRunCountDto("Movies", 2)],
                [new ActivityCoverDto($"/engine-image/stream/entity/work/{Work:D}/cover?size=t", "Movies", "Dune"), new ActivityCoverDto(null, "Movies", "No art")]),
        ];

        var cut = Render<LibraryActivityPage>();

        var covers = cut.WaitForElements(".lact-box__covers .lact-cover");
        var image = Assert.Single(covers[0].QuerySelectorAll("img"));
        Assert.StartsWith("/engine-image/stream/entity/work/", image.GetAttribute("src"), StringComparison.Ordinal);
        Assert.Single(covers[1].QuerySelectorAll("[data-testid='lact-picture-empty']"));
        Assert.Empty(covers[1].QuerySelectorAll("img"));

        // A picture that will not load is replaced by the same stand-in, never the browser's broken-image mark.
        cut.Find(".lact-box__covers img").TriggerEvent("onerror", new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());

        Assert.Equal(2, cut.FindAll(".lact-box__covers [data-testid='lact-picture-empty']").Count);
        Assert.Empty(cut.FindAll(".lact-box__covers img"));
    }

    [Fact]
    public void AnOpenedBatch_IsItsOwnPanelUnderTheLine_AndATileWithAPageHasATitleMenuAndNoCaption()
    {
        _runs = [Run(DateTimeOffset.Now.AddMinutes(-30), [new ActivityRunCountDto("Movies", 1)], [])];

        var cut = Render<LibraryActivityPage>();
        cut.WaitForElement("[data-testid='lact-entry-toggle']").Click();

        var open = cut.WaitForElement("[data-testid='lact-open']");
        Assert.Empty(cut.FindAll(".lact-box [data-testid='lact-open']"));
        var tile = open.QuerySelector("[data-testid='activity-tile']")!;
        Assert.Equal($"/details/work/{Work:D}?context=watch", tile.QuerySelector("a")!.GetAttribute("href"));
        Assert.Single(tile.QuerySelectorAll("img"));
        // The art carries the title; nothing is drawn as a caption over a picture.
        Assert.Empty(tile.QuerySelectorAll(".lact-tile__title"));
        Assert.DoesNotContain("Dune", tile.QuerySelector(".lact-tile__face")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(tile.QuerySelector("[data-media-tile-actions]"));
    }

    [Fact]
    public void WhileSomethingRuns_TheProgressCardIsOneRingWithItsPercent_AndReviewIsALinkInTheWarningColour()
    {
        var cut = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, new IngestionQueueProgressDto { State = "working", FilesFound = 10, FilesDone = 4, Progress = 0.4, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-8) })
            .Add(card => card.Presentation, new IngestionPresentationSnapshotDto { IsRunning = true, NeedReviewCount = 20, ReadyGroups = 920 }));

        Assert.Empty(cut.FindAll("[data-testid='activity-progress-started']"));
        Assert.NotNull(cut.Find(".lact-summary__ring"));
        Assert.Contains("40% processed", cut.Find(".lact-summary__head").TextContent, StringComparison.Ordinal);
        var review = cut.Find("a.lact-summary__review");
        Assert.Equal("/settings/review", review.GetAttribute("href"));
        Assert.Contains("Review 20 items", review.TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.996, "working", 99)]
    [InlineData(0.4, "working", 40)]
    [InlineData(1.0, "idle", 100)]
    public void ThePercent_IsRoundedDown_AndNeverHundredWhileStillWorking(double progress, string state, int expected)
    {
        var snapshot = new IngestionQueueProgressDto { State = state, Progress = progress, FilesFound = 10 };

        Assert.Equal(expected, MediaEngine.Web.Components.Settings.IngestionQueueProgressText.Percent(snapshot));
    }

    [Fact]
    public void WhenNothingRuns_TheUpToDateCardIsOnePlainLine_WithNoIconAndNoBar()
    {
        var cut = Render<LibraryActivityProgressCard>(parameters => parameters.Add(card => card.LastCompleted, DateTimeOffset.UtcNow.AddMinutes(-12)));

        var line = cut.Find("[data-testid='activity-up-to-date']");
        Assert.Equal("Everything is up to date · Last completed scan 12 minutes ago", string.Join(' ', line.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        Assert.Empty(line.QuerySelectorAll("svg"));
        Assert.Empty(cut.FindAll(".app-progress-bar"));
    }

    [Fact]
    public void TheWords_ForTheSubtitleAndTheTitleCountFollowTheMockup()
    {
        Assert.Equal("Movies, TV and Music", LibraryActivityText.Kinds(["Music", "Movies", "TV"]));
        Assert.Equal("Music", LibraryActivityText.Kinds(["Music"]));
        Assert.Equal(string.Empty, LibraryActivityText.Kinds([]));
        Assert.Equal("Metadata refresh · Music", LibraryActivityText.Subtitle(Run(DateTimeOffset.Now, [new ActivityRunCountDto("Music", 3)], kind: ActivityRunKindNames.Updated)));
        Assert.Equal("Resolved from Needs Review", LibraryActivityText.Subtitle(Run(DateTimeOffset.Now, kind: ActivityRunKindNames.Fixed)));
        Assert.Equal("Manually removed from library", LibraryActivityText.Subtitle(Run(DateTimeOffset.Now, kind: ActivityRunKindNames.Removed)));
        Assert.Equal("2 titles", LibraryActivityText.TitlesCount(2));
        Assert.Equal("1 title", LibraryActivityText.TitlesCount(1));
        Assert.Equal("4 new episodes", LibraryActivityText.MarkerLine(new ActivityTitleMarkerDto(Guid.NewGuid(), ActivityRunKindNames.Added, DateTimeOffset.Now, 4), "TV"));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static ActivityRunDto Run(
        DateTimeOffset started,
        IReadOnlyList<ActivityRunCountDto>? counts = null,
        IReadOnlyList<ActivityCoverDto>? covers = null,
        string kind = ActivityRunKindNames.Added)
        => new(
            Guid.NewGuid(), "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "Library", kind, ActivityRunStatusNames.Completed,
            started, started.AddMinutes(5), counts?.Sum(count => count.Count) ?? 1,
            counts ?? [new ActivityRunCountDto("Movies", 1)], covers ?? []);
}

