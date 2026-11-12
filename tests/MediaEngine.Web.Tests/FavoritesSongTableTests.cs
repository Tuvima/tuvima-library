using AngleSharp.Dom;
using Bunit;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Favorites on the shared song table (Listen music spec D11 and D14): the list keeps its own order, any column can sort it, and the
/// by-hand controls (drag, Alt+Up and Alt+Down, Move up and Move down) are offered only while it is in its own order.
/// </summary>
public sealed class FavoritesSongTableTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public FavoritesSongTableTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
    }

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private Task UsePhoneAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true, FinePointer: false, Hover: false));

    private static List<SongRow> FavoriteRows() =>
        [.. new[] { ("Zebra", "Ada"), ("Apple", "Zed"), ("Mango", "Bea") }.Select((song, index) =>
        {
            var songId = Guid.NewGuid();
            return new SongRow
            {
                Id = songId.ToString("D"),
                Source = SongRowSource.PlaylistItem,
                Title = song.Item1,
                Artist = song.Item2,
                Album = "The Album",
                Number = index + 1,
                WorkId = songId,
                PlaylistItemId = songId,
                DurationSeconds = 180,
            };
        })];

    private IRenderedComponent<SongTable> RenderFavorites(IReadOnlyList<SongRow> rows, List<SongReorderRequest> moves, List<SongRow>? removed = null) =>
        Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Playlist)
            .Add(table => table.Rows, rows)
            .Add(table => table.CanReorder, true)
            .Add(table => table.SortableOwnOrder, true)
            .Add(table => table.RemoveLabel, "Remove from Favorites")
            .Add(table => table.OnReorder, EventCallback.Factory.Create<SongReorderRequest>(this, moves.Add))
            .Add(table => table.OnRemoveFromPlaylist, EventCallback.Factory.Create<SongRow>(this, row => removed?.Add(row))));

    private static IElement RowElement(IRenderedComponent<SongTable> cut, SongRow row) => cut.Find($"[data-song-row-id='{row.Id}']");

    private static IReadOnlyList<string> Titles(IRenderedComponent<SongTable> cut) =>
        [.. cut.FindAll(".song-table__title").Select(title => title.TextContent.Trim())];

    private static void ClickHeader(IRenderedComponent<SongTable> cut, string label) =>
        cut.FindAll(".song-table__head [role='columnheader'] button").Single(button => button.TextContent.Trim() == label).Click();

    [Fact]
    public async Task Favorites_OpenInTheirOwnOrder_AndEveryTextColumnHeaderCanSortThem()
    {
        await UseDesktopAsync();
        var rows = FavoriteRows();

        var cut = RenderFavorites(rows, []);

        Assert.Equal(["Zebra", "Apple", "Mango"], Titles(cut));
        foreach (var header in new[] { "Song", "Artist", "Album", "Favorite", "Time" })
        {
            Assert.Contains(cut.FindAll(".song-table__head [role='columnheader'] button"), button => button.TextContent.Trim() == header);
        }

        ClickHeader(cut, "Song");
        Assert.Equal(["Apple", "Mango", "Zebra"], Titles(cut));
        ClickHeader(cut, "Artist");
        Assert.Equal(["Zebra", "Mango", "Apple"], Titles(cut));
    }

    [Fact]
    public async Task ClickingTheSortedHeaderTwiceMore_GoesBackToTheOwnOrder()
    {
        await UseDesktopAsync();
        var cut = RenderFavorites(FavoriteRows(), []);

        ClickHeader(cut, "Song");
        ClickHeader(cut, "Song");
        Assert.Equal(["Zebra", "Mango", "Apple"], Titles(cut));
        ClickHeader(cut, "Song");

        Assert.Equal(["Zebra", "Apple", "Mango"], Titles(cut));
        Assert.NotEmpty(cut.FindAll("[draggable='true']"));
    }

    [Fact]
    public async Task InTheirOwnOrder_DraggingARowAndAltArrowBothAskTheHostToMoveIt()
    {
        await UseDesktopAsync();
        var rows = FavoriteRows();
        var moves = new List<SongReorderRequest>();
        var cut = RenderFavorites(rows, moves);

        Assert.Equal(3, cut.FindAll("[draggable='true']").Count);
        RowElement(cut, rows[2]).DragStart();
        RowElement(cut, rows[0]).DragOver();
        RowElement(cut, rows[0]).Drop();
        RowElement(cut, rows[0]).KeyDown(new KeyboardEventArgs { Key = "ArrowDown", AltKey = true });

        Assert.Equal([new SongReorderRequest(rows[2], 2, 0), new SongReorderRequest(rows[0], 0, 1)], moves);
    }

    [Fact]
    public async Task WhileAColumnSortsFavorites_NothingCanBeDraggedOrMovedByKeyboard()
    {
        await UseDesktopAsync();
        var rows = FavoriteRows();
        var moves = new List<SongReorderRequest>();
        var cut = RenderFavorites(rows, moves);

        ClickHeader(cut, "Song");

        Assert.Empty(cut.FindAll("[draggable]"));
        Assert.Empty(cut.FindAll(".song-table__end-zone"));
        RowElement(cut, rows[1]).KeyDown(new KeyboardEventArgs { Key = "ArrowDown", AltKey = true });
        Assert.Empty(moves);
    }

    [Fact]
    public async Task TheRowMenuOffersRemoveFromFavorites_AndRemovingHandsTheRowToTheHostAtOnce()
    {
        await UseDesktopAsync();
        var rows = FavoriteRows();
        var removed = new List<SongRow>();
        var cut = RenderFavorites(rows, [], removed);

        RowElement(cut, rows[1]).QuerySelector(".song-table__cell--actions button[aria-haspopup='menu']")!.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        var items = cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim()).ToList();

        Assert.Contains("Remove from Favorites", items);
        Assert.DoesNotContain("Remove from playlist", items);
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Remove from Favorites").Click();
        cut.WaitForAssertion(() => Assert.Equal([rows[1]], removed));
    }

    [Fact]
    public async Task OnAPhone_TheSheetOffersMoveUpAndMoveDownInTheOwnOrder_AndTheMoveGoesToTheHost()
    {
        await UsePhoneAsync();
        var rows = FavoriteRows();
        var moves = new List<SongReorderRequest>();
        var cut = RenderFavorites(rows, moves);

        RowElement(cut, rows[1]).QuerySelector("button[aria-haspopup]")!.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        var items = cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim()).ToList();

        Assert.Contains("Move up", items);
        Assert.Contains("Move down", items);
        Assert.Contains("Remove from Favorites", items);
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Move up").Click();
        cut.WaitForAssertion(() => Assert.Equal([new SongReorderRequest(rows[1], 1, 0)], moves));
    }

    [Fact]
    public void AHandMadePlaylist_StillCannotBeSortedByAHeader()
    {
        Assert.False(SongTableSort.IsSortable(SongTablePlace.Playlist, SongColumn.Song));
        Assert.True(SongTableSort.IsSortable(SongTablePlace.Playlist, SongColumn.Song, ownOrderSortable: true));
        Assert.False(SongTableSort.IsSortable(SongTablePlace.Playlist, SongColumn.Number, ownOrderSortable: true));
    }

    [Fact]
    public void SortingFavoritesByAColumn_ThenClickingTwiceMore_ReturnsToTheOwnOrder()
    {
        var own = new SongSortState(null, false);

        var first = SongTableSort.Click(SongTablePlace.Playlist, own, SongColumn.Song, ownOrderSortable: true);
        var second = SongTableSort.Click(SongTablePlace.Playlist, first, SongColumn.Song, ownOrderSortable: true);
        var third = SongTableSort.Click(SongTablePlace.Playlist, second, SongColumn.Song, ownOrderSortable: true);
        var favoriteFirst = SongTableSort.Click(SongTablePlace.Playlist, own, SongColumn.Favorite, ownOrderSortable: true);
        var favoriteSecond = SongTableSort.Click(SongTablePlace.Playlist, favoriteFirst, SongColumn.Favorite, ownOrderSortable: true);
        var favoriteThird = SongTableSort.Click(SongTablePlace.Playlist, favoriteSecond, SongColumn.Favorite, ownOrderSortable: true);

        Assert.Equal(new SongSortState(SongColumn.Song, false), first);
        Assert.Equal(new SongSortState(SongColumn.Song, true), second);
        Assert.Equal(own, third);
        Assert.Equal(new SongSortState(SongColumn.Favorite, true), favoriteFirst);
        Assert.Equal(new SongSortState(SongColumn.Favorite, false), favoriteSecond);
        Assert.Equal(own, favoriteThird);
    }
}
