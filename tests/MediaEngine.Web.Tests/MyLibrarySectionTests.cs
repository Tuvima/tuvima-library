using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>My Library, the personal page: which sections show, which hide, and who sees what.</summary>
public sealed class MyLibrarySectionTests : AsyncBunitContext
{
    private static readonly Guid OwnerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private IEngineApiClient? _api;
    private readonly List<(string? Lane, string? MediaType, int? Limit)> _browseCalls = [];
    private readonly List<IReadOnlyList<Guid>> _itemCalls = [];
    private int _catalogCalls, _managedCalls;

    public MyLibrarySectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // The test sets _api before the page renders; the first resolve captures it.
        Services.AddSingleton(_ => _api ??= EngineApiClientStub.CreateDefault());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<ViewWorkspaceService>();
        Services.AddScoped(provider => new DeviceContextService(provider.GetRequiredService<IEngineApiClient>()));
    }

    private static ProfileViewModel Adult(Guid? id = null, string name = "Shy") =>
        new(id ?? OwnerId, name, "#C9922E", "Administrator", DateTimeOffset.UtcNow);

    private static ProfileViewModel Kid(string name = "Mina") =>
        new(OwnerId, name, "#7C4DFF", "RestrictedProfile", DateTimeOffset.UtcNow);

    private static DisplayArtworkDto Art()
    {
        var fields = typeof(DisplayArtworkDto).GetProperties()
            .Where(p => p.Name.EndsWith("Url", StringComparison.Ordinal))
            .ToDictionary(p => p.Name, _ => "/art.jpg");
        return JsonSerializer.Deserialize<DisplayArtworkDto>(JsonSerializer.Serialize(fields))!;
    }

    private static DisplayCardDto Card(Guid id, string kind, string title, double? percent = null,
        DisplayContinuationState state = DisplayContinuationState.Unstarted, int minutesAgo = 0) =>
        new(id, id, Guid.NewGuid(), null, kind, "work", title, null, [], Art(), "portrait", "default", "caption", "bottom",
            percent is null ? null : new DisplayProgressDto(percent.Value, state == DisplayContinuationState.Completed ? "Watched" : $"{percent:F0}%", Now.AddMinutes(-minutesAgo), null),
            [], new(true, false, false, false), Now.AddDays(-30))
        { ContinuationState = state };

    private static DisplayPageDto Page(params DisplayCardDto[] cards) =>
        new("page", "Page", null, null, [new DisplayShelfDto("shelf", "Shelf", null, cards, null)], cards);

    private sealed record Data
    {
        public List<ProfileViewModel> Profiles { get; init; } = [Adult()];
        public DisplayPageDto? Continue { get; init; }
        /// <summary>What the Engine's item lookup can find: any saved or liked id that has a card here comes back, whatever the library size.</summary>
        public DisplayPageDto? Library { get; init; }
        public DisplayPageDto? Finished { get; init; }
        public List<ProfileSavedItemDto> Saved { get; init; } = [];
        public List<ProfileReactionDto> Reactions { get; init; } = [];
        public bool? CanViewDocuments { get; init; }
        public Task? HoldContinue { get; init; }
        public Task? HoldProfiles { get; init; }
    }

    // Hands the answer back once the gate (if any) is released, so a test can hold one read while the others finish.
    private static async Task<T> After<T>(Task? gate, T value)
    {
        if (gate is not null)
        {
            await gate;
        }

        return value;
    }

    private void UseData(Data data) => _api = EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => After(data.HoldProfiles, data.Profiles));
        stub.SetHandler(nameof(IEngineApiClient.GetSavedItemsAsync), _ => Task.FromResult<IReadOnlyList<ProfileSavedItemDto>>(data.Saved));
        stub.SetHandler(nameof(IEngineApiClient.GetProfileReactionsAsync), _ => Task.FromResult<IReadOnlyList<ProfileReactionDto>>(data.Reactions));
        // args: lane, mediaType, limit, ct, finished. finished=true is History's read; the rest is Continue.
        stub.SetHandler(nameof(IEngineApiClient.GetDisplayContinueAsync), args =>
            args?[4] as bool? == true ? Task.FromResult<DisplayPageDto?>(data.Finished ?? Page()) : After(data.HoldContinue, data.Continue));
        // The lane pages are no longer read by the personal pages; any call is recorded so a test can prove it.
        stub.SetHandler(nameof(IEngineApiClient.GetDisplayBrowseAsync), args =>
        {
            _browseCalls.Add((args?[0] as string, args?[1] as string, args?[5] as int?));
            return Task.FromResult<DisplayPageDto?>(null);
        });
        // The Engine's item lookup: the cards for the asked ids that exist in the seeded library; the rest are simply absent.
        stub.SetHandler(nameof(IEngineApiClient.GetDisplayItemsAsync), args =>
        {
            var ids = (IReadOnlyList<Guid>)args![0]!;
            _itemCalls.Add(ids);
            var found = (data.Library?.Catalog ?? []).Where(card => ids.Contains(card.Id)).ToArray();
            return Task.FromResult<DisplayPageDto?>(Page(found));
        });
        stub.SetHandler(nameof(IEngineApiClient.GetCollectionCatalogAsync), _ => { _catalogCalls++; return Task.FromResult(new List<CollectionManagementCatalogViewModel>()); });
        stub.SetHandler(nameof(IEngineApiClient.GetManagedCollectionsAsync), _ => { _managedCalls++; return Task.FromResult(new List<ManagedCollectionViewModel>()); });
        if (data.CanViewDocuments is { } canView)
        {
            stub.SetHandler(nameof(IEngineApiClient.GetViewScopesAsync), _ =>
                Task.FromResult<ViewScopeResolutionDto?>(new(
                    new ViewResolvedScopeDto(ViewScopeKind.Mine, OwnerId, false), [], CanViewDocuments: canView)));
        }
    });

    private async Task<IRenderedComponent<MyLibrary>> RenderAsync(Data? data = null, DownloadsTabMode? downloads = null, string formFactor = "phone")
    {
        UseData(data ?? new Data());
        var device = Services.GetRequiredService<DeviceContextService>();
        await device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));
        var cut = Render<MyLibrary>(p =>
        {
            if (downloads is { } mode)
            {
                p.Add(c => c.DownloadsMode, mode);
            }
        });
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".my-library-skeleton")), TimeSpan.FromSeconds(10));
        return cut;
    }

    private static string[] Headings(IRenderedComponent<MyLibrary> cut) =>
        cut.FindAll("h2").Select(heading => heading.TextContent.Trim()).ToArray();

    // The seed: one started movie (Continue), one saved movie (My List), one finished show (History), one liked movie (Liked).
    private static Data Everything() => new()
    {
        Continue = Page(Card(Guid.Parse("a0000000-0000-0000-0000-000000000001"), "Movie", "Arrival", 40, DisplayContinuationState.InProgress, 5)),
        Library = Page(
            Card(Guid.Parse("b0000000-0000-0000-0000-000000000001"), "Movie", "Saved Film"),
            Card(Guid.Parse("b0000000-0000-0000-0000-000000000002"), "Movie", "Liked Film")),
        Finished = Page(Card(Guid.Parse("b0000000-0000-0000-0000-000000000003"), "Movie", "Finished Film", 100, DisplayContinuationState.Completed, 60)),
        Saved = [new(ProfileEntityKind.Movie, Guid.Parse("b0000000-0000-0000-0000-000000000001"), Now, null)],
        Reactions = [new(ProfileEntityKind.Movie, Guid.Parse("b0000000-0000-0000-0000-000000000002"), ProfileReactionKind.Like, Now)],
    };

    [Fact]
    public async Task HeadingSaysMyLibraryAndTheProfileNameFollows()
    {
        var cut = await RenderAsync();
        Assert.Equal("My Library", cut.Find("h1").TextContent.Trim());
        Assert.Equal("Shy", cut.Find(".my-library-header__name").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll(".my-library-header__avatar svg, .my-library-header__avatar img"));
    }

    [Fact]
    public async Task EverySectionShowsWhenItHasItemsAndMyListAndLikedKeepTheirSeeAll()
    {
        var cut = await RenderAsync(Everything());

        Assert.Equal(["Continue", "My List", "History", "Liked"], Headings(cut));
        Assert.Contains("Arrival", cut.Markup);
        Assert.Contains("Saved Film", cut.Markup);
        Assert.Contains("Finished Film", cut.Markup);
        Assert.Contains("Liked Film", cut.Markup);

        var seeAll = cut.FindAll("a.media-tile-shelf-link");
        Assert.Equal(["/for-me?view=my-list&area=all", "/for-me?view=liked"], seeAll.Select(link => link.GetAttribute("href")!).ToArray());
        Assert.All(seeAll, link => Assert.Equal("See All", link.TextContent.Trim()));
    }

    [Fact]
    public async Task ASectionWithNoDataIsHiddenNotLeftAsAnEmptyHeader()
    {
        var data = Everything() with { Reactions = [], Finished = Page(), Library = Page(Card(Guid.Parse("b0000000-0000-0000-0000-000000000001"), "Movie", "Saved Film")) };
        var cut = await RenderAsync(data);

        // No finished item and no like: History and Liked are gone, Continue and My List stay.
        Assert.Equal(["Continue", "My List"], Headings(cut));
    }

    [Fact]
    public async Task ContinueIsHiddenWhenNothingHasBeenStarted()
    {
        var cut = await RenderAsync(new Data { Continue = Page(Card(Guid.NewGuid(), "Movie", "Fresh")) });
        Assert.DoesNotContain("Continue", Headings(cut));
        Assert.DoesNotContain("Fresh", cut.Markup);
    }

    [Fact]
    public async Task ANewProfileShowsOnlyTheHeaderAndTheMyListHelperTextWithNoButtonsInIt()
    {
        var cut = await RenderAsync();

        Assert.Equal(["My List"], Headings(cut));
        var helper = cut.Find(".my-library-slot--empty");
        Assert.Contains("Add titles with the + My List button", helper.TextContent);
        // No dead buttons: the helper block has no link, button or See All.
        Assert.Empty(helper.QuerySelectorAll("a, button"));
        Assert.Empty(cut.FindAll(".media-tile"));
        Assert.Empty(cut.FindAll(".my-library-downloads"));
    }

    [Fact]
    public async Task SavedItemsTheProfileCannotSeeLeaveTheHelperTextNotAnEmptyRow()
    {
        // The Engine's lane reads leave out anything this profile may not see, so the saved id has no tile.
        var data = new Data { Saved = [new(ProfileEntityKind.Movie, Guid.NewGuid(), Now, null)] };
        var cut = await RenderAsync(data);
        Assert.Contains("Add titles with the + My List button", cut.Find(".my-library-slot--empty").TextContent);
    }

    [Theory]
    [InlineData(DownloadsTabMode.ExplainNativeApp)]
    [InlineData(DownloadsTabMode.Hidden)]
    public async Task DownloadsCardIsNotRenderedInTheBrowser(DownloadsTabMode mode)
    {
        var cut = await RenderAsync(downloads: mode);
        Assert.Empty(cut.FindAll(".my-library-downloads"));
        Assert.DoesNotContain("Downloads", cut.Markup);
    }

    [Fact]
    public async Task TheBrowserDefaultIsTheExplainNativeAppMode()
    {
        // No mode passed: the shipped switch decides, and the browser Dashboard ships with downloads off.
        var cut = await RenderAsync();
        Assert.Equal(DownloadsTabMode.ExplainNativeApp, ShellDock.DownloadsMode);
        Assert.Empty(cut.FindAll(".my-library-downloads"));
    }

    [Fact]
    public async Task DownloadsCardShowsWhenDownloadsAreAvailable()
    {
        var cut = await RenderAsync(downloads: DownloadsTabMode.Available);
        var card = Assert.Single(cut.FindAll(".my-library-downloads"));
        Assert.Equal("/downloads", card.GetAttribute("href"));
        Assert.Contains("Downloads", card.TextContent);
    }

    [Fact]
    public async Task SwitchProfileIsHiddenWithOneProfile()
    {
        var cut = await RenderAsync(new Data { Profiles = [Adult()] });
        Assert.DoesNotContain("Switch profile", cut.Markup);
        Assert.Empty(cut.FindAll("a[href='/who']"));
    }

    [Fact]
    public async Task SwitchProfileShowsWhenTheHouseholdHasMoreThanOneProfile()
    {
        var cut = await RenderAsync(new Data { Profiles = [Adult(), Adult(SecondId, "Sam")] });
        var link = Assert.Single(cut.FindAll("a.my-library-action[href='/who']"));
        Assert.Equal("Switch profile", link.TextContent.Trim());
    }

    [Fact]
    public async Task SettingsLinkShowsForAdults()
    {
        var cut = await RenderAsync();
        var link = Assert.Single(cut.FindAll("a.my-library-action[href='/settings']"));
        Assert.Equal("Settings", link.TextContent.Trim());
    }

    [Fact]
    public async Task KidsNeverSeeSettingsYetStillSwitchProfilesAndGetNoEditingControls()
    {
        var cut = await RenderAsync(new Data { Profiles = [Kid(), Adult(SecondId, "Parent")], Library = Everything().Library, Saved = Everything().Saved });
        var action = Assert.Single(cut.FindAll("a.my-library-action"));
        Assert.Equal("/who", action.GetAttribute("href"));
        Assert.Empty(cut.FindAll("a[href='/settings']"));
        Assert.DoesNotContain("Settings", cut.Find(".my-library-header").TextContent);
        // Same sections as everyone, and no editing controls anywhere on the page.
        Assert.Contains("My List", Headings(cut));
        Assert.Empty(cut.FindAll("button, input, select, textarea"));
    }

    [Fact]
    public async Task AKidsProfileInAHouseholdOfOneHasNoActionRowAtAll()
    {
        var cut = await RenderAsync(new Data { Profiles = [Kid()] });
        Assert.Empty(cut.FindAll(".my-library-header__actions"));
        Assert.Equal("Mina", cut.Find(".my-library-header__name").TextContent.Trim());
    }

    private static Data WithDocument(bool? canView) => new()
    {
        CanViewDocuments = canView,
        Library = Page(
            Card(Guid.Parse("c0000000-0000-0000-0000-000000000001"), "Document", "Tax Return"),
            Card(Guid.Parse("c0000000-0000-0000-0000-000000000002"), "Movie", "Saved Film")),
        Saved =
        [
            new(ProfileEntityKind.Movie, Guid.Parse("c0000000-0000-0000-0000-000000000001"), Now, null),
            new(ProfileEntityKind.Movie, Guid.Parse("c0000000-0000-0000-0000-000000000002"), Now.AddMinutes(-1), null),
        ],
    };

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ProfilesThatMayNotSeeDocumentsNeverMeetADocumentItem(bool? canView)
    {
        var cut = await RenderAsync(WithDocument(canView));
        Assert.Contains("Saved Film", cut.Markup);
        Assert.DoesNotContain("Tax Return", cut.Markup);
    }

    [Fact]
    public async Task ProfilesThatMaySeeDocumentsKeepThem()
    {
        var cut = await RenderAsync(WithDocument(true));
        Assert.Contains("Tax Return", cut.Markup);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(null, false)]
    [InlineData(true, true)]
    public void TheLikedGridHidesDocumentItemsFromProfilesThatMayNotSeeThem(bool? canView, bool documentShown)
    {
        var document = Guid.Parse("d0000000-0000-0000-0000-000000000001");
        var film = Guid.Parse("d0000000-0000-0000-0000-000000000002");
        UseData(new Data
        {
            CanViewDocuments = canView,
            Library = Page(Card(document, "Document", "Tax Return"), Card(film, "Movie", "Liked Film")),
            Reactions =
            [
                new(ProfileEntityKind.Movie, document, ProfileReactionKind.Like, Now),
                new(ProfileEntityKind.Movie, film, ProfileReactionKind.Like, Now.AddMinutes(-1)),
            ],
        });
        Services.AddSupplyValueFromQueryProvider();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/for-me?view=liked");

        var cut = Render<ForMe>();

        cut.WaitForAssertion(() => Assert.Contains("Liked Film", cut.Markup), TimeSpan.FromSeconds(10));
        Assert.Equal(documentShown, cut.Markup.Contains("Tax Return", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTileIndexIsBuiltFromDirectReadsAndNoLanePageIsRead()
    {
        // Performance note for the PR: one item lookup for the saved ids and one for the liked ids (each a single call while the list is under
        // 200 ids), one finished-items read, one Collection catalogue read and one Playlist read, however many items the profile saved or liked.
        // No lane page is read, so nothing is cut off at a lane page's size.
        await RenderAsync(Everything());

        Assert.Empty(_browseCalls);
        Assert.Equal(2, _itemCalls.Count);
        Assert.Equal([Guid.Parse("b0000000-0000-0000-0000-000000000001")], _itemCalls[0]);
        Assert.Equal([Guid.Parse("b0000000-0000-0000-0000-000000000002")], _itemCalls[1]);
        Assert.Equal(1, _catalogCalls);
        Assert.Equal(1, _managedCalls);
    }

    [Fact]
    public async Task ASavedItemFarBeyondTheFirstTwoHundredFiftyOfALaneStillAppearsInMyList()
    {
        // 300 titles in the library; the profile saved the one at position 270 most recently. A lane page of 250 would have dropped it.
        var cards = Enumerable.Range(0, 300)
            .Select(i => Card(Guid.Parse($"e0000000-0000-0000-0000-{i:000000000000}"), "Movie", $"Title {i:000}"))
            .ToArray();
        var saved = new List<ProfileSavedItemDto>
        {
            new(ProfileEntityKind.Movie, cards[270].Id, Now, null),
            new(ProfileEntityKind.Movie, cards[3].Id, Now.AddHours(-1), null),
        };
        var liked = new List<ProfileReactionDto>
        {
            new(ProfileEntityKind.Movie, cards[299].Id, ProfileReactionKind.Love, Now),
        };

        var cut = await RenderAsync(new Data { Library = Page(cards), Saved = saved, Reactions = liked });

        Assert.Equal(["My List", "Liked"], Headings(cut));
        Assert.Contains("Title 270", cut.Markup);
        Assert.Contains("Title 003", cut.Markup);
        Assert.Contains("Title 299", cut.Markup);
        Assert.Empty(_browseCalls);
    }

    [Fact]
    public async Task HistoryComesFromTheFinishedReadNewestFirst()
    {
        var older = Card(Guid.Parse("f0000000-0000-0000-0000-000000000001"), "Movie", "Older Finish", 100, DisplayContinuationState.Completed, 600);
        var newer = Card(Guid.Parse("f0000000-0000-0000-0000-000000000002"), "Movie", "Newer Finish", 100, DisplayContinuationState.Completed, 5);
        var cut = await RenderAsync(new Data { Finished = Page(older, newer) });

        Assert.Equal(["My List", "History"], Headings(cut));
        Assert.True(cut.Markup.IndexOf("Newer Finish", StringComparison.Ordinal) < cut.Markup.IndexOf("Older Finish", StringComparison.Ordinal));
        Assert.Empty(_browseCalls);
        Assert.Empty(_itemCalls);
    }

    [Fact]
    public async Task SectionsRevealTopToBottomSoContentOnScreenNeverMoves()
    {
        var release = new TaskCompletionSource();
        var data = Everything() with { HoldContinue = release.Task };
        UseData(data);
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        var cut = Render<MyLibrary>();

        // Header is in; Continue is still loading, so every section below it holds its skeleton and none has jumped in.
        cut.WaitForAssertion(() => Assert.Equal("Shy", cut.Find(".my-library-header__name").TextContent.Trim()));
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".my-library-skeleton").Count));
        Assert.Empty(cut.FindAll(".media-tile-shelf"));
        Assert.Empty(cut.FindAll("h2"));

        release.SetResult();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".my-library-skeleton")), TimeSpan.FromSeconds(10));
        Assert.Equal(["Continue", "My List", "History", "Liked"], Headings(cut));
    }

    [Fact]
    public async Task OnlyTheHeadingAndAHeaderSkeletonShowWhileTheProfileLoads()
    {
        var release = new TaskCompletionSource();
        UseData(new Data { HoldProfiles = release.Task });
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        var cut = Render<MyLibrary>();

        Assert.Equal("My Library", cut.Find("h1").TextContent.Trim());
        Assert.Empty(cut.FindAll(".my-library-skeleton"));
        Assert.Empty(cut.FindAll("h2"));
        Assert.NotEmpty(cut.FindAll(".my-library-header__action-skeleton"));

        release.SetResult();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".my-library-header__action-skeleton")), TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("/for-me?view=my-list", true)]
    [InlineData("/for-me?view=my-list&area=listen", false)]
    [InlineData("/for-me?view=my-list&area=all", false)]
    [InlineData("/for-me?view=liked", false)]
    public void TheOldForMeMyListLinkLandsOnThePageButFilteredGridLinksStayOnTheGrid(string url, bool landsOnMyLibrary)
    {
        UseData(new Data());
        Services.AddSupplyValueFromQueryProvider();
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(url);

        Render<ForMe>();

        if (landsOnMyLibrary)
        {
            Assert.EndsWith("/my-library", nav.Uri);
        }
        else
        {
            Assert.EndsWith(url, nav.Uri);
        }
    }

    [Fact]
    public void MyLibraryIsTheDockDestinationAndTheListGridHasItsOwnRoute()
    {
        Assert.Equal("/my-library", ShellDock.MyLibraryPath);
        var entry = Assert.Single(ShellDock.ResolvePhone(isKids: false), tab => tab.Key == "MyLibrary");
        Assert.Equal("/my-library", entry.Path);
        Assert.Equal("/for-me?view=my-list&area=all", ForMe.MyListGridRoute);
    }
}
