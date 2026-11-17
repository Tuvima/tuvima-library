using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The quadrant tile for groups and for people with works (spec details-and-tile-fixes D1, D2): four covers in a 2 by 2 grid, the kind
/// word top left and the count bottom left; a person's square photo bottom left with the count beside it; a person with no works is a
/// square photo. The fan, the single-cover group fallback and the round person tile are gone.
/// </summary>
public sealed class GroupQuadrantTileTests : AsyncBunitContext
{
    public GroupQuadrantTileTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
    }

    [Fact]
    public void AGroupWithFiveCovers_DrawsOnlyTheFirstFourInOrder()
    {
        var group = Group(Covers("/c/1.jpg", "/c/2.jpg", "/c/3.jpg", "/c/4.jpg", "/c/5.jpg"));

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, group));

        var sources = cut.FindAll("img.media-tile-quadrant").Select(image => image.GetAttribute("src")).ToList();
        Assert.Equal(4, sources.Count);
        for (var index = 0; index < 4; index++)
        {
            Assert.Contains($"/c/{index + 1}.jpg", sources[index], StringComparison.Ordinal);
        }

        Assert.Empty(cut.FindAll(".media-tile-quadrant.is-empty"));
    }

    [Fact]
    public void AGroupWithThreeCovers_FillsWhatExists_AndLeavesTheFourthCellPlain()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Group(Covers("/c/1.jpg", "/c/2.jpg", "/c/3.jpg"))));

        Assert.Equal(3, cut.FindAll("img.media-tile-quadrant").Count);
        Assert.Single(cut.FindAll(".media-tile-quadrant.is-empty"));
    }

    [Fact]
    public void AGroupWithNoCovers_IsThePlainTileWithTheKindIcon()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Group([])));

        Assert.Empty(cut.FindAll(".media-tile-quadrants"));
        Assert.Single(cut.FindAll(".media-tile-fallback"));
    }

    [Fact]
    public void AGroupWithACustomCover_DrawsThatCoverInsteadOfTheQuadrants()
    {
        var group = Group(Covers("/c/1.jpg", "/c/2.jpg"), customCover: "/art/chosen.jpg");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, group));

        Assert.Empty(cut.FindAll(".media-tile-quadrants"));
        Assert.Contains("chosen.jpg", cut.Find("img.media-tile-image").GetAttribute("src"), StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupOfAlbums_IsASquareTile_AndAGroupOfPostersIsAPosterTile()
    {
        var albums = Group(Covers("/a/1.jpg", "/a/2.jpg"), shape: MediaTileShape.Square);
        var posters = Group(Covers("/p/1.jpg", "/p/2.jpg"));

        var square = Render<MediaTile>(p => p.Add(c => c.Item, albums));
        var poster = Render<MediaTile>(p => p.Add(c => c.Item, posters));

        Assert.Contains("is-square", square.Find("article.media-tile").ClassList);
        Assert.Contains("is-portrait", poster.Find("article.media-tile").ClassList);
    }

    [Fact]
    public void TheKindWordIsTheTopLeftLabel_AndAStatusLabelTakesTheSlotFirst()
    {
        var plain = Render<MediaTile>(p => p.Add(c => c.Item, Group(Covers("/c/1.jpg"), kind: MediaTileGroupKind.Universe)));
        var isNew = Render<MediaTile>(p => p.Add(c => c.Item, Group(Covers("/c/1.jpg"), newLabel: "New")));

        Assert.Equal("Universe", plain.Find(".media-tile-label--status").TextContent.Trim());
        Assert.Equal("New", isNew.Find(".media-tile-label--status").TextContent.Trim());
        Assert.Empty(isNew.FindAll(".media-tile-label--kind"));
    }

    [Fact]
    public void AnArtistWithAlbums_IsTheSquareQuadrantTileWithThePhotoBottomLeftAndTheCountBeside()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "Evanescence", id, "/persons/evanescence/headshot", ["Artist"], "Music", 90, 2003, 2021, DateTimeOffset.UtcNow,
            Covers("/a/1.jpg", "/a/2.jpg", "/a/3.jpg", "/a/4.jpg"),
            MediaPersonGroupTileComposer.NavigationUrl(id, "Evanescence"), "#000", albumCount: 11, songCount: 90);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, tile));

        Assert.Contains("is-square", cut.Find("article.media-tile").ClassList);
        Assert.Equal(4, cut.FindAll("img.media-tile-quadrant").Count);
        Assert.Contains("headshot", cut.Find(".media-tile-person-photo img").GetAttribute("src"), StringComparison.Ordinal);
        Assert.Equal("11 albums", cut.Find(".media-tile-label--beside-photo").TextContent.Trim());
        Assert.Empty(cut.FindAll("a.person-tile"));
        Assert.Empty(cut.FindAll(".media-tile-label--kind"));
        Assert.Equal("Evanescence, 11 albums, 90 songs", cut.Find("a.media-tile-link").GetAttribute("aria-label"));
    }

    [Fact]
    public void ADirectorWithFilms_IsThePosterQuadrantTile()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "Ava DuVernay", id, "/persons/ava/headshot", ["Director"], "Movies", 4, 2012, 2023, DateTimeOffset.UtcNow,
            Covers("/m/1.jpg", "/m/2.jpg", "/m/3.jpg", "/m/4.jpg"),
            MediaPersonGroupTileComposer.NavigationUrl(id, "Ava DuVernay"), "#000");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, tile));

        Assert.Contains("is-portrait", cut.Find("article.media-tile").ClassList);
        Assert.Equal(4, cut.FindAll("img.media-tile-quadrant").Count);
        Assert.Equal("4 movies", cut.Find(".media-tile-label--beside-photo").TextContent.Trim());
    }

    [Fact]
    public void APersonWithWorksButNoPhoto_ShowsThePersonIconInTheCorner()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "Ava DuVernay", id, null, ["Director"], "Movies", 2, 2012, 2023, DateTimeOffset.UtcNow,
            Covers("/m/1.jpg", "/m/2.jpg"), MediaPersonGroupTileComposer.NavigationUrl(id, "Ava DuVernay"), "#000");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, tile));

        Assert.Empty(cut.FindAll(".media-tile-person-photo img"));
        Assert.Single(cut.FindAll(".media-tile-person-photo"));
    }

    [Fact]
    public void ACastMemberOrAPersonWithNoWorks_IsASquarePhotoWithTheNameOnTheArt()
    {
        var id = Guid.NewGuid();
        var cast = new MediaTileViewModel
        {
            Id = id,
            Title = "Jane Actor",
            NavigationUrl = $"/details/person/{id:D}",
            Person = new MediaTilePersonViewModel { Id = id, Name = "Jane Actor", ImageUrl = "/persons/jane/headshot", Roles = ["Actor"] },
        };

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, cast));

        Assert.False(cast.RenderAsPersonQuadrants);
        Assert.True(cast.RenderAsPersonPhoto);
        Assert.Equal("Jane Actor", cut.Find(".person-tile__name").TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-tile-quadrants"));
        Assert.Contains("headshot", cut.Find(".person-tile__photo img").GetAttribute("src"), StringComparison.Ordinal);
    }

    [Fact]
    public void APersonWithNoPhoto_IsAPlainSquareWithTheIcon()
    {
        var id = Guid.NewGuid();
        var cast = new MediaTileViewModel
        {
            Id = id,
            Title = "Jane Actor",
            NavigationUrl = $"/details/person/{id:D}",
            Person = new MediaTilePersonViewModel { Id = id, Name = "Jane Actor" },
        };

        var cut = Render<PersonTile>(p => p.Add(c => c.Item, cast));

        Assert.Empty(cut.FindAll(".person-tile__photo img"));
        Assert.Single(cut.FindAll(".person-tile__icon"));
    }

    [Fact]
    public void GroupTileShape_IsSquareOnlyWhenEveryShownCoverIsSquare()
    {
        Assert.Equal(MediaTileShape.Square, GroupTileShape.For(Covers(ArtworkShape.Square, "/a/1.jpg", "/a/2.jpg")));
        Assert.Equal(MediaTileShape.Portrait, GroupTileShape.For(Covers(ArtworkShape.Portrait, "/p/1.jpg", "/p/2.jpg")));
        Assert.Equal(MediaTileShape.Portrait, GroupTileShape.For([
            new ArtworkStackItem { ImageUrl = "/a/1.jpg", Shape = ArtworkShape.Square },
            new ArtworkStackItem { ImageUrl = "/p/1.jpg", Shape = ArtworkShape.Portrait },
        ]));
        Assert.Equal(MediaTileShape.Portrait, GroupTileShape.For([]));
    }

    [Fact]
    public void AnAudiobookAuthor_IsAPosterQuadrantTile_NotASquare()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "Frank Herbert", id, null, ["Author"], "Audiobooks", 3, 1965, 1981, DateTimeOffset.UtcNow,
            Covers("/b/1.jpg", "/b/2.jpg", "/b/3.jpg"), MediaPersonGroupTileComposer.NavigationUrl(id, "Frank Herbert"), "#000");

        Assert.Equal(MediaTileShape.Portrait, tile.Shape);
        Assert.Equal(MediaTileSurfaceKind.CoverPortrait, tile.SurfaceKind);
    }

    [Fact]
    public void AnArtistShelfFromAContributor_IsASquareOfAlbums_WithTheCountAsWorks()
    {
        var shelf = new ContributorShelfDto
        {
            Key = "artist:evanescence",
            PersonId = Guid.NewGuid(),
            PersonName = "Evanescence",
            HeadshotUrl = "/persons/evanescence/headshot",
            Role = "Artist",
            Lane = "Listen",
            Title = "Evanescence",
            OwnedCount = 3,
            Items =
            [
                new ContributorShelfItemDto { WorkId = Guid.NewGuid(), Title = "Fallen", MediaType = "Music", CoverUrl = "/a/1.jpg" },
                new ContributorShelfItemDto { WorkId = Guid.NewGuid(), Title = "The Open Door", MediaType = "Music", CoverUrl = "/a/2.jpg" },
            ],
        };

        var tile = CollectionSurfaceTileComposer.FromContributorShelf(shelf);

        Assert.Equal(MediaTileShape.Square, tile.Shape);
        Assert.True(tile.RenderAsPersonQuadrants);
        Assert.Equal("3 works", MediaTileLabels.Bottom(tile, "Kept"));
    }

    [Fact]
    public void ACollectionOfAlbumsIsSquare_AndItsChosenCoverIsCarriedAsTheCustomCover()
    {
        var collection = new CollectionManagementCatalogViewModel
        {
            Id = Guid.NewGuid(),
            Name = "Listening Room",
            ItemCount = 2,
            CoverArtworkUrl = "/stream/artwork/chosen",
            ArtworkItems =
            [
                new CollectionArtworkItemDto { WorkId = Guid.NewGuid(), Title = "One", MediaType = "Music", CoverUrl = "/stream/artwork/00000000-0000-0000-0000-000000000001", ArtworkShape = "square" },
                new CollectionArtworkItemDto { WorkId = Guid.NewGuid(), Title = "Two", MediaType = "Music", CoverUrl = "/stream/artwork/00000000-0000-0000-0000-000000000002", ArtworkShape = "square" },
            ],
        };
        var automatic = new CollectionManagementCatalogViewModel
        {
            Id = Guid.NewGuid(),
            Name = "Listening Room",
            ItemCount = 2,
            ArtworkItems = collection.ArtworkItems,
        };

        var chosen = CollectionSurfaceTileComposer.FromCollection(collection);
        var stacked = CollectionSurfaceTileComposer.FromCollection(automatic);

        Assert.Equal(MediaTileShape.Square, chosen.Shape);
        Assert.Equal("/stream/artwork/chosen", chosen.CustomCoverUrl);
        Assert.Null(stacked.CustomCoverUrl);
        Assert.Equal(2, stacked.GroupCovers.Count);
    }

    private static MediaTileViewModel Group(
        IReadOnlyList<ArtworkStackItem> covers,
        MediaTileShape shape = MediaTileShape.Portrait,
        MediaTileGroupKind kind = MediaTileGroupKind.Collection,
        string? customCover = null,
        string? newLabel = null) => new()
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "Group",
            IsCollection = true,
            UseGroupTile = true,
            GroupKind = kind,
            Shape = shape,
            NewLabel = newLabel,
            CustomCoverUrl = customCover,
            ArtworkStackItems = covers,
            NavigationUrl = "/details/collection/group",
            PreviewTotalCount = covers.Count,
        };

    private static IReadOnlyList<ArtworkStackItem> Covers(params string[] urls) => Covers(ArtworkShape.Portrait, urls);

    private static IReadOnlyList<ArtworkStackItem> Covers(ArtworkShape shape, params string[] urls) =>
        urls.Select((url, index) => new ArtworkStackItem { Id = index.ToString(), Title = $"Item {index}", ImageUrl = url, MediaType = "Book", Shape = shape }).ToList();
}
