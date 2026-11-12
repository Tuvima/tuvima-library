using AngleSharp.Dom;
using Bunit;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The playlist page on the shared song table (spec music-table-and-reader-frame WP5): reordering by drag and by keyboard saves the
/// same order the old inline table saved, and the old table's markup and selectors are gone from the page.
/// </summary>
public sealed class ListenPlaylistSongTableTests : AsyncBunitContext
{
    private const int ItemCount = 5;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public ListenPlaylistSongTableTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
    }

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private static List<SongRow> PlaylistRows() =>
        [.. Enumerable.Range(1, ItemCount).Select(index => new SongRow
        {
            Id = Guid.NewGuid().ToString("D"),
            Source = SongRowSource.PlaylistItem,
            Title = $"Song {index}",
            Artist = "The Artist",
            Album = "The Album",
            Number = index,
            WorkId = Guid.NewGuid(),
            PlaylistItemId = Guid.NewGuid(),
            DurationSeconds = 180,
        })];

    private IRenderedComponent<SongTable> RenderPlaylist(IReadOnlyList<SongRow> rows, List<SongReorderRequest> moves) =>
        Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Playlist)
            .Add(table => table.Rows, rows)
            .Add(table => table.CanReorder, true)
            .Add(table => table.OnReorder, EventCallback.Factory.Create<SongReorderRequest>(this, moves.Add)));

    private static IElement RowElement(IRenderedComponent<SongTable> cut, SongRow row) => cut.Find($"[data-song-row-id='{row.Id}']");

    // What the old inline playlist table saved when a row was dropped before another row (ListenPage.DropPlaylistTrackReorderAsync).
    private static List<int> OldDropBefore(int source, int target)
    {
        var ordered = Enumerable.Range(0, ItemCount).ToList();
        var moved = ordered[source];
        ordered.RemoveAt(source);
        if (source < target)
        {
            target--;
        }

        ordered.Insert(Math.Clamp(target, 0, ordered.Count), moved);
        return ordered;
    }

    // What it saved when a row was dropped on the empty zone below the last row (DropPlaylistTrackAtEndAsync).
    private static List<int> OldDropAtEnd(int source)
    {
        var ordered = Enumerable.Range(0, ItemCount).ToList();
        var moved = ordered[source];
        ordered.RemoveAt(source);
        ordered.Add(moved);
        return ordered;
    }

    public static TheoryData<int, int> EveryDragAndDropBeforeAnotherRow()
    {
        var cases = new TheoryData<int, int>();
        for (var source = 0; source < ItemCount; source++)
        {
            for (var target = 0; target < ItemCount; target++)
            {
                if (source != target)
                {
                    cases.Add(source, target);
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(EveryDragAndDropBeforeAnotherRow))]
    public async Task DraggingARowBeforeAnotherSavesTheSameOrderTheOldTableSaved(int source, int target)
    {
        await UseDesktopAsync();
        var rows = PlaylistRows();
        var moves = new List<SongReorderRequest>();
        var cut = RenderPlaylist(rows, moves);

        RowElement(cut, rows[source]).DragStart();
        RowElement(cut, rows[target]).DragOver();
        RowElement(cut, rows[target]).Drop();

        var expected = OldDropBefore(source, target);
        if (expected.SequenceEqual(Enumerable.Range(0, ItemCount)))
        {
            // Dropping a row on the row just after it changes nothing; the table reports no move.
            Assert.Empty(moves);
            return;
        }

        var request = Assert.Single(moves);
        var saved = ListenPlaylistOrder.MoveOnto(Enumerable.Range(0, ItemCount).ToList(), request.FromIndex, request.ToIndex);
        Assert.Equal(expected, saved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task DraggingARowToTheEndZoneSavesTheSameOrderTheOldTableSaved(int source)
    {
        await UseDesktopAsync();
        var rows = PlaylistRows();
        var moves = new List<SongReorderRequest>();
        var cut = RenderPlaylist(rows, moves);

        RowElement(cut, rows[source]).DragStart();
        cut.Find(".song-table__end-zone").DragOver();
        cut.Find(".song-table__end-zone").Drop();

        var request = Assert.Single(moves);
        var saved = ListenPlaylistOrder.MoveOnto(Enumerable.Range(0, ItemCount).ToList(), request.FromIndex, request.ToIndex);
        Assert.Equal(OldDropAtEnd(source), saved);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(0, 1)]
    [InlineData(2, -1)]
    [InlineData(2, 1)]
    [InlineData(4, -1)]
    [InlineData(4, 1)]
    public async Task AltArrowSavesTheSameOrderTheOldTableSavedAndStopsAtTheEnds(int source, int delta)
    {
        await UseDesktopAsync();
        var rows = PlaylistRows();
        var moves = new List<SongReorderRequest>();
        var cut = RenderPlaylist(rows, moves);

        RowElement(cut, rows[source]).KeyDown(new KeyboardEventArgs { Key = delta < 0 ? "ArrowUp" : "ArrowDown", AltKey = true });

        // The old table moved by ListenPlaylistOrder.Move(items, index, delta), which returns nothing at either end.
        var expected = ListenPlaylistOrder.Move(Enumerable.Range(0, ItemCount).ToList(), source, delta);
        if (expected is null)
        {
            Assert.Empty(moves);
            return;
        }

        var request = Assert.Single(moves);
        Assert.Equal(expected, ListenPlaylistOrder.MoveOnto(Enumerable.Range(0, ItemCount).ToList(), request.FromIndex, request.ToIndex));
    }

    [Fact]
    public async Task AfterAnAltArrowMoveFocusReturnsToTheMovedRow()
    {
        await UseDesktopAsync();
        var rows = PlaylistRows();
        var cut = RenderPlaylist(rows, []);

        RowElement(cut, rows[1]).KeyDown(new KeyboardEventArgs { Key = "ArrowDown", AltKey = true });

        var focus = JSInterop.VerifyInvoke("tuvimaFocusById");
        Assert.Equal($"song-table-row-{rows[1].PlaylistItemId:D}", focus.Arguments[0]);
        Assert.Equal(focus.Arguments[0], RowElement(cut, rows[1]).Id);
    }

    [Fact]
    public async Task AReadOnlyPlaylistCanStillCarryASongOutButHasNoReorderZone()
    {
        await UseDesktopAsync();
        var rows = PlaylistRows();
        var carried = new List<IReadOnlyList<SongRow>>();

        var cut = Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Playlist)
            .Add(table => table.Rows, rows)
            .Add(table => table.OnDragOutStarted, EventCallback.Factory.Create<IReadOnlyList<SongRow>>(this, carried.Add)));

        Assert.Equal(ItemCount, cut.FindAll("[draggable]").Count);
        Assert.Empty(cut.FindAll(".song-table__end-zone"));
        RowElement(cut, rows[0]).DragStart();
        Assert.Equal([rows[0]], Assert.Single(carried));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 0)]
    [InlineData(2, 4)]
    public void MoveOntoPutsTheRowWhereTheTargetRowWasAndShiftsTheRowsBetween(int source, int target)
    {
        var items = new[] { "a", "b", "c", "d", "e" };

        var result = ListenPlaylistOrder.MoveOnto(items, source, target);

        Assert.NotNull(result);
        Assert.Equal(items[source], result[target]);
        Assert.Equal(items.OrderBy(x => x), result.OrderBy(x => x));
    }

    [Fact]
    public void MoveOntoDoesNothingWhenTheRowIsAlreadyThereOrAnIndexIsOutsideTheList()
    {
        Assert.Null(ListenPlaylistOrder.MoveOnto(["a", "b"], 1, 1));
        Assert.Null(ListenPlaylistOrder.MoveOnto(["a", "b"], 0, 2));
        Assert.Null(ListenPlaylistOrder.MoveOnto(["a", "b"], -1, 0));
    }

    // -- The old inline table is gone ----------------------------------------------------------------------

    [Fact]
    public void ThePlaylistPageDrawsTheSharedSongTableAndNoLongerCarriesItsOwnTableMenuOrSelectors()
    {
        var pages = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Pages");
        var markup = File.ReadAllText(Path.Combine(pages, "ListenPage.razor"));
        var css = File.ReadAllText(Path.Combine(pages, "ListenPage.razor.css"));
        var code = string.Join('\n', Directory.GetFiles(pages, "ListenPage*.cs").Select(File.ReadAllText));

        Assert.Contains("<SongTable Place=\"SongTablePlace.Playlist\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-playlist-track", markup + css, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-column-resizer", markup + css, StringComparison.Ordinal);
        Assert.DoesNotContain("TogglePlaylistColumn", markup + code, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistRowMenuItems", markup + code, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginColumnResize", markup + code, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-playlist-cards", markup + css, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
