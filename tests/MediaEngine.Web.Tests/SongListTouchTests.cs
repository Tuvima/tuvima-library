using Bunit;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>The Songs and Albums lists on each kind of device: the plain phone list, the draggable desktop grid, and a touch tablet with neither drag nor resize.</summary>
public sealed class SongListTouchTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public SongListTouchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
    }

    private Task UseAsync(string formFactor, bool touch) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, touch));

    private static IReadOnlyList<WorkViewModel> Tracks(int count) =>
        Enumerable.Range(1, count).Select(index => new WorkViewModel
        {
            Id = Guid.NewGuid(),
            RootWorkId = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            ArtistPersonId = Guid.NewGuid(),
            MediaType = "Music",
            CanonicalValues =
            [
                new CanonicalValueViewModel { Key = "title", Value = $"Track {index}" },
                new CanonicalValueViewModel { Key = "artist", Value = "The Artist" },
                new CanonicalValueViewModel { Key = "album", Value = "The Album" },
                new CanonicalValueViewModel { Key = "duration_sec", Value = "185" },
            ],
        }).ToList();

    private IRenderedComponent<SongList> RenderList(IReadOnlyList<WorkViewModel> tracks, bool dragOut = true) =>
        Render<SongList>(parameters =>
        {
            parameters.Add(list => list.Place, SongTablePlace.Songs).Add(list => list.Tracks, tracks);
            if (dragOut)
            {
                parameters.Add(list => list.OnDragStarted, _ => Task.CompletedTask).Add(list => list.OnDragEnded, () => Task.CompletedTask);
            }
        });

    [Fact]
    public async Task Phone_ShowsAPlainListWithTitleArtistAndARowMenuPerRow()
    {
        await UseAsync("phone", touch: true);
        var tracks = Tracks(3);

        var cut = RenderList(tracks);

        Assert.Empty(cut.FindAll(".app-table-shell--cards"));
        Assert.Equal(tracks.Count, cut.FindAll(".song-table__prow").Count);
        Assert.Equal(tracks.Count, cut.FindAll(".song-table__pmenu button[aria-haspopup='menu']").Count);
        Assert.Equal("The Artist", cut.FindAll(".song-table__psub")[0].TextContent.Trim());
        Assert.DoesNotContain("The Album", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".song-table__resizer"));
        Assert.Empty(cut.FindAll("[draggable]"));
        Assert.Empty(cut.FindAll("input[type='checkbox']"));
    }

    [Fact]
    public async Task Desktop_KeepsTheDraggableGridWithResizeHandlesAndTheColumnsButton()
    {
        await UseAsync("desktop", touch: false);

        var cut = RenderList(Tracks(3));

        Assert.NotEmpty(cut.FindAll(".song-table__resizer"));
        Assert.Equal(3, cut.FindAll(".song-table__row[draggable]").Count);
        Assert.NotEmpty(cut.FindAll(".song-table__head-cell--actions button[aria-haspopup='menu']"));
    }

    [Fact]
    public async Task Desktop_WithNoDragHostedRowsAreNotDraggable()
    {
        await UseAsync("desktop", touch: false);

        var cut = RenderList(Tracks(2), dragOut: false);

        Assert.Empty(cut.FindAll(".song-table__row[draggable]"));
    }

    [Fact]
    public async Task TabletTouch_HidesDragAndResizeButKeepsTheGrid()
    {
        await UseAsync("tablet", touch: true);

        var cut = RenderList(Tracks(2));

        Assert.NotEmpty(cut.FindAll(".song-table__row"));
        Assert.Empty(cut.FindAll(".song-table__resizer"));
        Assert.Empty(cut.FindAll(".song-table__row[draggable]"));
    }

    [Fact]
    public async Task DraggingARowOut_TellsTheHostWhichSongStartedAndWhenItEnded()
    {
        await UseAsync("desktop", touch: false);
        var tracks = Tracks(2);
        var started = new List<Guid>();
        var ended = 0;
        var cut = Render<SongList>(parameters => parameters
            .Add(list => list.Place, SongTablePlace.Songs)
            .Add(list => list.Tracks, tracks)
            .Add(list => list.OnDragStarted, id =>
            {
                started.Add(id);
                return Task.CompletedTask;
            })
            .Add(list => list.OnDragEnded, () =>
            {
                ended++;
                return Task.CompletedTask;
            }));

        var row = cut.FindAll(".song-table__row")[1];
        row.DragStart();
        row.DragEnd();

        // Both songs tie on date added, so the list falls back to title order: the second row is Track 2.
        Assert.Equal([tracks[1].Id], started);
        Assert.Equal(1, ended);
    }

    [Theory]
    [InlineData(0, -1, false)]
    [InlineData(0, 1, true)]
    [InlineData(2, 1, false)]
    [InlineData(2, -1, true)]
    [InlineData(1, -1, true)]
    [InlineData(1, 1, true)]
    public void PlaylistOrderMovesOneStepAndStopsAtTheEnds(int source, int delta, bool moves)
    {
        var items = new[] { "a", "b", "c" };

        var result = ListenPlaylistOrder.Move(items, source, delta);

        Assert.Equal(moves, result is not null);
        if (result is null)
        {
            return;
        }

        Assert.Equal(items[source], result[source + delta]);
        Assert.Equal(items.OrderBy(x => x), result.OrderBy(x => x));
    }

    [Fact]
    public void PlaylistOrderMovesTheRightRow()
    {
        Assert.Equal(["b", "a", "c"], ListenPlaylistOrder.Move(["a", "b", "c"], 1, -1));
        Assert.Equal(["a", "c", "b"], ListenPlaylistOrder.Move(["a", "b", "c"], 1, 1));
    }
}
