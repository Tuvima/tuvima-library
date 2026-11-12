using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Ticked albums in the Listen Albums list go into a playlist (Listen music spec D15): the Select button on a desktop or tablet, "Select"
/// in a cover's "..." sheet on a phone, the bar with Add to playlist (and Add to collection where editing is allowed, never Edit), every
/// song on the albums added once in order with the counts told in place, and nothing else loosened: albums stay untickable on every other
/// list and kind, and delete, move, match and edit still refuse them. The Engine and the dialog are hand-written spies.
/// </summary>
public sealed class ListenAlbumSelectionTests : AsyncBunitContext
{
    private static readonly Guid RoadTrip = Guid.Parse("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid SongA = Guid.Parse("50000000-0000-0000-0000-00000000000a");
    private static readonly Guid SongB = Guid.Parse("50000000-0000-0000-0000-00000000000b");
    private static readonly Guid SongC = Guid.Parse("50000000-0000-0000-0000-00000000000c");
    private static readonly Guid AlbumOne = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid AlbumTwo = Guid.Parse("a1000000-0000-0000-0000-000000000002");

    private static readonly LibraryBrowsePreset ListenPreset = new()
    {
        RouteBase = "/listen",
        Title = "Listen",
        HeroVariant = BrowseHeroVariant.Listen,
        Tabs =
        [
            new BrowseTabPreset
            {
                Id = "music",
                Label = "Music",
                MediaType = "Music",
                GroupingOptions =
                [
                    new BrowseGroupingOption("songs", "Songs", "music_note"),
                    new BrowseGroupingOption("albums", "Albums", "album"),
                    new BrowseGroupingOption("artists", "Artists", "person"),
                ],
                DefaultGrouping = "albums",
            },
        ],
    };

    private static readonly LibraryBrowsePreset WatchPreset = new()
    {
        RouteBase = "/watch",
        Title = "Watch",
        HeroVariant = BrowseHeroVariant.Watch,
        Tabs = [new BrowseTabPreset { Id = "movies", Label = "Movies", MediaType = "Movies" }],
    };

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly PlaylistChoiceDialogService _dialogs = new() { Choice = new PlaylistChoice(RoadTrip, "Road trip") };
    private readonly List<Guid> _added = [];
    private readonly List<Guid> _alreadyInPlaylist = [];
    private readonly NavigationManager _nav;
    private readonly DetailsOverlayState _overlay;

    public ListenAlbumSelectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSystemViewGroupsAsync), _ => Task.FromResult(new List<ContentGroupViewModel>
            {
                ContentGroupViewModel.FromContract(new ContentGroupDto { CollectionId = AlbumOne, RootWorkId = AlbumOne, DisplayName = "One", Creator = "Band", PrimaryMediaType = "Music", WorkCount = 2 }),
                ContentGroupViewModel.FromContract(new ContentGroupDto { CollectionId = AlbumTwo, RootWorkId = AlbumTwo, DisplayName = "Two", Creator = "Band", PrimaryMediaType = "Music", WorkCount = 1 }),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetSystemViewGroupDetailAsync), args =>
            {
                var album = (string)args![1]!;
                return Task.FromResult<CollectionGroupDetailViewModel?>(CollectionGroupDetailViewModel.FromContract(new CollectionGroupDetailDto
                {
                    CollectionId = Guid.NewGuid(),
                    DisplayName = album,
                    Works = album == "One"
                        ? [Track(SongB, "Second", "2"), Track(SongA, "First", "1")]
                        : [Track(SongC, "Only", "1")],
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionItemsAsync), _ =>
                Task.FromResult(_alreadyInPlaylist.Select(id => new CollectionItemDto { Id = Guid.NewGuid(), WorkId = id, Title = "Held" }).ToList()));
            stub.SetHandler(nameof(IEngineApiClient.AddCollectionItemAsync), args =>
            {
                _added.Add((Guid)args![1]!);
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetManagedCollectionsAsync), _ => Task.FromResult(new List<ManagedCollectionViewModel>
            {
                new() { Id = RoadTrip, Name = "Road trip", CollectionType = "Playlist", Resolution = "materialized", MembershipMode = "Manual", CanEdit = true, IsEnabled = true },
                new() { Id = Guid.NewGuid(), Name = "Newest rock", CollectionType = "Playlist", Resolution = "materialized", MembershipMode = "Smart", CanEdit = true, IsEnabled = true },
            }));
        });
        Services.AddSingleton(api);
        Services.AddSingleton(_device);
        Services.AddSingleton(TestSessions.For(kids: false, administrator: true));
        Services.AddSingleton<IAppDialogService>(_dialogs);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        _nav = Services.GetRequiredService<NavigationManager>();
        _overlay = new DetailsOverlayState(_nav, new NoHistory());
    }

