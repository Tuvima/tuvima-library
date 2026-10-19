using Bunit;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Personal &gt; Overview (D11, D13, D14, D17, D18, D19): the person's own header, counts, Recent activity strip and taste.</summary>
public sealed class OverviewPageTests : AsyncBunitContext
{
    private static readonly Guid OwnId = Guid.NewGuid();
    private static readonly string WorkA = Guid.NewGuid().ToString("D");
    private static readonly string Key = new('b', 64);

    private ProfileViewModel _profile = new(OwnId, "Test User", "#C9922E", "Profile",
        new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero), NavigationConfig: "{\"playlist_order\":[\"a\"]}");

    private ProfileOverviewViewModel? _overview = new();
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
            new GrantAdminProtectionDto(false, "None", null, 1, false, null), 1, DateTimeOffset.UtcNow);
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
        string? workId = null) =>
        PersonalActivityPresentationTests.Tile(kind, seconds, progress, finished, shape, title, subtitle, parent, artwork, covers, songs, albums, workId,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-20).ToString("O"));

    private IRenderedComponent<OverviewPage> RenderPage()
    {
        var cut = Render<OverviewPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-settings-overview]")));
        return cut;
    }

    [Fact]
    public void Header_ShowsTheAvatarNameAndWhenTheyJoined_WithEditProfileAndTheAppsIcon()
    {
        var cut = RenderPage();

        Assert.Equal("Test User", cut.Find(".overview-header__name").TextContent);
        Assert.Equal("Joined March 2026", cut.Find(".overview-header__joined").TextContent);
        Assert.NotEmpty(cut.FindAll(".overview-header__avatar svg, .overview-header__avatar img"));
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Edit profile", StringComparison.Ordinal));

        var apps = cut.Find("[aria-label='Apps']");
        Assert.Equal("a", apps.TagName.ToLowerInvariant());
        Assert.Equal("/settings/overview/apps", apps.GetAttribute("href"));
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
    public void Counts_ShowInProgressFinishedAndTheTimeWatchedReadAndListened()
    {
        _overview = Overview(inProgress: 3, completed: 12, consumed: new()
        {
            ["Movies"] = 7200,
            ["TV"] = 3600,
            ["Books"] = 1800,
            ["Audiobooks"] = 5400,
            ["Music"] = 3600,
        });

        var cut = RenderPage();

        Assert.Equal("3", CountValue(cut, "in-progress"));
        Assert.Equal("12", CountValue(cut, "finished"));
        Assert.Equal("3 h", CountValue(cut, "watched"));
        Assert.Equal("30 m", CountValue(cut, "read"));
        Assert.Equal("2.5 h", CountValue(cut, "listened"));
    }

    [Fact]
    public void Counts_LeaveOutAnyTimeTheEngineCannotSupply_NeverFakingIt()
    {
        _overview = Overview(inProgress: 0, completed: 0, consumed: new() { ["Movies"] = 3600 });

        var cut = RenderPage();

        Assert.Equal(["in-progress", "finished", "watched"], cut.FindAll("[data-overview-count]").Select(item => item.GetAttribute("data-overview-count")!).ToArray());
        Assert.Equal("0", CountValue(cut, "in-progress"));
    }

    [Fact]
    public void Counts_AreAbsentWhenTheOverviewCannotBeRead_ButThePageStillOpens()
    {
        _overview = null;

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-overview-count]"));
        Assert.NotEmpty(cut.FindAll(".overview-header__name"));
    }

    [Fact]
    public void Strip_ShowsTheCaptionOfEachKind_AndPrintsNoTitles()
    {
        _recent = new(
        [
            Tile(PersonalActivityKindValues.Watch, 52 * 60, progress: 40, subtitle: "S2 E4 · Fear", title: "Tremors Show", workId: WorkA),
            Tile(PersonalActivityKindValues.Read, 80 * 60, progress: 100, finished: true, shape: PersonalActivityShapeValues.Portrait, title: "Dune Book", workId: WorkA),
            Tile(PersonalActivityKindValues.Listen, 3 * 3600 + 12 * 60, progress: 20, shape: PersonalActivityShapeValues.Square, title: "Audiobook Title", workId: WorkA),
            Tile(PersonalActivityKindValues.MusicSession, 65 * 60, shape: PersonalActivityShapeValues.Square, title: "Music", songs: 50, albums: 12, covers: [$"/engine-activity-artwork/{Key}"]),
            Tile(PersonalActivityKindValues.MyList, shape: PersonalActivityShapeValues.Portrait, title: "Saved Film", workId: WorkA),
        ]);

        var cut = RenderPage();

        var captions = cut.FindAll(".activity-tile__caption").Select(caption => caption.TextContent.Trim()).ToArray();
        Assert.Equal(["Watched 52 m", "Read 1 h 20 m", "Listened 3 h 12 m", "Played 1 h 05 m", "Added to My list"], captions);

        var printed = cut.Find("[data-activity-strip]").TextContent;
        foreach (var title in new[] { "Tremors Show", "Dune Book", "Audiobook Title", "Saved Film", "S2 E4" })
        {
            Assert.DoesNotContain(title, printed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Strip_ShowsABarWhilePartWayThrough_ACheckWhenFinished_AndNothingForMusicOrMyList()
    {
        _recent = new(
        [
            Tile(PersonalActivityKindValues.Watch, 600, progress: 40, workId: WorkA),
            Tile(PersonalActivityKindValues.Read, 600, progress: 100, finished: true, workId: WorkA),
            Tile(PersonalActivityKindValues.MusicSession, 600, shape: PersonalActivityShapeValues.Square, songs: 3, albums: 1),
            Tile(PersonalActivityKindValues.MyList, workId: WorkA),
        ]);

        var cut = RenderPage();

        var tiles = cut.FindAll(".activity-tile");
        Assert.Equal(4, tiles.Count);
        Assert.Equal("40", tiles[0].QuerySelector(".app-artwork-progress")!.GetAttribute("aria-valuenow"));
        Assert.Null(tiles[0].QuerySelector(".app-artwork-completed"));
        Assert.NotNull(tiles[1].QuerySelector(".app-artwork-completed"));
        Assert.Null(tiles[1].QuerySelector(".app-artwork-progress"));
        Assert.Null(tiles[2].QuerySelector(".app-artwork-progress, .app-artwork-completed"));
        Assert.Null(tiles[3].QuerySelector(".app-artwork-progress, .app-artwork-completed"));
    }

    [Fact]
    public void Strip_GivesEveryTileTheClassOfItsShape()
    {
        _recent = new(
        [
            Tile(PersonalActivityKindValues.Watch, shape: PersonalActivityShapeValues.Landscape, workId: WorkA),
            Tile(PersonalActivityKindValues.Read, shape: PersonalActivityShapeValues.Portrait, workId: WorkA),
            Tile(PersonalActivityKindValues.Listen, shape: PersonalActivityShapeValues.Square, workId: WorkA),
        ]);

        var cut = RenderPage();

        Assert.Equal(["landscape", "portrait", "square"], cut.FindAll(".activity-tile").Select(tile => tile.GetAttribute("data-activity-shape")!).ToArray());
        Assert.All(cut.FindAll(".activity-tile"), tile => Assert.Contains("activity-tile--", tile.ClassName));
    }

    [Fact]
    public void MusicSession_IsASquareCollageOfAtMostNineCovers()
    {
        var covers = Enumerable.Range(0, 12).Select(i => $"/engine-activity-artwork/{new string((char)('a' + i % 6), 64)}").ToList();
        _recent = new([Tile(PersonalActivityKindValues.MusicSession, 3600, shape: PersonalActivityShapeValues.Square, covers: covers, songs: 50, albums: 12)]);

        var cut = RenderPage();

        var collage = cut.Find("[data-activity-collage]");
        Assert.Equal(9, collage.QuerySelectorAll("img").Length);
        Assert.Contains("activity-tile__collage--3", collage.ClassName);
    }

    [Fact]
    public void MusicSession_WithFewCovers_ShowsOnlyThoseCovers()
    {
        _recent = new([Tile(PersonalActivityKindValues.MusicSession, 3600, shape: PersonalActivityShapeValues.Square, covers: [$"/engine-activity-artwork/{Key}", $"/engine-activity-artwork/{new string('c', 64)}"], songs: 4, albums: 2)]);

        var cut = RenderPage();

        Assert.Equal(2, cut.Find("[data-activity-collage]").QuerySelectorAll("img").Length);
    }

    [Fact]
    public void HoverCard_OnAHoverCapableScreen_ShowsTheLargerImageTitleEpisodeWhenAndHowLong()
    {
        _recent = new([Tile(PersonalActivityKindValues.Watch, 52 * 60, progress: 40, subtitle: "S2 E4 · Fear", title: "Tremors Show", artwork: $"/engine-activity-artwork/{Key}", workId: WorkA)]);
        var cut = RenderPage();
        Assert.Empty(cut.FindAll("[data-activity-card]"));

        cut.Find(".activity-tile").MouseEnter();

        var card = cut.Find("[data-activity-card]");
        Assert.Equal("Tremors Show", card.QuerySelector(".activity-card__title")!.TextContent);
        Assert.Contains("S2 E4 · Fear", card.TextContent, StringComparison.Ordinal);
        Assert.Equal(2, card.QuerySelectorAll(".activity-card__line--muted").Length);
        Assert.Contains("Watched 52 m", card.TextContent, StringComparison.Ordinal);
        Assert.Equal($"/engine-activity-artwork/{Key}", card.QuerySelector("img.activity-card__image")!.GetAttribute("src"));

        cut.Find(".activity-tile").MouseLeave();
        Assert.Empty(cut.FindAll("[data-activity-card]"));
    }

    [Fact]
    public void HoverCard_AlsoOpensOnKeyboardFocus()
    {
        _recent = new([Tile(PersonalActivityKindValues.Read, 600, workId: WorkA, title: "Dune Book")]);
        var cut = RenderPage();

        cut.Find(".activity-tile").FocusIn();

        Assert.Equal("Dune Book", cut.Find(".activity-card__title").TextContent);
        cut.Find(".activity-tile").FocusOut();
        Assert.Empty(cut.FindAll("[data-activity-card]"));
    }

    [Fact]
    public void HoverCard_ForAMusicSession_SaysHowManySongsFromHowManyAlbums()
    {
        _recent = new([Tile(PersonalActivityKindValues.MusicSession, 3900, shape: PersonalActivityShapeValues.Square, title: string.Empty, songs: 50, albums: 12, covers: [$"/engine-activity-artwork/{Key}"])]);
        var cut = RenderPage();

        cut.Find(".activity-tile").MouseEnter();

        var card = cut.Find("[data-activity-card]");
        Assert.Equal("Music", card.QuerySelector(".activity-card__title")!.TextContent);
        Assert.Contains("50 songs from 12 albums", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("Played 1 h 05 m", card.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnAPhone_ThereIsNoHoverCard_AndATapOpensTheItem()
    {
        // The phone has no hover, and each test context holds one device, so this one builds its own.
        await using var ctx = new PhoneContext();
        var cut = ctx.RenderWithTiles([Tile(PersonalActivityKindValues.Watch, 600, workId: WorkA, title: "Tremors Show")]);

        cut.Find(".activity-tile").MouseEnter();
        cut.Find(".activity-tile").FocusIn();

        Assert.Empty(cut.FindAll("[data-activity-card]"));
        Assert.Equal($"/details/work/{WorkA}?context=watch", cut.Find(".activity-tile__link").GetAttribute("href"));
        Assert.Contains("overview-page--phone", cut.Find("[data-settings-overview]").ClassName);
    }

    [Fact]
    public void ATap_OpensTheItem_ForAWatchedItem_AndTheActivityPageForAMusicSessionOrAnItemThatIsGone()
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
            cut.FindAll(".activity-tile__link").Select(link => link.GetAttribute("href")!).ToArray());
    }

    [Fact]
    public void SeeAll_OpensTheActivityPage_AndSeeYourTasteOpensTheTastePage()
    {
        var cut = RenderPage();

        Assert.Equal("/settings/overview/activity", cut.Find("[data-overview-see-all]").GetAttribute("href"));
        Assert.Equal("/settings/overview/taste", cut.Find("[data-overview-taste-link]").GetAttribute("href"));
    }

    [Fact]
    public void EmptyStrip_SaysWhatShowsUpThere()
    {
        _recent = new([]);

        var cut = RenderPage();

        Assert.Equal("Nothing yet — what you watch, read and listen to shows up here.", cut.Find("[data-activity-empty]").TextContent);
        Assert.Empty(cut.FindAll("[data-activity-strip]"));
    }

    [Fact]
    public void StripThatCannotBeLoaded_SaysSoInsteadOfPretendingNothingHappened()
    {
        _recent = null;

        var cut = RenderPage();

        Assert.NotEmpty(cut.FindAll("[data-activity-unavailable]"));
        Assert.Empty(cut.FindAll("[data-activity-empty]"));
    }

    [Fact]
    public void YourTaste_SummarisesTheTopGenresAndMoodsInOneLine()
    {
        _overview = Overview(taste: new TasteProfileDto
        {
            GenreDistribution = new Dictionary<string, double> { ["Science fiction"] = 0.9, ["Thriller"] = 0.6, ["Drama"] = 0.4, ["Comedy"] = 0.1 },
            MoodPreferences = new Dictionary<string, double> { ["Tense"] = 0.8 },
        });

        var cut = RenderPage();

        Assert.Equal("Mostly Science fiction, Thriller and Drama, in a tense mood.", cut.Find("[data-overview-taste-summary]").TextContent);
    }

    [Fact]
    public void YourTaste_WithNothingYet_SaysSoAndStillLinksToTheTastePage()
    {
        var cut = RenderPage();

        Assert.StartsWith("Your taste appears here", cut.Find("[data-overview-taste-summary]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-overview-taste-link]"));
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

    private static AngleSharp.Dom.IElement EditProfileButton(IRenderedComponent<OverviewPage> cut) =>
        cut.FindAll("button").First(button => button.TextContent.Contains("Edit profile", StringComparison.Ordinal));

    private static string CountValue(IRenderedComponent<OverviewPage> cut, string key) =>
        cut.Find($"[data-overview-count='{key}'] .overview-counts__value").TextContent;

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
                new GrantAdminProtectionDto(false, "None", null, 1, false, null), 1, DateTimeOffset.UtcNow);
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
