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

    [Theory]
    [InlineData("Music", 148, 12, 148, 1965, 2016, "1965–2016 · 12 albums · 148 songs")]
    [InlineData("Music", 1, 1, 1, 1970, 1970, "1970 · 1 album · 1 song")]
    [InlineData("Books", 21, null, null, 1937, 1977, "1937–1977 · 21 books")]
    [InlineData("Audiobooks", 1, null, null, null, 2003, "2003 · 1 audiobook")]
    [InlineData("Movies", 7, null, null, null, null, "7 movies")]
    [InlineData("TV", 3, null, null, 2010, 2020, "2010–2020 · 3 shows")]
    public void FactsLine_ShowsActiveYearsAndTheCountsThatFitTheRole(
        string mediaType, int owned, int? albums, int? songs, int? from, int? to, string expected)
        => Assert.Equal(expected, MediaPersonGroupTileComposer.FactsLine(mediaType, owned, albums, songs, from, to));

    [Fact]
    public void Compose_ForAnArtist_CountsAlbumsAndSongsSeparately()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "David Bowie", id, null, ["Artist"], "Music", 148, 1967, 2016, DateTimeOffset.UtcNow, [],
            MediaPersonGroupTileComposer.NavigationUrl(id, "David Bowie"), "#1ED760", albumCount: 12, songCount: 148);

        Assert.Equal("1967–2016 · 12 albums · 148 songs", tile.Person?.FactsLine);
        Assert.Equal(["albums", "songs"], tile.MediaCounts.Select(count => count.Label).ToArray());
    }
}