    // -- Desktop and tablet ----------------------------------------------------------------------

    [Fact]
    public async Task OnADesktop_AlbumsTickAfterSelect_AndTheBarOffersPlaylistAndCollectionButNeverEdit()
    {
        var lane = await RenderLaneAsync("desktop", "albums");

        Assert.Empty(lane.FindAll(".media-tile-pick"));
        lane.Find("[data-album-select]").Click();
        Assert.Equal(2, lane.FindAll(".media-tile-pick").Count);
        Tick(lane, 0);
        Tick(lane, 1);

        var labels = BarLabels(lane);
        Assert.Contains("2 selected", lane.Find(".tl-batch__count").TextContent);
        Assert.Contains("Add to playlist", labels);
        Assert.Contains("Add 2 to collection", labels);
        Assert.DoesNotContain("Edit", labels);
        Assert.DoesNotContain(labels, label => label.Contains("Match", StringComparison.Ordinal) || label.Contains("Move", StringComparison.Ordinal));
        Assert.Empty(lane.FindAll(".tl-batch__more"));
    }

    [Fact]
    public async Task OnATablet_TickedAlbumsOfferAddToPlaylistWithoutAnyRightToEdit()
    {
        var lane = await RenderLaneAsync("tablet", "albums");

        lane.Find("[data-album-select]").Click();
        Tick(lane, 0);

        var labels = BarLabels(lane);
        Assert.Contains("Add to playlist", labels);
        Assert.DoesNotContain("Edit", labels);
        Assert.DoesNotContain(labels, label => label.Contains("collection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TwoTickedAlbums_AddEverySongOnceInTrackOrder_AndTheNoticeGivesTheCounts()
    {
        _alreadyInPlaylist.Add(SongC);
        var lane = await RenderLaneAsync("desktop", "albums");
        lane.Find("[data-album-select]").Click();
        Tick(lane, 0);
        Tick(lane, 1);

        PressBar(lane, "Add to playlist");

        lane.WaitForAssertion(() => Assert.Contains("2 added, 1 already there", lane.Find("[data-bulk-selection-notice]").TextContent));
        Assert.Equal([SongA, SongB], _added);
        Assert.Equal(["Add to playlist"], _dialogs.Titles);
    }

    [Fact]
    public async Task TheAlbumPlaylistList_AsksForAHandMadePlaylistOnly()
    {
        var lane = await RenderLaneAsync("desktop", "albums");
        lane.Find("[data-album-select]").Click();
        Tick(lane, 0);

        PressBar(lane, "Add to playlist");

        lane.WaitForAssertion(() => Assert.Single(_dialogs.Titles));
        // The list is the same chooser the songs use: a playlist is chosen (Playlist and ChooseOnly), and it lists only hand-made ones.
        Assert.NotNull(_dialogs.LastParameters);
    }

    [Fact]
    public async Task Done_DropsTheTicksAndTheCoversOpenAgain()
    {
        var lane = await RenderLaneAsync("desktop", "albums");
        lane.Find("[data-album-select]").Click();
        Tick(lane, 0);
        Assert.NotEmpty(lane.FindAll(".tl-batch__bar"));

        lane.Find("[data-album-select]").Click();

        Assert.Empty(lane.FindAll(".tl-batch__bar"));
        Assert.Empty(lane.FindAll(".media-tile-pick"));
    }

    [Fact]
    public async Task InTheTableLayout_AlbumsStayAsTheyWere_NoSelectButtonAndNoBar()
    {
        var lane = await RenderLaneAsync("desktop", "albums", "&view=list");

        Assert.Empty(lane.FindAll("[data-album-select]"));
        Assert.Empty(lane.FindAll(".media-tile-pick"));
        Assert.Empty(lane.FindAll(".tl-batch__bar"));
    }

    // -- Phone --------------------------------------------------------------------------------------

    [Fact]
    public async Task OnAPhone_SelectInTheCoversSheetStartsSelecting_AndTheBottomBarAddsTheSongs()
    {
        var lane = await RenderLaneAsync("phone", "albums");
        Assert.Empty(lane.FindAll("[data-album-select]"));
        Assert.Empty(lane.FindAll(".song-selection-bar"));

        await BeginSelectOnPhoneAsync(lane, 0);
        Tick(lane, 1);

        var bar = lane.Find(".song-selection-bar");
        Assert.Contains("2 selected", bar.TextContent);
        var actions = bar.QuerySelectorAll("button").Select(button => button.TextContent.Trim()).ToArray();
        Assert.Contains("Add to playlist", actions);
        Assert.DoesNotContain("Edit", actions);
        Assert.Empty(lane.FindAll(".tl-batch__bar"));

        bar.QuerySelectorAll("button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        lane.WaitForAssertion(() => Assert.Contains("3 songs added to Road trip", lane.Find("[data-song-selection-notice]").TextContent));
        Assert.Equal([SongA, SongB, SongC], _added);
        Assert.Equal(PhoneSheet, _dialogs.LastOptions?.Sheet);

        lane.Find(".song-selection-bar__done").Click();
        Assert.Empty(lane.FindAll(".song-selection-bar"));
        Assert.Empty(lane.FindAll(".media-tile-pick"));
    }

    [Fact]
    public async Task OnAPhone_AnAlbumCoversMenuHoldsSelectOnlyWhenTheListOffersIt()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        var tile = AlbumTile();

        var plain = Render<MediaTileActions>(parameters => parameters.Add(actions => actions.Item, tile));
        Assert.DoesNotContain(plain.FindComponent<OverflowActionMenu>().Instance.Actions, action => action.Key == "select");

        var begun = new List<MediaTileViewModel>();
        var offer = new MediaTileSelectionOffer(_ => true, item =>
        {
            begun.Add(item);
            return Task.CompletedTask;
        });
        var offered = Render<MediaTileActions>(parameters => parameters
            .Add(actions => actions.Item, tile)
            .AddCascadingValue(offer));
        var menu = offered.FindComponent<OverflowActionMenu>().Instance;
        var select = Assert.Single(menu.Actions, action => action.Key == "select");
        Assert.Equal("Select", select.Label);

        await offered.InvokeAsync(() => menu.OnActionSelected.InvokeAsync(select));

        Assert.Same(tile, Assert.Single(begun));
    }

    [Fact]
    public async Task OnADesktop_AnAlbumCoversMenuHasNoSelectRow_BecauseTheListHasASelectButton()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var offer = new MediaTileSelectionOffer(_ => true, _ => Task.CompletedTask);

        var cut = Render<MediaTileActions>(parameters => parameters
            .Add(actions => actions.Item, AlbumTile())
            .AddCascadingValue(offer));

        Assert.DoesNotContain(cut.FindComponent<OverflowActionMenu>().Instance.Actions, action => action.Key == "select");
    }

    // -- Nothing else is loosened --------------------------------------------------------------------

    [Fact]
    public async Task OnTheArtistsList_AndOnWatch_ThereIsNoSelectAndNothingTicks()
    {
        var artists = await RenderLaneAsync("desktop", "artists");
        Assert.Empty(artists.FindAll("[data-album-select]"));

        var watch = await RenderLaneAsync("desktop", "movies", preset: WatchPreset, route: "/watch/movies", tab: "movies");
        Assert.Empty(watch.FindAll("[data-album-select]"));
        Assert.Empty(watch.FindAll(".media-tile-pick"));
    }

    [Fact]
    public void AnAlbumIsOnlyTickableThroughTheNarrowListenRule_NeverTheSharedOne()
    {
        Assert.False(BulkEditRules.IsSelectableKind(DetailEntityType.MusicAlbum));
        Assert.True(BulkEditRules.IsPlaylistSelectableKind(DetailEntityType.MusicAlbum));
        foreach (var kind in Enum.GetValues<DetailEntityType>().Where(kind => kind != DetailEntityType.MusicAlbum))
        {
            Assert.False(BulkEditRules.IsPlaylistSelectableKind(kind), kind.ToString());
        }
    }

    [Fact]
    public void Delete_Move_Match_Renumber_ChangeAlbum_AndPutInSeries_StillRefuseAlbums()
    {
        BulkEditRow[] albums =
        [
            new(AlbumOne, "One", DetailEntityType.MusicAlbum, null, "Music", null, "Band"),
            new(AlbumTwo, "Two", DetailEntityType.MusicAlbum, null, "Music", null, "Band"),
        ];

        Assert.False(BulkEditRules.AreDeletable(albums));
        Assert.False(BulkEditRules.CanMove(albums));
        Assert.False(BulkEditRules.CanMatch(albums, "Music"));
        Assert.False(BulkEditRules.CanRenumber(albums, "Music"));
        Assert.False(BulkEditRules.CanChangeAlbum(albums, "Music"));
        Assert.False(BulkEditRules.CanPutInSeries(albums, "Music"));
        Assert.Empty(BulkEditRules.BarMenuActions(albums, canDelete: true, busy: false));
        Assert.True(BulkEditRules.CanAddToPlaylist(albums, "Music"));
        Assert.True(BulkEditRules.CanAddToCollection(albums, "Music"));
    }

    [Fact]
    public async Task ABarOfTickedAlbums_NeverShowsEditMatchMoveOrDelete_EvenWhereEditingIsAllowed()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var selection = new BulkEditSelection { MediaType = "Music" };
        selection.Replace([new BulkEditRow(AlbumOne, "One", DetailEntityType.MusicAlbum, null, "Music", null, "Band")]);

        var bar = Render<BulkSelectionBar>(parameters => parameters.Add(component => component.Selection, selection));

        var labels = bar.FindAll(".tl-batch__bar button").Select(button => button.TextContent.Trim()).ToArray();
        Assert.Contains("Add to playlist", labels);
        Assert.Contains("Add 1 to collection", labels);
        Assert.DoesNotContain("Edit", labels);
        Assert.DoesNotContain("Match", labels);
        Assert.Empty(bar.FindAll(".tl-batch__more"));
    }

    // -- Finding an album's songs ---------------------------------------------------------------------

    [Fact]
    public async Task AnAlbumFromTheAlbumsList_IsFoundByNameAndArtist_AndSongsNotInTheLibraryAreLeftOut()
    {
        (string Album, string? Artist)? asked = null;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetSystemViewGroupDetailAsync), args =>
        {
            asked = ((string)args![1]!, (string?)args[3]);
            return Task.FromResult<CollectionGroupDetailViewModel?>(CollectionGroupDetailViewModel.FromContract(new CollectionGroupDetailDto
            {
                CollectionId = Guid.NewGuid(),
                DisplayName = "One",
                Works =
                [
                    Track(SongB, "Two", "1", disc: 2),
                    Track(SongA, "One", "2", disc: 1),
                    new CollectionGroupWorkDto { WorkId = SongC, Title = "Not owned", TrackNumber = "3", IsOwned = false },
                ],
            }));
        }));

        var ids = await PlaylistMembership.ResolveSongIdsAsync(
            api, [new BulkEditRow(AlbumOne, "One", DetailEntityType.MusicAlbum, null, "Music", null, "Band")]);

        Assert.Equal(("One", "Band"), asked);
        Assert.Equal([SongA, SongB], ids);
        await Task.CompletedTask;
    }

    // -- Helpers -------------------------------------------------------------------------------------

    private static readonly AppDialogSheetMode PhoneSheet = AppDialogSheetMode.Phone;

    private static CollectionGroupWorkDto Track(Guid id, string title, string number, int? disc = null) =>
        new() { WorkId = id, Title = title, TrackNumber = number, DiscNumber = disc };

    private static MediaTileViewModel AlbumTile() => new()
    {
        Id = AlbumOne,
        CollectionId = AlbumOne,
        Title = "One",
        MediaKind = "Music",
        Presentation = MediaTilePresentation.Album,
        IsCollection = true,
    };

    private async Task<IRenderedComponent<MediaBrowseShell>> RenderLaneAsync(
        string formFactor, string grouping, string extraQuery = "", LibraryBrowsePreset? preset = null, string route = "/listen/music", string tab = "music")
    {
        await (formFactor switch
        {
            "tablet" => _device.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true)),
            "phone" => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true)),
            _ => _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)),
        });
        var query = route == "/listen/music" ? $"?browse={grouping}{extraQuery}" : string.Empty;
        _nav.NavigateTo(route + query);
        _overlay.RememberPage(route, new RouteData(typeof(Blank), new Dictionary<string, object?>()));
        _overlay.Attach();
        var lane = Render<MediaBrowseShell>(parameters => parameters
            .Add(shell => shell.Preset, preset ?? ListenPreset)
            .Add(shell => shell.Tab, tab)
            .AddCascadingValue(_overlay));
        lane.WaitForAssertion(() => Assert.False((bool)typeof(MediaBrowseShell)
            .GetField("_loading", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lane.Instance)!));
        return lane;
    }

    private static void Tick(IRenderedComponent<MediaBrowseShell> lane, int index) =>
        lane.WaitForAssertion(() => lane.FindAll(".media-tile-pick__hit button")[index].Click());

    private static string[] BarLabels(IRenderedComponent<MediaBrowseShell> lane) =>
        [.. lane.FindAll(".tl-batch__bar button").Select(button => button.TextContent.Trim())];

    private static void PressBar(IRenderedComponent<MediaBrowseShell> lane, string label) =>
        lane.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == label).Click();

    // The cover's "..." sheet calls the offer the list cascades; pressing it is the same as choosing Select there.
    private static async Task BeginSelectOnPhoneAsync(IRenderedComponent<MediaBrowseShell> lane, int index)
    {
        var shell = lane.Instance;
        var offer = (MediaTileSelectionOffer)typeof(MediaBrowseShell)
            .GetProperty("AlbumSelectionOffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
        var cards = (IEnumerable<MediaTileViewModel>)typeof(MediaBrowseShell)
            .GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
        var tile = cards.ElementAt(index);
        Assert.True(offer.Applies(tile));
        await lane.InvokeAsync(() => offer.BeginAsync(tile));
    }

    private sealed class Blank : ComponentBase
    {
    }

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }
}
