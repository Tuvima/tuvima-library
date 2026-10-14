using Bunit;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

public sealed class ListenListsTouchTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public ListenListsTouchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
    }

    private Task UseAsync(string formFactor, bool touch) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, touch));

    private static IReadOnlyList<WorkViewModel> Tracks(int count) =>
        Enumerable.Range(1, count).Select(index => new WorkViewModel
        {
            Id = Guid.NewGuid(),
            RootWorkId = Guid.NewGuid(),
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

    [Fact]
    public async Task PhoneRendersCardsWithRoleAttributesAndARowMenuPerRow()
    {
        await UseAsync("phone", touch: true);
        var tracks = Tracks(3);

        var cut = Render<ListenSongTable>(parameters => parameters
            .Add(table => table.Tracks, tracks)
            .Add(table => table.Sortable, false));

        Assert.NotEmpty(cut.FindAll(".app-table-shell--cards"));
        foreach (var role in new[] { "title", "subtitle", "value", "actions" })
        {
            Assert.Single(cut.FindAll($"th[data-card-role='{role}']"));
            Assert.Equal(tracks.Count, cut.FindAll($"tbody td[data-card-role='{role}']").Count);
        }

        Assert.Equal(tracks.Count, cut.FindAll("tbody .app-row-menu__trigger").Count);
        Assert.Equal("The Artist · The Album", cut.FindAll("tbody td[data-card-role='subtitle']")[0].TextContent.Trim());
        Assert.Empty(cut.FindAll(".listen-song-table__resizer"));
        Assert.Empty(cut.FindAll("[draggable]"));
    }

    [Fact]
    public async Task DesktopKeepsTheDraggableGridWithResizeHandles()
    {
        await UseAsync("desktop", touch: false);

        var cut = Render<ListenSongTable>(parameters => parameters
            .Add(table => table.Tracks, Tracks(3)));

        Assert.Empty(cut.FindAll(".app-table-shell--cards"));
        Assert.NotEmpty(cut.FindAll(".listen-song-table__resizer"));
        Assert.Equal(3, cut.FindAll(".listen-song-table__row[draggable]").Count);
    }

    [Fact]
    public async Task TabletTouchHidesDragAndResizeButKeepsTheGrid()
    {
        await UseAsync("tablet", touch: true);

        var cut = Render<ListenSongTable>(parameters => parameters
            .Add(table => table.Tracks, Tracks(2)));

        Assert.Empty(cut.FindAll(".app-table-shell--cards"));
        Assert.Empty(cut.FindAll(".listen-song-table__resizer"));
        Assert.Empty(cut.FindAll(".listen-song-table__row[draggable]"));
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
