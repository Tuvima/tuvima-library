using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.MediaHub;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Dragging songs onto a playlist (Listen music spec D10): a ticked song carries every ticked song and an unticked one just itself; a
/// drop on a hand-made playlist adds once, skips songs already there and says how many went in; rule-based and system playlists take
/// nothing; New playlist creates and then adds; reordering inside a playlist is not disturbed; and the desktop row menu offers Move up and
/// Move down in a list's own order. The Engine is a hand-written spy.
/// </summary>
public sealed class SongDragToPlaylistTests : AsyncBunitContext
{
    private static readonly Guid RoadTrip = Guid.Parse("b1000000-0000-0000-0000-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(Guid Playlist, Guid Work)> _added = [];
    private readonly List<(string Name, IReadOnlyList<Guid> Works)> _created = [];
    private readonly List<Guid> _alreadyInPlaylist = [];

    public SongDragToPlaylistTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionItemsAsync), _ =>
                Task.FromResult(_alreadyInPlaylist.Select(id => new CollectionItemDto { Id = Guid.NewGuid(), WorkId = id, Title = "Held" }).ToList()));
            stub.SetHandler(nameof(IEngineApiClient.AddCollectionItemAsync), args =>
            {
                _added.Add(((Guid)args![0]!, (Guid)args[1]!));
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.CreateCollectionWithItemsAsync), args =>
            {
                _created.Add(((string)args![0]!, (IReadOnlyList<Guid>)args[8]!));
                return Task.FromResult<Guid?>(Guid.NewGuid());
            });
        }));
        Services.AddScoped<SongDragDropService>();
    }

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private static SongRow Song(string title, bool playlist = false) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Source = playlist ? SongRowSource.PlaylistItem : SongRowSource.Work,
        Title = title,
        Artist = "The Artist",
        Album = "The Album",
        WorkId = Guid.NewGuid(),
        PlaylistItemId = playlist ? Guid.NewGuid() : null,
        DateAdded = new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero),
        DurationSeconds = 180,
    };

    private static List<SongRow> Songs() => [Song("Alpha"), Song("Bravo"), Song("Charlie"), Song("Delta")];

    private static List<SongRow> PlaylistSongs() => [Song("Alpha", true), Song("Bravo", true), Song("Charlie", true)];

    private static IElement RowElement(IRenderedComponent<SongTable> cut, SongRow row) => cut.Find($"[data-song-row-id='{row.Id}']");

    private static EventCallback<T> Cb<T>(Action<T> handler) => new(null, handler);

    private IRenderedComponent<SongTable> RenderSongs(
        IReadOnlyList<SongRow> rows,
        List<IReadOnlyList<SongRow>> carried,
        IReadOnlySet<string>? ticked = null) =>
        Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Songs)
            .Add(table => table.Rows, rows)
            .Add(table => table.ShowSelection, true)
            .Add(table => table.SelectedIds, ticked ?? new HashSet<string>())
            .Add(table => table.OnDragOutStarted, Cb<IReadOnlyList<SongRow>>(carried.Add)));

    // The rows in the order the table draws them (the default sort is not necessarily the order the rows were given in).
    private static List<string> DrawnIds(IRenderedComponent<SongTable> cut) =>
        [.. cut.FindAll(".song-table__row[data-song-row-id]").Select(row => row.GetAttribute("data-song-row-id")!)];

    // -- What a drag carries ------------------------------------------------------------------

    [Fact]
    public async Task DraggingATickedSongCarriesEveryTickedSongInTheOrderTheListShowsThem()
    {
        await UseDesktopAsync();
        var rows = Songs();
        var carried = new List<IReadOnlyList<SongRow>>();
        var cut = RenderSongs(rows, carried, new HashSet<string> { rows[2].Id, rows[0].Id });
        var drawn = DrawnIds(cut);

        RowElement(cut, rows[2]).DragStart();

        var songs = Assert.Single(carried);
        Assert.Equal(drawn.Where(id => id == rows[0].Id || id == rows[2].Id), songs.Select(song => song.Id));
        Assert.Equal(2, songs.Count);
        Assert.True(Services.GetRequiredService<SongDragDropService>().IsCarrying);
    }

    [Fact]
    public async Task DraggingAnUntickedSongCarriesOnlyThatSongEvenWhenOthersAreTicked()
    {
        await UseDesktopAsync();
        var rows = Songs();
        var carried = new List<IReadOnlyList<SongRow>>();
        var cut = RenderSongs(rows, carried, new HashSet<string> { rows[0].Id, rows[1].Id });

        RowElement(cut, rows[3]).DragStart();

        Assert.Equal([rows[3]], Assert.Single(carried));
    }

    [Fact]
    public async Task TheTableTellsTheBrowserHowManySongsATickedDragCarriesAndWhichRowsAreTicked()
    {
        await UseDesktopAsync();
        var rows = Songs();
        var cut = RenderSongs(rows, [], new HashSet<string> { rows[0].Id, rows[1].Id, rows[2].Id });

        var root = cut.Find(".song-table");
        Assert.Equal("true", root.GetAttribute("data-song-drag-out"));
        Assert.Equal("3 songs", root.GetAttribute("data-song-drag-label"));
        Assert.Equal("true", RowElement(cut, rows[0]).GetAttribute("data-song-ticked"));
        Assert.Null(RowElement(cut, rows[3]).GetAttribute("data-song-ticked"));
    }

    [Fact]
    public async Task WithOneOrNoTickedSongTheDragPictureIsTheBrowsersOwn()
    {
        await UseDesktopAsync();
        var rows = Songs();
        var cut = RenderSongs(rows, [], new HashSet<string> { rows[0].Id });

        Assert.Null(cut.Find(".song-table").GetAttribute("data-song-drag-label"));
    }

    [Fact]
    public async Task OnATouchScreenNothingIsDraggable()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true, FinePointer: false, Hover: false));
        var cut = RenderSongs(Songs(), []);

        Assert.Empty(cut.FindAll("[draggable]"));
        Assert.Null(cut.Find(".song-table").GetAttribute("data-song-drag-out"));
    }

    [Fact]
    public async Task WhenTheDragEndsTheTableStopsCarryingAndTellsTheHost()
    {
        await UseDesktopAsync();
        var rows = Songs();
        var ended = 0;
        var cut = Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Songs)
            .Add(table => table.Rows, rows)
            .Add(table => table.OnDragOutEnded, EventCallback.Factory.Create(this, () => ended++)));
        var service = Services.GetRequiredService<SongDragDropService>();

        RowElement(cut, rows[0]).DragStart();
        Assert.True(service.IsCarrying);
        RowElement(cut, rows[0]).DragEnd();

        Assert.False(service.IsCarrying);
        Assert.Equal(1, ended);
    }

    // -- Dropping on a hand-made playlist -----------------------------------------------------

    [Fact]
    public async Task DroppingTickedSongsOnAPlaylistAddsEachOnceAndTheTableSaysHowManyWentIn()
    {
        await UseDesktopAsync();
        var rows = Songs();
        var cut = RenderSongs(rows, [], new HashSet<string> { rows[0].Id, rows[1].Id, rows[2].Id });
        var service = Services.GetRequiredService<SongDragDropService>();
        RowElement(cut, rows[1]).DragStart();

        var result = await cut.InvokeAsync(() => service.DropOnPlaylistAsync(RoadTrip, "Road trip"));

        Assert.Equal(3, result!.Added);
        Assert.Equal(3, _added.Count);
        Assert.All(_added, entry => Assert.Equal(RoadTrip, entry.Playlist));
        cut.WaitForAssertion(() => Assert.Equal("3 songs added to Road trip", cut.Find("[data-song-table-notice]").TextContent.Trim()));

        // The songs were handed over once: a second drop on the same drag adds nothing.
        Assert.Null(await cut.InvokeAsync(() => service.DropOnPlaylistAsync(RoadTrip, "Road trip")));
        Assert.Equal(3, _added.Count);
    }

    [Fact]
    public async Task DroppingSongsAlreadyInThePlaylistSkipsThemAndSaysSo()
    {
        await UseDesktopAsync();
        var rows = Songs();
        _alreadyInPlaylist.Add(rows[1].WorkId!.Value);
        var cut = RenderSongs(rows, [], new HashSet<string> { rows[0].Id, rows[1].Id, rows[2].Id });
        var service = Services.GetRequiredService<SongDragDropService>();
        RowElement(cut, rows[0]).DragStart();

        await cut.InvokeAsync(() => service.DropOnPlaylistAsync(RoadTrip, "Road trip"));

        Assert.DoesNotContain(_added, entry => entry.Work == rows[1].WorkId);
        Assert.Equal(2, _added.Count);
        cut.WaitForAssertion(() => Assert.Equal("2 added, 1 already there", cut.Find("[data-song-table-notice]").TextContent.Trim()));
    }

    [Fact]
    public async Task OnlyTheTableTheSongsCameFromShowsTheSentence()
    {
        await UseDesktopAsync();
        var first = Songs();
        var second = Songs();
        var source = RenderSongs(first, []);
        var other = RenderSongs(second, []);
        var service = Services.GetRequiredService<SongDragDropService>();
        RowElement(source, first[0]).DragStart();

        await source.InvokeAsync(() => service.DropOnPlaylistAsync(RoadTrip, "Road trip"));

        source.WaitForAssertion(() => Assert.Equal("1 song added to Road trip", source.Find("[data-song-table-notice]").TextContent.Trim()));
        Assert.Empty(other.FindAll("[data-song-table-notice]"));
    }

    [Fact]
    public async Task ADropWithNothingBeingCarriedDoesNothing()
    {
        var service = Services.GetRequiredService<SongDragDropService>();

        Assert.Null(await service.DropOnPlaylistAsync(RoadTrip, "Road trip"));
        Assert.Empty(_added);
    }

    // -- Playlists that take no drops ---------------------------------------------------------

    [Fact]
    public async Task ARuleBasedPlaylistTileTakesNoDropsAndShowsNoDropTarget()
    {
        await UseDesktopAsync();
        var rows = Songs();
        var table = RenderSongs(rows, []);
        RowElement(table, rows[0]).DragStart();
        var tile = MediaTileComposerService.FromPlaylist(Playlist("Newest rock", membershipMode: "Smart"));

        var cut = Render<MediaSquareTile>(parameters => parameters.Add(component => component.Item, tile));
        cut.Find("article").TriggerEvent("ondrop", new Microsoft.AspNetCore.Components.Web.DragEventArgs());

        Assert.False(tile.AcceptsSongDrops);
        Assert.Null(cut.Find("article").GetAttribute("data-song-drop"));
        Assert.Empty(_added);
        Assert.True(Services.GetRequiredService<SongDragDropService>().IsCarrying);
    }

    [Theory]
    [InlineData("System", "Manual", true)]
    [InlineData("Playlist", "Manual", false)]
    public void ASystemPlaylistOrOneThePersonCannotEditIsNotADropTarget(string type, string mode, bool canEdit)
    {
        var tile = MediaTileComposerService.FromPlaylist(Playlist("Not for drops", type, mode, canEdit));

        Assert.False(tile.AcceptsSongDrops);
    }

    [Fact]
    public async Task AHandMadePlaylistTileIsADropTargetAndAddsTheDraggedSongs()
    {
        await UseDesktopAsync();
        var rows = Songs();
        var table = RenderSongs(rows, [], new HashSet<string> { rows[0].Id, rows[1].Id });
        RowElement(table, rows[0]).DragStart();
        var tile = MediaTileComposerService.FromPlaylist(Playlist("Road trip"));

        var cut = Render<MediaSquareTile>(parameters => parameters.Add(component => component.Item, tile));
        Assert.Equal("playlist", cut.Find("article").GetAttribute("data-song-drop"));
        cut.Find("article").TriggerEvent("ondrop", new Microsoft.AspNetCore.Components.Web.DragEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(2, _added.Count));
        Assert.All(_added, entry => Assert.Equal(RoadTrip, entry.Playlist));
        table.WaitForAssertion(() => Assert.Equal("2 songs added to Road trip", table.Find("[data-song-table-notice]").TextContent.Trim()));
    }

    // -- The rail ---------------------------------------------------------------------------------

    [Fact]
    public void OnlyAHandMadePlaylistInTheRailAndNewPlaylistAreMarkedAsDropTargets()
    {
        var navigation = new[]
        {
            new MediaSectionNavigationGroup(
                "Playlists",
                [
                    new("Road trip", "/listen/music/playlists/road-trip", AppMaterialIcons.Outlined.PlaylistPlay, DropTarget: new PlaylistNavigationDropTarget(RoadTrip)),
                    new("Newest rock", "/listen/music/playlists/newest-rock", AppMaterialIcons.Outlined.AutoAwesome),
                ],
                new MediaSectionNavigationCreateOptions(
                    "New Playlist", "New Smart Playlist", "Playlist name", new ManualPlaylistCreateTarget(), new SmartPlaylistCreateTarget(), new NewPlaylistNavigationDropTarget())),
        };

        var cut = Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "Listen")
            .Add(component => component.NavigationGroups, navigation)
            .AddChildContent("<section>Listen content</section>"));

        var targets = cut.FindAll("[data-song-drop]");
        Assert.Equal(2, targets.Count);
        Assert.Equal("new-playlist", targets.Single(target => target.ClassList.Contains("media-section-shell__rail-heading")).GetAttribute("data-song-drop"));
        var playlist = targets.Single(target => target.ClassList.Contains("media-section-shell__rail-item"));
        Assert.Equal("playlist", playlist.GetAttribute("data-song-drop"));
        Assert.Contains("Road trip", playlist.TextContent, StringComparison.Ordinal);
    }

    // -- New playlist -------------------------------------------------------------------------

    [Fact]
    public async Task DroppingOnNewPlaylistKeepsTheSongsUntilANameIsGivenThenCreatesThePlaylistHoldingThem()
    {
        await UseDesktopAsync();
        var rows = Songs();
        var cut = RenderSongs(rows, [], new HashSet<string> { rows[0].Id, rows[3].Id });
        var service = Services.GetRequiredService<SongDragDropService>();
        RowElement(cut, rows[0]).DragStart();

        var staged = service.Take();
        Assert.NotNull(staged);
        Assert.False(service.IsCarrying);
        Assert.Empty(_created);

        var result = await cut.InvokeAsync(() => service.CreatePlaylistAsync("Mix", staged!));

        Assert.True(result.Created);
        var created = Assert.Single(_created);
        Assert.Equal("Mix", created.Name);
        Assert.Equal(2, created.Works.Count);
        cut.WaitForAssertion(() => Assert.Equal("2 songs added to Mix", cut.Find("[data-song-table-notice]").TextContent.Trim()));
    }

    // -- Reordering inside a playlist is untouched --------------------------------------------

    [Fact]
    public async Task InAPlaylistADragThatStaysInTheTableStillReordersAndAlsoCarriesTheSongOut()
    {
        await UseDesktopAsync();
        var rows = PlaylistSongs();
        var moves = new List<SongReorderRequest>();
        var carried = new List<IReadOnlyList<SongRow>>();
        var cut = Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Playlist)
            .Add(table => table.Rows, rows)
            .Add(table => table.CanReorder, true)
            .Add(table => table.OnReorder, Cb<SongReorderRequest>(moves.Add))
            .Add(table => table.OnDragOutStarted, Cb<IReadOnlyList<SongRow>>(carried.Add)));

        RowElement(cut, rows[0]).DragStart();
        RowElement(cut, rows[2]).DragOver();
        Assert.NotEmpty(cut.FindAll(".song-table__drop-line"));
        RowElement(cut, rows[2]).Drop();

        Assert.Equal([new SongReorderRequest(rows[0], 0, 1)], moves);
        Assert.Equal([rows[0]], Assert.Single(carried));
    }

    [Fact]
    public async Task InAPlaylistADragThatLeavesTheTableAddsToThePlaylistDroppedOnAndDoesNotReorder()
    {
        await UseDesktopAsync();
        var rows = PlaylistSongs();
        var moves = new List<SongReorderRequest>();
        var cut = Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Playlist)
            .Add(table => table.Rows, rows)
            .Add(table => table.CanReorder, true)
            .Add(table => table.OnReorder, Cb<SongReorderRequest>(moves.Add)));
        var service = Services.GetRequiredService<SongDragDropService>();

        RowElement(cut, rows[1]).DragStart();
        await cut.InvokeAsync(() => service.DropOnPlaylistAsync(RoadTrip, "Road trip"));
        RowElement(cut, rows[1]).DragEnd();

        Assert.Empty(moves);
        Assert.Equal([(RoadTrip, rows[1].WorkId!.Value)], _added);
        Assert.Empty(cut.FindAll(".song-table__drop-line"));
    }

    // -- Move up and Move down in the desktop row menu ----------------------------------------

    private IRenderedComponent<SongTable> RenderFavorites(IReadOnlyList<SongRow> rows, List<SongReorderRequest> moves, SongSortState? sort = null) =>
        Render<SongTable>(parameters =>
        {
            parameters
                .Add(table => table.Place, SongTablePlace.Playlist)
                .Add(table => table.Rows, rows)
                .Add(table => table.SortableOwnOrder, true)
                .Add(table => table.CanReorder, true)
                .Add(table => table.OnReorder, Cb<SongReorderRequest>(moves.Add));
            if (sort is not null)
            {
                parameters.Add(table => table.Sort, sort);
            }
        });

    private static IReadOnlyList<IElement> OpenMenu(IRenderedComponent<SongTable> cut, SongRow row)
    {
        RowElement(cut, row).QuerySelector("button[aria-haspopup]")!.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        return cut.FindAll("[role='menuitem']");
    }

    [Fact]
    public async Task TheDesktopRowMenuOffersMoveUpAndMoveDownAndTheMoveGoesToTheHost()
    {
        await UseDesktopAsync();
        var rows = PlaylistSongs();
        var moves = new List<SongReorderRequest>();
        var cut = RenderFavorites(rows, moves);

        var items = OpenMenu(cut, rows[1]);
        Assert.Contains(items, item => item.TextContent.Trim() == "Move up");
        items.Single(item => item.TextContent.Trim() == "Move down").Click();

        cut.WaitForAssertion(() => Assert.Equal([new SongReorderRequest(rows[1], 1, 2)], moves));
    }

    [Fact]
    public async Task TheDesktopRowMenuGreysMoveUpOnTheFirstRowAndMoveDownOnTheLast()
    {
        await UseDesktopAsync();
        var rows = PlaylistSongs();
        var cut = RenderFavorites(rows, []);
        var other = RenderFavorites(rows, []);

        var top = OpenMenu(cut, rows[0]);
        Assert.True(top.Single(item => item.TextContent.Trim() == "Move up").HasAttribute("disabled"));
        Assert.False(top.Single(item => item.TextContent.Trim() == "Move down").HasAttribute("disabled"));

        var bottom = OpenMenu(other, rows[2]);
        Assert.False(bottom.Single(item => item.TextContent.Trim() == "Move up").HasAttribute("disabled"));
        Assert.True(bottom.Single(item => item.TextContent.Trim() == "Move down").HasAttribute("disabled"));
    }

    [Fact]
    public async Task WhileAColumnSortsTheListTheDesktopRowMenuOffersNoMove()
    {
        await UseDesktopAsync();
        var rows = PlaylistSongs();
        var cut = RenderFavorites(rows, [], new SongSortState(SongColumn.Song, false));

        var items = OpenMenu(cut, rows[1]).Select(item => item.TextContent.Trim()).ToList();

        Assert.DoesNotContain("Move up", items);
        Assert.DoesNotContain("Move down", items);
    }

    private static ManagedCollectionViewModel Playlist(
        string name,
        string type = "Playlist",
        string membershipMode = "Manual",
        bool canEdit = true) => new()
        {
            Id = RoadTrip,
            Name = name,
            CollectionType = type,
            Resolution = "materialized",
            MembershipMode = membershipMode,
            CanEdit = canEdit,
            IsEnabled = true,
        };
}
