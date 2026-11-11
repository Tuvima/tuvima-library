using Bunit;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Taste;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Profile &gt; Overview (Profile Overview, Taste and Activity spec): the banner, stat strip, Continue enjoying, Recent activity timeline, taste and the last 30 days.</summary>
public sealed class OverviewPageTests : AsyncBunitContext
{
    private static readonly Guid OwnId = Guid.NewGuid();
    private static readonly string WorkA = Guid.NewGuid().ToString("D");
    private static readonly string Key = new('b', 64);

    private ProfileViewModel _profile = new(OwnId, "Test User", "#C9922E", "Profile",
        new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero), NavigationConfig: "{\"playlist_order\":[\"a\"]}");

    private ProfileOverviewViewModel? _overview = new();
    private PersonalActivitySummaryDto? _summary = new(0, 0, 0, 0, 0, 0, 0, null);
    private TasteViewDto? _tasteView;
    private PersonalActivityRecentDto? _recent = new([]);
    private readonly List<(Guid Id, string Name, string Color, string? NavigationConfig)> _updates = [];

    public OverviewPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        UseDevice("desktop", hover: true);
        Services.AddSingleton(CreateApi());

        var grant = new AccountProfileGrantDto(OwnId, OwnId, "Test User", null, true, true, true,
            new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow);
        var authority = new DashboardAuthorityResponse(OwnId, OwnId, true, true, 1, 1, false, false, null, 1, [grant], [], []);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", OwnId, OwnId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
    }

    private IEngineApiClient CreateApi() => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel> { _profile }));
        stub.SetHandler(nameof(IEngineApiClient.GetProfileOverviewAsync), _ => Task.FromResult(_overview));
        stub.SetHandler(nameof(IEngineApiClient.GetPersonalActivityRecentAsync), _ => Task.FromResult(_recent));
        stub.SetHandler(nameof(IEngineApiClient.GetPersonalActivitySummaryAsync), _ => Task.FromResult(_summary));
        stub.SetHandler(nameof(IEngineApiClient.GetPersonalTasteAsync), _ => Task.FromResult(_tasteView));
        stub.SetHandler(nameof(IEngineApiClient.UpdateProfileAsync), args =>
        {
            var id = (Guid)args![0]!;
            var name = (string)args[1]!;
            var color = (string)args[2]!;
            var navigation = (string?)args[3];
            _updates.Add((id, name, color, navigation));
            _profile = _profile with { DisplayName = name, AvatarColor = color };
            return Task.FromResult(true);
        });
    });

    private void UseDevice(string formFactor, bool hover)
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, !hover, hover, hover)).GetAwaiter().GetResult();
        Services.AddScoped(_ => device);
    }

    private static PersonalActivityRecentTileDto Tile(
        string kind,
        long seconds = 0,
        double? progress = null,
        bool finished = false,
        string shape = PersonalActivityShapeValues.Landscape,
        string title = "A Title",
        string? subtitle = null,
        string? parent = null,
        string? artwork = null,
        IReadOnlyList<string>? covers = null,
        int? songs = null,
        int? albums = null,
        string? workId = null,
        int daysAgo = 0) =>
        PersonalActivityPresentationTests.Tile(kind, seconds, progress, finished, shape, title, subtitle, parent, artwork, covers, songs, albums, workId,
            startedAt: DateTimeOffset.Now.AddDays(-daysAgo).ToString("O"));

    private IRenderedComponent<OverviewPage> RenderPage()
    {
        var cut = Render<OverviewPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-settings-overview]")));
        return cut;
    }

    [Fact]
    public void Header_ShowsTheAvatarNameAndWhenTheyJoined_WithEditProfileAndNoAppsLink()
    {
        var cut = RenderPage();

        Assert.Equal("Test User", cut.Find(".overview-header__name").TextContent);
        Assert.Equal("Joined March 2026", cut.Find(".overview-header__joined").TextContent);
        Assert.NotEmpty(cut.FindAll(".overview-header__avatar svg, .overview-header__avatar img"));
        Assert.Contains(cut.FindAll("a, button"), link => link.TextContent.Contains("Edit profile", StringComparison.Ordinal));

        // The app links and QR code live on General now; Profile points nowhere near them.
        Assert.Empty(cut.FindAll("[data-overview-apps-link]"));
        Assert.Empty(cut.FindAll("a[href*='/apps']"));
        Assert.DoesNotContain("Get the Library app", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[aria-label='Apps']"));
    }

    [Fact]
    public void ThereIsNoManageProfilesButton_NoStarRatings_AndNoSeparateMyListRow()
    {
        _recent = new([Tile(PersonalActivityKindValues.MyList, workId: WorkA)]);

        var cut = RenderPage();

        Assert.DoesNotContain("Manage profiles", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Rating", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Like", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Recent activity", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Your taste", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatStrip_ShowsInProgress_FinishedInTheLast30Days_HoursEnjoyed_AndDaysActive()
    {
        // 3 h watched + 30 m read + 2.5 h listened = 6 hours enjoyed over a lifetime; the other two are the rolling window.
        _summary = new PersonalActivitySummaryDto(
            WatchSeconds: 3 * 3600, ReadSeconds: 1800, ListenSeconds: 9000, MusicSeconds: 3600, AudiobookSeconds: 5400,
            FinishedCount: 412, InProgressCount: 3, FirstActivityAt: null,
            FinishedLast30Days: 5, DaysActiveLast30: 12);

        var cut = RenderPage();

        Assert.Equal(["in-progress", "finished", "hours", "days-active"], cut.FindAll("[data-overview-count]").Select(item => item.GetAttribute("data-overview-count")!).ToArray());
        Assert.Equal("3", CountValue(cut, "in-progress"));
        Assert.Equal("5", CountValue(cut, "finished"));
        Assert.Equal("6", CountValue(cut, "hours"));
        Assert.Equal("12", CountValue(cut, "days-active"));
        Assert.Equal("Last 30 days", cut.Find("[data-overview-count='finished'] .overview-stats__note").TextContent);
        Assert.Equal("Hours enjoyed", cut.Find("[data-overview-count='hours'] .overview-stats__label").TextContent);
    }

    [Fact]
    public void TheFinishedFigure_IsTheRollingWindow_NotTheLifetimeTotal()
    {
        _overview = Overview(inProgress: 80, completed: 80);
        _summary = new PersonalActivitySummaryDto(7200, 0, 0, 0, 0, FinishedCount: 412, InProgressCount: 9, FirstActivityAt: null, FinishedLast30Days: 2, DaysActiveLast30: 4);

        var cut = RenderPage();

        Assert.Equal("9", CountValue(cut, "in-progress"));
        Assert.Equal("2", CountValue(cut, "finished"));
        Assert.Equal("2", CountValue(cut, "hours"));
    }

    [Fact]
    public void HoursEnjoyed_ShowsOneDecimalBelowTenHours()
    {
        _summary = new PersonalActivitySummaryDto(12600, 0, 0, 0, 0, 0, 0, null);

        var cut = RenderPage();

        Assert.Equal("3.5", CountValue(cut, "hours"));
    }

    [Fact]
    public void ANewProfile_ShowsZeros_AndAnInvitation_AndNoCalendar()
    {
        var cut = RenderPage();

        Assert.Equal("0", CountValue(cut, "in-progress"));
        Assert.Equal("0", CountValue(cut, "finished"));
        Assert.Equal("0", CountValue(cut, "hours"));
        Assert.Equal("0", CountValue(cut, "days-active"));
        Assert.Contains("fills in", cut.Find("[data-overview-invite]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-overview-heatmap]"));
    }

    [Fact]
    public void TheStatStrip_IsAbsentWhenTheSummaryCannotBeRead_ButThePageStillOpens()
    {
        _summary = null;

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-overview-count]"));
        Assert.Empty(cut.FindAll("[data-overview-invite]"));
        Assert.NotEmpty(cut.FindAll(".overview-header__name"));
    }

    [Fact]
    public void ContinueEnjoying_ShowsTheNewestTitlePartWayThroughForEachOfReadingWatchingAndListening()
    {
        _overview = new ProfileOverviewViewModel
        {
            ContinueItems =
            [
                Item("Old Show", "TV", 20, daysAgo: 5),
                Item("Dune", "Book", 55, daysAgo: 1),
                Item("New Show", "Movie", 30, daysAgo: 0),
                Item("Dune (audio)", "Audiobook", 80, daysAgo: 2),
            ],
        };

        var cut = RenderPage();

        Assert.Equal(["Reading", "Watching", "Listening"], cut.FindAll("[data-overview-continue-kind]").Select(card => card.GetAttribute("data-overview-continue-kind")!).ToArray());
        Assert.Equal("Dune", cut.Find("[data-overview-continue-kind='Reading'] .overview-continue__title").TextContent);
        Assert.Equal("New Show", cut.Find("[data-overview-continue-kind='Watching'] .overview-continue__title").TextContent);
        Assert.Equal("80%", cut.Find("[data-overview-continue-kind='Listening'] .overview-continue__percent").TextContent);
        Assert.StartsWith("/watch/player/", cut.Find("[data-overview-continue-kind='Watching'] a").GetAttribute("href"), StringComparison.Ordinal);
    }

    [Fact]
    public void ContinueEnjoying_LeavesOutAKindWithNothingInProgress_AndSaysSoWhenThereIsNothing()
    {
        _overview = new ProfileOverviewViewModel { ContinueItems = [Item("Dune", "Book", 55, daysAgo: 1)] };
        var cut = RenderPage();
        Assert.Equal(["Reading"], cut.FindAll("[data-overview-continue-kind]").Select(card => card.GetAttribute("data-overview-continue-kind")!).ToArray());

        _overview = new ProfileOverviewViewModel();
        cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-overview-continue]"));
        Assert.NotEmpty(cut.FindAll("[data-overview-continue-empty]"));
    }

    [Fact]
    public void RecentActivity_GroupsRowsUnderTodayAndYesterday_WithTheTitleCaptionAndAMenu()
    {
        _recent = new(
        [
            Tile(PersonalActivityKindValues.Watch, 52 * 60, progress: 40, subtitle: "S2 E4 · Fear", title: "Tremors Show", workId: WorkA),
            Tile(PersonalActivityKindValues.Read, 80 * 60, progress: 100, finished: true, shape: PersonalActivityShapeValues.Portrait, title: "Dune Book", workId: WorkA, daysAgo: 1),
        ]);

        var cut = RenderPage();

        Assert.Equal(["Today", "Yesterday"], cut.FindAll("[data-timeline-day]").Select(day => day.GetAttribute("data-timeline-day")!).ToArray());
        var rows = cut.FindAll(".overview-timeline__row");
        Assert.Equal("Tremors Show", rows[0].QuerySelector(".overview-timeline__title")!.TextContent);
        Assert.Equal("Watched 52 m · S2 E4 · Fear", rows[0].QuerySelector(".overview-timeline__detail")!.TextContent);
        Assert.Equal("40%", rows[0].QuerySelector("[data-activity-progress] span:last-child")!.TextContent);
        Assert.NotNull(rows[1].QuerySelector("[data-activity-finished]"));
        Assert.All(rows, row => Assert.NotNull(row.QuerySelector("button[aria-haspopup='menu']")));
        Assert.Equal($"/details/work/{WorkA}?context=watch", rows[0].QuerySelector(".overview-timeline__title")!.GetAttribute("href"));
    }

    [Fact]
    public void RecentActivity_ShowsNoBarOrCheckForMusicOrMyList_AndAtMostEightRows()
    {
        _recent = new(
        [
            Tile(PersonalActivityKindValues.MusicSession, 600, shape: PersonalActivityShapeValues.Square, songs: 3, albums: 1),
            Tile(PersonalActivityKindValues.MyList, workId: WorkA),
            .. Enumerable.Range(0, 10).Select(i => Tile(PersonalActivityKindValues.Watch, 600, progress: 10, title: $"Show {i}", workId: WorkA)),
        ]);

        var cut = RenderPage();

        var rows = cut.FindAll(".overview-timeline__row");
        Assert.Equal(8, rows.Count);
        Assert.Null(rows[0].QuerySelector("[data-activity-progress], [data-activity-finished]"));
        Assert.Null(rows[1].QuerySelector("[data-activity-progress], [data-activity-finished]"));
        Assert.Equal("Added to My list", rows[1].QuerySelector(".overview-timeline__detail")!.TextContent);
    }

    [Fact]
    public void MusicSession_IsASquareCollageOfAtMostNineCovers_AndSaysHowManySongs()
    {
        var covers = Enumerable.Range(0, 12).Select(i => $"/engine-activity-artwork/{new string((char)('a' + i % 6), 64)}").ToList();
        _recent = new([Tile(PersonalActivityKindValues.MusicSession, 3600, shape: PersonalActivityShapeValues.Square, title: string.Empty, covers: covers, songs: 50, albums: 12)]);

        var cut = RenderPage();

        var collage = cut.Find("[data-activity-collage]");
        Assert.Equal(9, collage.QuerySelectorAll("img").Length);
        Assert.Contains("overview-timeline__collage--3", collage.ClassName);
        Assert.Equal("Music", cut.Find(".overview-timeline__title").TextContent);
        Assert.Contains("50 songs from 12 albums", cut.Find(".overview-timeline__detail").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ATitleOpensTheItem_AndAMusicSessionOrAnItemThatIsGoneOpensTheActivityPage()
    {
        _recent = new(
        [
            Tile(PersonalActivityKindValues.Watch, 600, workId: WorkA),
            Tile(PersonalActivityKindValues.MusicSession, 600, shape: PersonalActivityShapeValues.Square, songs: 5, albums: 2),
            Tile(PersonalActivityKindValues.Read, 600, workId: null),
        ]);

        var cut = RenderPage();

        Assert.Equal(
            [$"/details/work/{WorkA}?context=watch", "/settings/overview/activity", "/settings/overview/activity"],
            cut.FindAll(".overview-timeline__title").Select(link => link.GetAttribute("href")!).ToArray());
    }

    [Fact]
    public void SeeAll_OpensTheActivityPage_AndSeeYourTasteOpensTheTastePage()
    {
        var cut = RenderPage();

        Assert.Equal("/settings/overview/activity", cut.Find("[data-overview-see-all]").GetAttribute("href"));
        Assert.Equal("/settings/overview/taste", cut.Find("[data-overview-taste-link]").GetAttribute("href"));
    }

    [Fact]
    public void ForAKidsProfile_SeeAllOpensMyActivity()
    {
        _profile = _profile with { Role = "RestrictedProfile" };

        var cut = RenderPage();

        Assert.Equal("/my-activity", cut.Find("[data-overview-see-all]").GetAttribute("href"));
    }

    [Fact]
    public void ForAKidsProfile_TasteOpensTheirOwnPage_AndThereIsNoEditing()
    {
        _profile = _profile with { Role = "RestrictedProfile" };

        var cut = RenderPage();

        Assert.Equal("/my-profile/taste", cut.Find("[data-overview-taste-link]").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".overview-header__edit"));
        Assert.Empty(cut.FindAll(".overview-header__apps"));
    }

    [Fact]
    public void EmptyTimeline_SaysWhatShowsUpThere()
    {
        _recent = new([]);

        var cut = RenderPage();

        Assert.Equal("Nothing yet — what you watch, read and listen to shows up here.", cut.Find("[data-activity-empty]").TextContent);
        Assert.Empty(cut.FindAll("[data-activity-timeline]"));
    }

    [Fact]
    public void TimelineThatCannotBeLoaded_SaysSoInsteadOfPretendingNothingHappened()
    {
        _recent = null;

        var cut = RenderPage();

        Assert.NotEmpty(cut.FindAll("[data-activity-unavailable]"));
        Assert.Empty(cut.FindAll("[data-activity-empty]"));
    }

    [Fact]
    public void YourTaste_SaysYouLoveTheTopThreeGenres_AndShowsTheTopGenresAsPills()
    {
        _tasteView = new TasteViewDto
        {
            People =
            [
                new TastePersonDto
                {
                    IsYou = true,
                    LastUpdatedAt = "2026-10-01T00:00:00.0000000Z",
                    Genres = [new("sci-fi", 0.4, TasteValues.SourceLearned), new("thriller", 0.3, TasteValues.SourceLearned), new("drama", 0.2, TasteValues.SourceLearned), new("comedy", 0.1, TasteValues.SourceLearned)],
                },
            ],
        };

        var cut = RenderPage();

        Assert.Equal("You love sci-fi, thriller and drama.", cut.Find("[data-overview-taste-summary]").TextContent);
        Assert.Equal(4, cut.FindAll("[data-overview-taste-genres] li").Count);
    }

    [Fact]
    public void YourTaste_WithNothingYet_SaysSoAndStillLinksToTheTastePage()
    {
        var cut = RenderPage();

        Assert.StartsWith("Your taste appears here", cut.Find("[data-overview-taste-summary]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-overview-taste-link]"));
        Assert.Empty(cut.FindAll("[data-overview-taste-genres]"));
    }

    [Fact]
    public void TheLast30Days_DrawsACalendarOfWholeWeeks_DarkerForBusierDays_AndSaysHowManyDaysWereActive()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _summary = new PersonalActivitySummaryDto(7200, 0, 0, 0, 0, 5, 1, null, FinishedLast30Days: 3, DaysActiveLast30: 2,
            Daily:
            [
                new PersonalActivityDayDto(today.ToString("yyyy-MM-dd"), 7200, 2),
                new PersonalActivityDayDto(today.AddDays(-3).ToString("yyyy-MM-dd"), 600, 1),
            ]);

        var cut = RenderPage();

        var cells = cut.FindAll("[data-heatmap-date]");
        Assert.Equal(ProfileOverviewPresentationDays, cells.Count);
        Assert.Equal("4", cut.Find($"[data-heatmap-date='{today:yyyy-MM-dd}']").GetAttribute("data-heatmap-level"));
        Assert.Equal("1", cut.Find($"[data-heatmap-date='{today.AddDays(-3):yyyy-MM-dd}']").GetAttribute("data-heatmap-level"));
        Assert.Equal("0", cut.Find($"[data-heatmap-date='{today.AddDays(-1):yyyy-MM-dd}']").GetAttribute("data-heatmap-level"));
        Assert.Equal(0, cut.FindAll("[data-heatmap-date]").Count(cell => cell.ClassName!.Contains("--outside", StringComparison.Ordinal)));
        Assert.Contains("Active on 2 of the last 30 days", cut.Find("[data-overview-month-line]").TextContent, StringComparison.Ordinal);
        Assert.Contains("active on 2 days", cut.Find("[data-overview-heatmap-grid]").GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    private const int ProfileOverviewPresentationDays = 30;

    [Fact]
    public async Task OnAPhone_ThePageIsASingleColumn_AndTheTimelineStillOpensItems()
    {
        // Each test context holds one device, so the phone builds its own.
        await using var ctx = new PhoneContext();
        var cut = ctx.RenderWithTiles([Tile(PersonalActivityKindValues.Watch, 600, workId: WorkA, title: "Tremors Show")]);

        Assert.Contains("overview-page--phone", cut.Find("[data-settings-overview]").ClassName);
        Assert.Equal($"/details/work/{WorkA}?context=watch", cut.Find(".overview-timeline__title").GetAttribute("href"));
    }

    [Fact]
    public void OnADesktop_ThePageHasNoPhoneClass()
    {
        var cut = RenderPage();

        Assert.DoesNotContain("overview-page--phone", cut.Find("[data-settings-overview]").ClassName);
    }

    [Fact]
    public void EditProfile_OpensTheSelfEditor_WithOnlyTheNameAndPicture()
    {
        var cut = RenderPage();

        EditProfileButton(cut).Click();

        Assert.Equal("Edit profile", cut.Find("#profile-editor-title").TextContent);
        Assert.Equal("Test User", cut.Find(".profile-editor__name").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".profile-editor__settings"));
        Assert.Empty(cut.FindAll(".profile-editor__delete"));
    }

    [Fact]
    public void SavingEditProfile_SendsTheNewNameAndColour_KeepingTheNavigationPreferences_AndShowsTheNewName()
    {
        var cut = RenderPage();
        EditProfileButton(cut).Click();

        cut.Find(".profile-editor__name").Input("Shy");
        cut.FindAll(".profile-editor__actions button").First(button => button.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() =>
        {
            var update = Assert.Single(_updates);
            Assert.Equal(OwnId, update.Id);
            Assert.Equal("Shy", update.Name);
            Assert.Equal("{\"playlist_order\":[\"a\"]}", update.NavigationConfig);
            Assert.Equal("Shy", cut.Find(".overview-header__name").TextContent);
            Assert.Empty(cut.FindAll("#profile-editor-title"));
        });
    }

    [Fact]
    public void CancellingEditProfile_ChangesNothing()
    {
        var cut = RenderPage();
        EditProfileButton(cut).Click();

        cut.FindAll(".profile-editor__actions button").First(button => button.TextContent.Trim() == "Cancel").Click();

        Assert.Empty(_updates);
        Assert.Empty(cut.FindAll("#profile-editor-title"));
    }

    [Fact]
    public void SaveThatTheEngineRefuses_KeepsTheEditorOpenWithAMessage()
    {
        var failing = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel> { _profile }));
            stub.SetHandler(nameof(IEngineApiClient.GetProfileOverviewAsync), _ => Task.FromResult(_overview));
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalActivityRecentAsync), _ => Task.FromResult(_recent));
            stub.SetHandler(nameof(IEngineApiClient.UpdateProfileAsync), _ => Task.FromResult(false));
        });
        Services.AddSingleton(failing);
        var cut = RenderPage();
        EditProfileButton(cut).Click();

        cut.FindAll(".profile-editor__actions button").First(button => button.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Contains("couldn't be saved", cut.Find(".who-page__error").TextContent, StringComparison.Ordinal));
        Assert.NotEmpty(cut.FindAll("#profile-editor-title"));
    }

    [Fact]
    public void EditProfile_IsNotOfferedOnAKidsProfile()
    {
        _profile = _profile with { Role = "RestrictedProfile" };

        var cut = RenderPage();

        Assert.DoesNotContain(cut.FindAll("a, button"), element => element.TextContent.Contains("Edit profile", StringComparison.Ordinal));
    }

    private static AngleSharp.Dom.IElement EditProfileButton(IRenderedComponent<OverviewPage> cut) =>
        cut.FindAll("button").First(button => button.TextContent.Contains("Edit profile", StringComparison.Ordinal));

    private static string CountValue(IRenderedComponent<OverviewPage> cut, string key) =>
        cut.Find($"[data-overview-count='{key}'] .overview-stats__value").TextContent;

    private static ProfileOverviewItemViewModel Item(string title, string mediaType, double percent, int daysAgo) => new()
    {
        AssetId = Guid.NewGuid(),
        Title = title,
        MediaType = mediaType,
        ProgressPct = percent,
        LastAccessed = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        Route = mediaType is "Book" ? $"/read/{Guid.NewGuid()}" : $"/watch/player/{Guid.NewGuid()}",
    };

    private static ProfileOverviewViewModel Overview(
        int inProgress = 0,
        int completed = 0,
        Dictionary<string, double>? consumed = null,
        TasteProfileDto? taste = null) => new()
        {
            Stats = new ProfileOverviewStatsViewModel
            {
                InProgress = inProgress,
                Completed = completed,
                ConsumedSecondsByMediaType = consumed ?? [],
            },
            Taste = taste,
        };

    /// <summary>A separate context for the phone, because each test context holds one device.</summary>
    private sealed class PhoneContext : AsyncBunitContext
    {
        public IRenderedComponent<OverviewPage> RenderWithTiles(IReadOnlyList<PersonalActivityRecentTileDto> tiles)
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddLogging();
            Services.AddLocalization();
            Services.AddNativeUiServices();
            Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            Services.AddScoped<UniverseStateContainer>();
            Services.AddScoped<ActiveProfileSessionService>();
            Services.AddScoped<UIOrchestratorService>();
            var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
            device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true)).GetAwaiter().GetResult();
            Services.AddScoped(_ => device);
            var profile = new ProfileViewModel(OwnId, "Test User", "#C9922E", "Profile", DateTimeOffset.UtcNow);
            Services.AddSingleton(EngineApiClientStub.Create(stub =>
            {
                stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel> { profile }));
                stub.SetHandler(nameof(IEngineApiClient.GetProfileOverviewAsync), _ => Task.FromResult<ProfileOverviewViewModel?>(new ProfileOverviewViewModel()));
                stub.SetHandler(nameof(IEngineApiClient.GetPersonalActivityRecentAsync), _ => Task.FromResult<PersonalActivityRecentDto?>(new PersonalActivityRecentDto(tiles)));
            }));
            var grant = new AccountProfileGrantDto(OwnId, OwnId, "Test User", null, true, true, true,
                new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow);
            var session = new DashboardSessionAccessor();
            session.Set("test-session-token", OwnId, OwnId, Guid.NewGuid(),
                new DashboardAuthorityResponse(OwnId, OwnId, true, true, 1, 1, false, false, null, 1, [grant], [], []));
            Services.AddScoped(_ => session);

            var cut = Render<OverviewPage>();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-settings-overview]")));
            return cut;
        }
    }
}
