using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

public sealed class MediaPersonGroupTileComposerTests
{
    [Fact]
    public void Compose_BuildsSharedPersonCollectionWithTruthfulAudiobookCount()
    {
        var personId = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            personId,
            "George R. R. Martin",
            personId,
            "/persons/headshot",
            ["Author", "Screenwriter"],
            "Audiobooks",
            1,
            2003,
            2003,
            DateTimeOffset.UtcNow,
            [
                new ArtworkStackItem
                {
                    Id = "audiobook",
                    Title = "A Game of Thrones",
                    ImageUrl = "/stream/audiobook/cover",
                    MediaType = "Audiobooks",
                },
            ],
            MediaPersonGroupTileComposer.NavigationUrl(personId, "George R. R. Martin"),
            "var(--tl-media-audio)");

        Assert.True(tile.RenderAsLandscapeGroupTile);
        Assert.Equal($"/details/person/{personId:D}", tile.NavigationUrl);
        Assert.Equal(personId, tile.Person?.Id);
        Assert.Equal(["Author", "Screenwriter"], tile.Person?.Roles);
        var count = Assert.Single(tile.MediaCounts);
        Assert.Equal(AppMaterialIcons.Outlined.Headphones, count.Icon);
        Assert.Equal("audiobook", count.Label);
        Assert.Equal(1, count.Count);
    }

    [Fact]
    public void NavigationUrl_PreservesTheOriginatingLaneContext()
    {
        var personId = Guid.NewGuid();

        var route = MediaPersonGroupTileComposer.NavigationUrl(personId, "David Bowie", "listen");

        Assert.Equal($"/details/person/{personId:D}?context=listen", route);
    }
}
