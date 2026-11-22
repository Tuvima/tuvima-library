using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Playlists page and the sidebar (Listen playlists spec WP3, mockup A and D): a page of tiles with Sort and New playlist (Hand-picked or
/// Smart, on a phone too), tiles that take dropped songs and say how it went, a phone with no sidebar and a desktop that keeps it, a name box
/// with a Create button that closes when it is left, and Pin to top putting a playlist first. The Engine is a hand-written spy.
/// </summary>
public sealed class ListenPlaylistsPageTests : AsyncBunitContext
{
    private static readonly Guid ActiveId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid RoadTrip = Guid.Parse("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid Focus = Guid.Parse("b1000000-0000-0000-0000-000000000002");
    private static readonly Guid Jazz = Guid.Parse("b1000000-0000-0000-0000-000000000003");

    private readonly List<(Guid Playlist, Guid Work)> _added = [];
    private List<ManagedCollectionViewModel> _collections = [];
    private readonly List<string?> _savedNavigationConfigs = [];

    public ListenPlaylistsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddScoped<ListenAudioDragService>();
        Services.AddScoped<SongDragDropService>();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(ActiveId, "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetManagedCollectionsAsync), _ => Task.FromResult(_collections));
            stub.SetHandler(nameof(IEngineApiClient.UpdateProfileAsync), args =>
            {
                _savedNavigationConfigs.Add((string?)args![3]);
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.AddCollectionItemsAsync), args =>
                PlaylistAddSpy.Answer(args, [], (playlist, work) => _added.Add((playlist, work))));
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new DefaultPreferences());
        Services.AddSingleton(provider => new PlaybackSessionController(
            null!, null!, preferences: provider.GetRequiredService<IUserPlaybackPreferencesAccessor>()));
    }

    private Task UseFormFactorAsync(string formFactor) =>
        Services.GetRequiredService<DeviceContextService>().InitialiseAsync(
            new DeviceContextSnapshot("web", formFactor, false, FinePointer: formFactor == "desktop", Hover: formFactor == "desktop"));

    private static ManagedCollectionViewModel Playlist(
        Guid id,
        string name,
        string type = "Playlist",
        int items = 3,
        bool pinned = false,
        double? seconds = null,
        DateTimeOffset? created = null) => new()
        {
            Id = id,
            Name = name,
            CollectionType = type,
            MembershipMode = type == "Smart" ? "Smart" : "Manual",
            Resolution = type == "Smart" ? "query" : "materialized",
            CanEdit = true,
            IsEnabled = true,
            ItemCount = items,
            IsPinned = pinned,
            TotalDurationSeconds = seconds,
            CreatedAt = created ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

    // -- The Playlists page of tiles --------------------------------------------------------------------

    private (IRenderedComponent<AppDialogHost> Host, IRenderedComponent<PlaylistTilesView> Page) RenderTiles(
        IReadOnlyList<ManagedCollectionViewModel> playlists,
        List<string>? handPicked = null,
        List<string>? smart = null)
    {
        var host = Render<AppDialogHost>();
        Render<AppPopoverHost>();
        var page = Render<PlaylistTilesView>(parameters => parameters
            .Add(view => view.Playlists, playlists)
            .Add(view => view.OnCreateHandPicked, new EventCallback<string>(null, (Action<string>)(name => handPicked?.Add(name))))
            .Add(view => view.OnCreateSmart, new EventCallback<string>(null, (Action<string>)(name => smart?.Add(name)))));
        return (host, page);
    }

    private static List<string> TileNames(IRenderedComponent<PlaylistTilesView> page) =>
        [.. page.FindAll("[data-playlist-tile] article.media-tile").Select(tile => tile.GetAttribute("aria-label")!)];

    private static List<string> TypeLines(IRenderedComponent<PlaylistTilesView> page) =>
        [.. page.FindAll("[data-playlist-tile] .media-tile-label--bottom").Select(line => line.TextContent.Trim())];

    [Fact]
    public async Task ThePlaylistsPage_OnAPhone_OffersNewHandPickedAndNewSmart()
    {
        await UseFormFactorAsync("phone");
        var handPicked = new List<string>();
        var smart = new List<string>();
        var (host, page) = RenderTiles([Playlist(RoadTrip, "Road trip")], handPicked, smart);

        Assert.Equal("New", page.Find("[data-playlist-new]").TextContent.Trim());
        page.Find("[data-playlist-new]").Click();
        host.WaitForElement("[data-new-playlist]");
        Assert.Equal("Hand-picked", host.Find("[data-new-playlist-handpicked]").TextContent.Trim());
        Assert.Equal("Smart", host.Find("[data-new-playlist-smart]").TextContent.Trim());

        host.Find("[data-new-playlist-name]").Input("Late night");
        host.Find("[data-new-playlist-handpicked]").Click();
        page.WaitForAssertion(() => Assert.Equal(["Late night"], handPicked));

        page.Find("[data-playlist-new]").Click();
        host.WaitForElement("[data-new-playlist]");
        host.Find("[data-new-playlist-name]").Input("Focus");
        host.Find("[data-new-playlist-smart]").Click();
        page.WaitForAssertion(() => Assert.Equal(["Focus"], smart));
    }

    [Fact]
    public async Task HandPickedNeedsANameButSmartDoesNot()
    {
        await UseFormFactorAsync("desktop");
        var (host, page) = RenderTiles([]);

        page.Find("[data-playlist-new]").Click();
        host.WaitForElement("[data-new-playlist]");

        Assert.True(host.Find("[data-new-playlist-handpicked]").HasAttribute("disabled"));
        Assert.False(host.Find("[data-new-playlist-smart]").HasAttribute("disabled"));
        host.Find("[data-new-playlist-name]").Input("Road trip");
        Assert.False(host.Find("[data-new-playlist-handpicked]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task OnADesktopATileSaysTheTypeInWordsTheCountAndTheLength()
    {
        await UseFormFactorAsync("desktop");
        var (_, page) = RenderTiles(
        [
            Playlist(RoadTrip, "Road trip", items: 42, seconds: 2 * 3600 + 55 * 60),
            Playlist(Focus, "Focus", "Smart", items: 310),
        ]);

        var meta = TypeLines(page);

        Assert.Contains("Playlist · 42 songs · 2h 55m", meta);
        Assert.Contains("Smart playlist · 310 songs", meta);
    }

    [Fact]
    public async Task OnAPhoneATileLeavesOutTheLengthSoTwoFitAcross()
    {
        await UseFormFactorAsync("phone");
        var (_, page) = RenderTiles(
        [
            Playlist(RoadTrip, "Road trip", items: 42),
            Playlist(Focus, "Focus", "Smart", items: 310),
        ]);

        var meta = TypeLines(page);

        Assert.Equal(["Playlist · 42 songs", "Smart playlist · 310 songs"], meta);
    }

    [Fact]
    public async Task ATileIsFilledWithTheFirstFourAlbumsOfItsSongsAndAPlaylistWithNoCoversShowsThePlaylistIcon()
    {
        await UseFormFactorAsync("desktop");
        Render<AppPopoverHost>();
        var page = Render<PlaylistTilesView>(parameters => parameters
            .Add(view => view.Playlists, [Playlist(RoadTrip, "Road trip"), Playlist(Focus, "Focus", "Smart")])
            .Add(view => view.AlbumCovers, new Dictionary<Guid, IReadOnlyList<string>>
            {
                [RoadTrip] = ["/c/1.jpg", "/c/2.jpg", "/c/3.jpg"],
            }));

        var roadTrip = page.Find($"[data-playlist-tile='{RoadTrip:D}']");
        var focus = page.Find($"[data-playlist-tile='{Focus:D}']");

        Assert.Equal(3, roadTrip.QuerySelectorAll("img.media-tile-quadrant").Length);
        Assert.Single(roadTrip.QuerySelectorAll(".media-tile-quadrant.is-empty"));
        Assert.Empty(focus.QuerySelectorAll(".media-tile-quadrants"));
        Assert.Single(focus.QuerySelectorAll(".media-tile-fallback"));
    }

    [Fact]
    public async Task EveryTileCarriesTheSharedMenuAndASmartTileAddsEditRules()
    {
        await UseFormFactorAsync("desktop");
        var (_, page) = RenderTiles([Playlist(RoadTrip, "Road trip"), Playlist(Focus, "Focus", "Smart")]);

        var triggers = page.FindAll("[data-playlist-tile] .app-overflow-menu__trigger");

        Assert.Equal(2, triggers.Count);
        Assert.Equal(["Road trip options", "Focus options"], triggers.Select(trigger => trigger.GetAttribute("aria-label")).ToList());
    }

    [Fact]
    public async Task SortReordersTheTilesByNameAndByRecentlyAddedAndKeepsPinnedFirst()
    {
        await UseFormFactorAsync("desktop");
        var (_, page) = RenderTiles(
        [
            Playlist(Jazz, "Sunday jazz", created: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            Playlist(RoadTrip, "Road trip", created: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Playlist(Focus, "Focus", "Smart", pinned: true, created: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
        ]);

        Assert.Equal(["Focus", "Sunday jazz", "Road trip"], TileNames(page));
        Assert.Equal("Sort: Manual", page.Find(".playlist-tiles__sort .app-overflow-menu__trigger").TextContent.Trim());

        page.Find(".playlist-tiles__sort .app-overflow-menu__trigger").Click();
        _ = page.FindAll("button.app-menu-item").Single(item => item.TextContent.Trim() == "Name").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Equal(["Focus", "Road trip", "Sunday jazz"], TileNames(page)));

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("button.app-menu-item")));
        page.Find(".playlist-tiles__sort .app-overflow-menu__trigger").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.app-menu-item")));
        _ = page.FindAll("button.app-menu-item").Single(item => item.TextContent.Trim() == "Recently added").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Equal(["Focus", "Sunday jazz", "Road trip"], TileNames(page)));
    }

    [Fact]
    public async Task ANoPlaylistsPageSaysSoAndKeepsNewPlaylistToHand()
    {
        await UseFormFactorAsync("desktop");
        var (_, page) = RenderTiles([]);

        Assert.Contains("No playlists yet", page.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(page.FindAll("[data-playlist-new]"));
        Assert.Empty(page.FindAll("[data-playlist-tile]"));
    }

    // -- Dropping songs on a tile -------------------------------------------------------------------------

    private static void CarrySongs(SongDragDropService drops, object owner, int count) =>
        drops.Begin(owner, [.. Enumerable.Range(1, count).Select(index => Guid.Parse($"50000000-0000-0000-0000-{index:D12}"))]);

    [Fact]
    public async Task DroppingSongsOnAHandMadeTileAddsThemAndSaysTheSentenceInPlace()
    {
        await UseFormFactorAsync("desktop");
        var (_, page) = RenderTiles([Playlist(RoadTrip, "Road trip")]);
        var drops = Services.GetRequiredService<SongDragDropService>();
        CarrySongs(drops, new object(), 3);

        var tile = page.Find($"[data-playlist-tile='{RoadTrip:D}'] article.media-tile");
        Assert.Equal("playlist", tile.GetAttribute("data-song-drop"));
        tile.TriggerEvent("ondrop", new DragEventArgs());

        page.WaitForAssertion(() => Assert.Equal("3 songs added to Road trip", page.Find("[data-playlist-drop-notice]").TextContent.Trim()));
        Assert.Equal(3, _added.Count);
        Assert.All(_added, entry => Assert.Equal(RoadTrip, entry.Playlist));
    }

    [Fact]
    public async Task ASmartListTileRefusesDroppedSongs()
    {
        await UseFormFactorAsync("desktop");
        var (_, page) = RenderTiles([Playlist(Focus, "Focus", "Smart", items: 310)]);
        var drops = Services.GetRequiredService<SongDragDropService>();
        CarrySongs(drops, new object(), 2);

        var tile = page.Find($"[data-playlist-tile='{Focus:D}'] article.media-tile");
        Assert.Null(tile.GetAttribute("data-song-drop"));
        tile.TriggerEvent("ondrop", new DragEventArgs());

        Assert.Empty(_added);
        Assert.Empty(page.FindAll("[data-playlist-drop-notice]"));
        Assert.True(drops.IsCarrying);
    }

    [Fact]
    public async Task TheFavoritesTileTakesNoDropsAndHasNoMenu()
    {
        await UseFormFactorAsync("desktop");
        Render<AppPopoverHost>();
        var page = Render<PlaylistTilesView>(parameters => parameters
            .Add(view => view.Playlists, [Playlist(RoadTrip, "Road trip")])
            .Add(view => view.ShowFavorites, true)
            .Add(view => view.FavoritesCount, 128));

        var favorites = page.Find("[data-playlist-tile='favorites']");

        Assert.Equal("Favorites", favorites.QuerySelector("article.media-tile")!.GetAttribute("aria-label"));
        Assert.Equal("Built in · 128 songs", favorites.QuerySelector(".media-tile-label--bottom")!.TextContent.Trim());
        Assert.Null(favorites.QuerySelector("article.media-tile")!.GetAttribute("data-song-drop"));
        Assert.Empty(favorites.QuerySelectorAll(".app-overflow-menu__trigger"));
        Assert.Equal("favorites", page.Find("[data-playlist-tile]").GetAttribute("data-playlist-tile"));
    }

    // -- The name box ----------------------------------------------------------------------------------

    private IRenderedComponent<PlaylistNameBox> RenderNameBox(List<string> created, List<string> cancelled, TimeSpan? grace = null) =>
        Render<PlaylistNameBox>(parameters => parameters
            .Add(box => box.Placeholder, "Playlist name")
            .Add(box => box.BlurGrace, grace ?? TimeSpan.FromMilliseconds(20))
            .Add(box => box.OnCreate, new EventCallback<string>(null, (Action<string>)created.Add))
            .Add(box => box.OnCancel, new EventCallback(null, (Action)(() => cancelled.Add("closed")))));

    [Fact]
    public void TheNameBoxHasACreateButtonThatWaitsForAName()
    {
        var created = new List<string>();
        var cut = RenderNameBox(created, []);

        Assert.Equal("Create", cut.Find("[data-playlist-name-create]").TextContent.Trim());
        Assert.True(cut.Find("[data-playlist-name-create]").HasAttribute("disabled"));

        cut.Find("input").Input("  Road trip  ");
        cut.Find("[data-playlist-name-create]").Click();

        Assert.Equal(["Road trip"], created);
    }

    [Fact]
    public void EnterInTheNameBoxCreatesAndEscapeClosesIt()
    {
        var created = new List<string>();
        var cancelled = new List<string>();
        var cut = RenderNameBox(created, cancelled);

        cut.Find("input").Input("Focus");
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Equal(["Focus"], created);
        Assert.Single(cancelled);
    }

    [Fact]
    public void TheNameBoxClosesWhenTheFocusLeavesIt()
    {
        var cancelled = new List<string>();
        var cut = RenderNameBox([], cancelled);

        cut.Find("[data-playlist-name-box]").TriggerEvent("onfocusout", new FocusEventArgs());

        cut.WaitForAssertion(() => Assert.Single(cancelled));
    }

    [Fact]
    public async Task MovingTheFocusFromTheFieldToCreateKeepsTheNameBoxOpen()
    {
        var cancelled = new List<string>();
        var cut = RenderNameBox([], cancelled, TimeSpan.FromMilliseconds(150));

        cut.Find("[data-playlist-name-box]").TriggerEvent("onfocusout", new FocusEventArgs());
        cut.Find("[data-playlist-name-box]").TriggerEvent("onfocusin", new FocusEventArgs());
        await Task.Delay(400);

        Assert.Empty(cancelled);
    }

    [Fact]
    public async Task TheSidebarNewPlaylistBoxClosesOnBlurAndCreatesWithItsButton()
    {
        await UseFormFactorAsync("desktop");
        _collections = [Playlist(RoadTrip, "Road trip")];
        Render<AppPopoverHost>();
        Render<AppDialogHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.Section, "playlists"));
        page.WaitForElement(".listen-rail__playlist-row");

        page.Find(".listen-rail__add-menu .app-overflow-menu__trigger").Click();
        await page.FindAll("button.app-menu-item").Single(item => item.TextContent.Contains("New playlist", StringComparison.OrdinalIgnoreCase)).ClickAsync(new());
        page.WaitForElement("[data-playlist-name-box]");
        Assert.Equal("Create", page.Find("[data-playlist-name-create]").TextContent.Trim());

        page.Find("[data-playlist-name-box]").TriggerEvent("onfocusout", new FocusEventArgs());
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-playlist-name-box]")));
    }

    // -- A phone has no sidebar, a desktop keeps it ----------------------------------------------------

    [Fact]
    public async Task OnAPhoneTheListenPageDrawsNoSidebarAndAPlaylistLeadsBackToThePlaylistsPage()
    {
        await UseFormFactorAsync("phone");
        _collections = [Playlist(RoadTrip, "Road trip")];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/listen/music/playlists/{RoadTrip:D}");
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.CollectionId, RoadTrip));
        page.WaitForElement(".playlist-hero__copy");

        Assert.Empty(page.FindAll(".listen-rail-shell"));
        Assert.Contains("listen-page--no-rail", page.Find(".listen-page").ClassName, StringComparison.Ordinal);
        var back = page.Find(".listen-back-link");
        Assert.Equal("Playlists", back.TextContent.Trim());
        Assert.Equal(ListenNavigation.PlaylistsRoute, back.GetAttribute("href"));
    }

    [Fact]
    public async Task OnADesktopTheListenPageKeepsTheSidebarWithEveryHandMadePlaylistAsADropTarget()
    {
        await UseFormFactorAsync("desktop");
        _collections =
        [
            Playlist(RoadTrip, "Road trip"),
            Playlist(Focus, "Focus", "Smart"),
        ];
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.Section, "playlists"));
        page.WaitForElement(".listen-rail__playlist-row");

        Assert.NotEmpty(page.FindAll(".listen-rail-shell"));
        Assert.Empty(page.FindAll(".listen-back-link"));
        Assert.Equal("new-playlist", page.Find(".listen-rail__section-heading").GetAttribute("data-song-drop"));
        var rows = page.FindAll(".listen-rail__playlist-row");
        Assert.Equal("playlist", rows[0].QuerySelector("a[data-song-drop]")?.GetAttribute("data-song-drop") ?? rows[1].QuerySelector("a[data-song-drop]")?.GetAttribute("data-song-drop"));
        Assert.Single(page.FindAll(".listen-rail__playlist-row a[data-song-drop]"));
        Assert.Equal("See all", page.Find(".listen-rail__see-all").TextContent.Trim());
        Assert.Equal(ListenNavigation.PlaylistsRoute, page.Find(".listen-rail__see-all").GetAttribute("href"));
        Assert.Equal(["Road trip options", "Focus options"], page.FindAll(".listen-rail__playlist-row .app-overflow-menu__trigger").Select(trigger => trigger.GetAttribute("aria-label")).Order().Reverse().ToList());
    }

    // -- Pin to top -------------------------------------------------------------------------------------

    [Fact]
    public async Task APinnedPlaylistIsFirstInTheSidebarWhateverTheSavedOrder()
    {
        await UseFormFactorAsync("desktop");
        _collections =
        [
            Playlist(RoadTrip, "Road trip"),
            Playlist(Jazz, "Sunday jazz", pinned: true),
        ];
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.Section, "playlists"));
        page.WaitForElement(".listen-rail__playlist-row");

        var names = page.FindAll(".listen-rail__playlist-row .listen-rail__item span:not(.listen-rail__thumb)")
            .Select(span => span.TextContent.Trim()).Where(text => text.Length > 0).ToList();

        Assert.Equal(["Sunday jazz", "Road trip"], names);
    }

    [Fact]
    public void PinnedPlaylistsComeFirstThenTheProfilesOwnOrderThenTheNewestOfTheRest()
    {
        var older = Playlist(RoadTrip, "Road trip", created: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Playlist(Focus, "Focus", created: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var pinned = Playlist(Jazz, "Sunday jazz", pinned: true);

        var inOrder = ListenPlaylistOrderState.Order([older, newer, pinned], [older.Id]);
        var unknownOrder = ListenPlaylistOrderState.Order([older, newer, pinned], []);

        Assert.Equal(["Sunday jazz", "Road trip", "Focus"], inOrder.Select(playlist => playlist.Name));
        Assert.Equal(["Sunday jazz", "Focus", "Road trip"], unknownOrder.Select(playlist => playlist.Name));
    }

    [Fact]
    public void TheOldSavedOrderIsStillReadAndFollowsTheProfile()
    {
        var written = ListenPlaylistOrderState.Write(null, [Jazz, RoadTrip]);

        Assert.Equal([Jazz, RoadTrip], ListenPlaylistOrderState.Read(written));
    }

    // -- Move up and Move down, and the finger's drag, for the playlists themselves (spec listen-playlists WP5, D3) ---------

    private static async Task OpenTileMenuAsync(IRenderedComponent<PlaylistTilesView> page, string playlistName)
    {
        var tile = page.FindAll("[data-playlist-tile]").Single(candidate => candidate.QuerySelector("article.media-tile")!.GetAttribute("aria-label") == playlistName);
        await tile.QuerySelector(".app-row-menu__trigger")!.ClickAsync(new());
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.app-menu-item")));
    }

    private static List<string> MenuWords(IRenderedComponent<PlaylistTilesView> page) =>
        [.. page.FindAll("button.app-menu-item").Select(item => item.TextContent.Trim())];

    [Fact]
    public async Task APlaylistTilesMenuHasMoveUpAndMoveDownThatHandTheTileToThePage()
    {
        await UseFormFactorAsync("desktop");
        var up = new List<Guid>();
        var down = new List<Guid>();
        Render<AppPopoverHost>();
        var page = Render<PlaylistTilesView>(parameters => parameters
            .Add(view => view.Playlists, [Playlist(RoadTrip, "Road trip"), Playlist(Focus, "Focus"), Playlist(Jazz, "Sunday jazz")])
            .Add(view => view.OnMoveUp, new EventCallback<ManagedCollectionViewModel>(null, (Action<ManagedCollectionViewModel>)(playlist => up.Add(playlist.Id))))
            .Add(view => view.OnMoveDown, new EventCallback<ManagedCollectionViewModel>(null, (Action<ManagedCollectionViewModel>)(playlist => down.Add(playlist.Id)))));

        await OpenTileMenuAsync(page, "Focus");
        Assert.Contains("Move up", MenuWords(page));
        Assert.Contains("Move down", MenuWords(page));
        await page.FindAll("button.app-menu-item").Single(item => item.TextContent.Trim() == "Move down").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Equal([Focus], down));
        Assert.Empty(up);
        Assert.Equal("Moved Focus to place 3 of 3", page.Find("[data-playlist-tiles-announce]").TextContent.Trim());
    }

    [Fact]
    public async Task TheFirstTileCannotMoveUpAndTheLastCannotMoveDown()
    {
        await UseFormFactorAsync("desktop");
        Render<AppPopoverHost>();
        var page = Render<PlaylistTilesView>(parameters => parameters
            .Add(view => view.Playlists, [Playlist(RoadTrip, "Road trip"), Playlist(Focus, "Focus")])
            .Add(view => view.OnMoveUp, new EventCallback<ManagedCollectionViewModel>(null, (Action<ManagedCollectionViewModel>)(_ => { })))
            .Add(view => view.OnMoveDown, new EventCallback<ManagedCollectionViewModel>(null, (Action<ManagedCollectionViewModel>)(_ => { }))));

        await OpenTileMenuAsync(page, "Road trip");

        Assert.True(page.FindAll("button.app-menu-item").Single(item => item.TextContent.Trim() == "Move up").HasAttribute("disabled"));
        Assert.False(page.FindAll("button.app-menu-item").Single(item => item.TextContent.Trim() == "Move down").HasAttribute("disabled"));
    }

    [Fact]
    public async Task APinnedPlaylistAndAnUnpinnedOneNeverSwapPlacesBecausePinnedAlwaysComeFirst()
    {
        await UseFormFactorAsync("desktop");
        Render<AppPopoverHost>();
        var page = Render<PlaylistTilesView>(parameters => parameters
            .Add(view => view.Playlists, [Playlist(Jazz, "Sunday jazz", pinned: true), Playlist(RoadTrip, "Road trip")])
            .Add(view => view.OnMoveUp, new EventCallback<ManagedCollectionViewModel>(null, (Action<ManagedCollectionViewModel>)(_ => { })))
            .Add(view => view.OnMoveDown, new EventCallback<ManagedCollectionViewModel>(null, (Action<ManagedCollectionViewModel>)(_ => { }))));

        await OpenTileMenuAsync(page, "Sunday jazz");

        Assert.True(page.FindAll("button.app-menu-item").Single(item => item.TextContent.Trim() == "Move down").HasAttribute("disabled"));
    }

    [Fact]
    public async Task TheTilesMenuHasNoMoveRowsInANameOrNewestSortBecauseTheOrderThereIsNotTheProfilesOwn()
    {
        await UseFormFactorAsync("desktop");
        Render<AppPopoverHost>();
        var page = Render<PlaylistTilesView>(parameters => parameters
            .Add(view => view.Playlists, [Playlist(RoadTrip, "Road trip"), Playlist(Focus, "Focus")])
            .Add(view => view.OnMoveUp, new EventCallback<ManagedCollectionViewModel>(null, (Action<ManagedCollectionViewModel>)(_ => { })))
            .Add(view => view.OnMoveDown, new EventCallback<ManagedCollectionViewModel>(null, (Action<ManagedCollectionViewModel>)(_ => { }))));
        await page.Find(".playlist-tiles__sort .app-overflow-menu__trigger").ClickAsync(new());
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.app-menu-item")));
        await page.FindAll("button.app-menu-item").Single(item => item.TextContent.Trim() == "Name").ClickAsync(new());

        await OpenTileMenuAsync(page, "Focus");

        Assert.DoesNotContain("Move up", MenuWords(page));
        Assert.DoesNotContain("Move down", MenuWords(page));
    }

    [Fact]
    public async Task ThePlaylistsPageWithoutMoveHandlersLeavesTheMenuAsItWas()
    {
        await UseFormFactorAsync("desktop");
        var (_, page) = RenderTiles([Playlist(RoadTrip, "Road trip"), Playlist(Focus, "Focus")]);

        await OpenTileMenuAsync(page, "Focus");

        Assert.Equal(["Rename", "Edit description and cover", "Duplicate", "Pin to top", "Delete"], MenuWords(page));
    }

    [Fact]
    public async Task AFingerDroppingASidebarPlaylistAfterAnotherSavesTheNewOrderOnTheProfile()
    {
        await UseFormFactorAsync("desktop");
        _collections =
        [
            Playlist(RoadTrip, "Road trip", created: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            Playlist(Focus, "Focus", created: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
            Playlist(Jazz, "Sunday jazz", created: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        ];
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.Section, "playlists"));
        page.WaitForElement(".listen-rail__playlist-row");
        Assert.Equal([RoadTrip, Focus, Jazz], page.FindAll("[data-playlist-reorder-row]").Select(row => Guid.Parse(row.GetAttribute("data-playlist-reorder-row")!)));

        await page.InvokeAsync(() => page.Instance.TouchReorderPlaylistAsync(RoadTrip.ToString("D"), Jazz.ToString("D"), true));

        var saved = Assert.Single(_savedNavigationConfigs);
        Assert.Equal([Focus, Jazz, RoadTrip], ListenPlaylistOrderState.Read(saved));
        page.WaitForAssertion(() => Assert.Equal([Focus, Jazz, RoadTrip], page.FindAll("[data-playlist-reorder-row]").Select(row => Guid.Parse(row.GetAttribute("data-playlist-reorder-row")!))));
    }

    [Fact]
    public async Task AFingerDroppingASidebarPlaylistBeforeAnotherSavesItAheadOfIt()
    {
        await UseFormFactorAsync("desktop");
        _collections =
        [
            Playlist(RoadTrip, "Road trip", created: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            Playlist(Focus, "Focus", created: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
            Playlist(Jazz, "Sunday jazz", created: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        ];
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.Section, "playlists"));
        page.WaitForElement(".listen-rail__playlist-row");

        await page.InvokeAsync(() => page.Instance.TouchReorderPlaylistAsync(Jazz.ToString("D"), RoadTrip.ToString("D"), false));

        Assert.Equal([Jazz, RoadTrip, Focus], ListenPlaylistOrderState.Read(Assert.Single(_savedNavigationConfigs)));
    }

    [Fact]
    public async Task ADropNamingAPlaylistThatIsNotInTheSidebarSavesNothing()
    {
        await UseFormFactorAsync("desktop");
        _collections = [Playlist(RoadTrip, "Road trip"), Playlist(Focus, "Focus")];
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.Section, "playlists"));
        page.WaitForElement(".listen-rail__playlist-row");

        await page.InvokeAsync(() => page.Instance.TouchReorderPlaylistAsync(RoadTrip.ToString("D"), Guid.NewGuid().ToString("D"), true));
        await page.InvokeAsync(() => page.Instance.TouchReorderPlaylistAsync("not an id", Focus.ToString("D"), false));

        Assert.Empty(_savedNavigationConfigs);
    }

    [Fact]
    public async Task EachSidebarPlaylistHasAGripAFingerCanDragAndTheSidebarIsHandedToTheScript()
    {
        await UseFormFactorAsync("desktop");
        _collections = [Playlist(RoadTrip, "Road trip"), Playlist(Focus, "Focus")];
        var module = JSInterop.SetupModule("./js/playlist-reorder.js");
        module.Mode = JSRuntimeMode.Loose;
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.Section, "playlists"));
        page.WaitForElement(".listen-rail__playlist-row");

        Assert.Equal(2, page.FindAll(".listen-rail__playlist-row [data-playlist-reorder-handle]").Count);
        page.WaitForAssertion(() => Assert.Single(module.Invocations["attach"]));
    }

    [Fact]
    public async Task TheSidebarMenuOffersMoveUpAndMoveDownAndSavesTheOrder()
    {
        await UseFormFactorAsync("desktop");
        _collections =
        [
            Playlist(RoadTrip, "Road trip", created: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            Playlist(Focus, "Focus", created: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
        ];
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.Section, "playlists"));
        page.WaitForElement(".listen-rail__playlist-row");

        await page.FindAll(".listen-rail__playlist-row .app-row-menu__trigger")[0].ClickAsync(new());
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.app-menu-item")));
        Assert.True(page.FindAll("button.app-menu-item").Single(item => item.TextContent.Trim() == "Move up").HasAttribute("disabled"));
        await page.FindAll("button.app-menu-item").Single(item => item.TextContent.Trim() == "Move down").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Equal([Focus, RoadTrip], ListenPlaylistOrderState.Read(Assert.Single(_savedNavigationConfigs))));
    }

    private sealed class DefaultPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(ActiveId));

        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }
}
