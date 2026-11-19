using Bunit;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Group tiles of the 2026-11-18 review mock-ups (D4, D5), now drawn as the landscape cover collage (issue 1175): a person's tile keeps
/// the grid (one title fills the tile, two fill the left column, three add the top right, four fill the grid, empty squares are dashed);
/// a group of titles has the name and count on the collage's solid band; a Series has a purple outline and a tag that says what it holds;
/// a group that mixes media kinds is a gold-outlined Collection in the same collage; a Universe has no outline.
/// </summary>
public sealed class GroupTileLayoutTests : AsyncBunitContext
{
    public GroupTileLayoutTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
    }

    [Fact]
    public void OneTitle_OfAPerson_IsOneFullCoverFillingTheTile_WithNoEmptySquares()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Person(Book("/c/1.jpg"))));

        Assert.Contains("is-count-1", cut.Find(".media-tile-quadrants").ClassList);
        var cover = Assert.Single(cut.FindAll("img.media-tile-quadrant"));
        Assert.Contains("is-single", cover.ClassList);
        Assert.Empty(cut.FindAll(".media-tile-quadrant.is-empty"));
    }

    [Fact]
    public void TwoTitles_OfAPerson_TakeTopLeftAndBottomLeft_AndTheRightColumnIsTwoEmptySquares()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Person(Book("/c/1.jpg"), Book("/c/2.jpg"))));

        Assert.Equal(["is-slot-tl", "is-slot-bl"], Slots(cut, "img.media-tile-quadrant"));
        Assert.Equal(["is-slot-tr", "is-slot-br"], Slots(cut, ".media-tile-quadrant.is-empty"));
    }

    [Fact]
    public void ThreeTitles_OfAPerson_TakeTopLeftBottomLeftAndTopRight_AndTheBottomRightIsEmpty()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Person(Book("/c/1.jpg"), Book("/c/2.jpg"), Book("/c/3.jpg"))));

        Assert.Equal(["is-slot-tl", "is-slot-bl", "is-slot-tr"], Slots(cut, "img.media-tile-quadrant"));
        Assert.Equal(["is-slot-br"], Slots(cut, ".media-tile-quadrant.is-empty"));
    }

    [Fact]
    public void FourTitles_OfAPerson_FillTheGrid_InOrder_WithNoEmptySquares()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Person(Book("/c/1.jpg"), Book("/c/2.jpg"), Book("/c/3.jpg"), Book("/c/4.jpg"), Book("/c/5.jpg"))));

        Assert.Equal(["is-slot-tl", "is-slot-bl", "is-slot-tr", "is-slot-br"], Slots(cut, "img.media-tile-quadrant"));
        Assert.Empty(cut.FindAll(".media-tile-quadrant.is-empty"));
        var sources = cut.FindAll("img.media-tile-quadrant").Select(image => image.GetAttribute("src")).ToList();
        for (var index = 0; index < 4; index++)
        {
            Assert.Contains($"/c/{index + 1}.jpg", sources[index], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void APersonsCoverKeepsItsOwnShape_APortraitFillsItsQuarterAndASquareStaysSquare()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Person(
            Cover("/c/1.jpg", "Audiobook", ArtworkShape.Portrait),
            Cover("/c/2.jpg", "Audiobook", ArtworkShape.Square),
            Cover("/c/3.jpg", "Audiobook", ArtworkShape.Wide))));

        var shapes = cut.FindAll("img.media-tile-quadrant").Select(image => image.ClassList.Single(name => name.StartsWith("is-shape-", StringComparison.Ordinal))).ToList();
        Assert.Equal(["is-shape-portrait", "is-shape-square", "is-shape-wide"], shapes);
    }

    [Fact]
    public void TheNameAndTheCountOfAGroupSitTogetherOnTheSolidBand()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Group(Book("/c/1.jpg"), Book("/c/2.jpg"))));

        var band = cut.Find(".group-collage__band");
        Assert.Equal("Group", band.QuerySelector(".group-collage__name")!.TextContent.Trim());
        Assert.Equal("2 items", band.QuerySelector(".group-collage__count")!.TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-tile-label--bottom"));
        Assert.Empty(cut.FindAll(".media-tile-scrim"));
    }

    [Fact]
    public void ASeriesOfOneMediaKind_IsTheLandscapeCollageWithAPurpleOutlineAndTheTagSaysWhatItHolds()
    {
        var books = Render<MediaTile>(p => p.Add(c => c.Item, Group(Series(MediaTilePresentation.BookSeries), Book("/c/1.jpg"), Book("/c/2.jpg"))));
        var audio = Render<MediaTile>(p => p.Add(c => c.Item, Group(Series(MediaTilePresentation.AudiobookSeries),
            Cover("/a/1.jpg", "Audiobook", ArtworkShape.Square), Cover("/a/2.jpg", "Audiobook", ArtworkShape.Square))));
        var plain = Render<MediaTile>(p => p.Add(c => c.Item, Group(Series(MediaTilePresentation.Default), Book("/c/1.jpg"), Book("/c/2.jpg"))));

        Assert.Equal("Book series", books.Find(".media-tile-label--kind").TextContent.Trim());
        Assert.Equal("Audiobook series", audio.Find(".media-tile-label--kind").TextContent.Trim());
        Assert.Equal("Series", plain.Find(".media-tile-label--kind").TextContent.Trim());
        foreach (var tile in new[] { books, audio, plain })
        {
            var article = tile.Find("article.media-tile");
            Assert.Contains("is-series-outlined", article.ClassList);
            Assert.DoesNotContain("is-collection-outlined", article.ClassList);
            Assert.Contains("is-landscape", article.ClassList);
            Assert.Empty(tile.FindAll(".media-tile-quadrants"));
        }
    }

    [Fact]
    public void AGroupOfMixedMediaKinds_IsAGoldOutlinedCollection_WithItsCoversInTheCollageInNaturalShapes()
    {
        var group = Group(Series(MediaTilePresentation.BookSeries),
            Cover("/c/book.jpg", "Book", ArtworkShape.Portrait),
            Cover("/c/audio.jpg", "Audiobook", ArtworkShape.Square),
            Cover("/c/film.jpg", "Movie", ArtworkShape.Wide));

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, group));

        var article = cut.Find("article.media-tile");
        Assert.Contains("is-collection-outlined", article.ClassList);
        Assert.DoesNotContain("is-series-outlined", article.ClassList);
        Assert.Contains("is-landscape", article.ClassList);
        Assert.DoesNotContain("is-portrait", article.ClassList);
        Assert.Equal("Collection", cut.Find(".media-tile-label--kind").TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-tile-quadrants"));
        Assert.Equal(["portrait", "square", "wide"], cut.FindAll(".group-collage__cover").Select(cover => cover.GetAttribute("data-shape")));
    }

    [Fact]
    public void ABookAndItsAudiobook_AreDifferentMediaKinds_SoTheGroupIsACollection()
    {
        var group = Group(Series(MediaTilePresentation.Default), Cover("/c/1.jpg", "Book", ArtworkShape.Portrait), Cover("/c/2.jpg", "Audiobooks", ArtworkShape.Square));

        Assert.True(group.HoldsMixedMedia);
        Assert.False(Group(Series(MediaTilePresentation.Default), Cover("/c/1.jpg", "Book", ArtworkShape.Portrait), Cover("/c/2.jpg", "Books", ArtworkShape.Portrait)).HoldsMixedMedia);
    }

    [Fact]
    public void AUniverse_IsTheSameCollageWithNoOutline_EvenWhenItHoldsMixedMedia()
    {
        var universe = Group(
            new MediaTileViewModel { GroupKind = MediaTileGroupKind.Universe },
            Cover("/c/1.jpg", "Book", ArtworkShape.Portrait),
            Cover("/c/2.jpg", "Movie", ArtworkShape.Portrait));

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, universe));

        var article = cut.Find("article.media-tile");
        Assert.Contains("is-landscape", article.ClassList);
        Assert.DoesNotContain("is-series-outlined", article.ClassList);
        Assert.DoesNotContain("is-collection-outlined", article.ClassList);
        Assert.Equal("Universe", cut.Find(".media-tile-label--kind").TextContent.Trim());
        Assert.Equal(2, cut.FindAll(".group-collage__cover").Count);
    }

    [Fact]
    public void ACuratedCollectionOfOneMediaKind_HasNoSeriesOutline()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Group(Book("/c/1.jpg"), Book("/c/2.jpg"))));

        var article = cut.Find("article.media-tile");
        Assert.DoesNotContain("is-series-outlined", article.ClassList);
        Assert.DoesNotContain("is-collection-outlined", article.ClassList);
        Assert.Equal("Collection", cut.Find(".media-tile-label--kind").TextContent.Trim());
    }

    [Fact]
    public void AnArtistWithAlbums_KeepsASquareTileWithTheNewGridAndNoOutline()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "Evanescence", id, "/persons/evanescence/headshot", ["Artist"], "Music", 90, 2003, 2021, DateTimeOffset.UtcNow,
            [Cover("/a/1.jpg", "Music", ArtworkShape.Square), Cover("/a/2.jpg", "Music", ArtworkShape.Square)],
            MediaPersonGroupTileComposer.NavigationUrl(id, "Evanescence"), "#000", albumCount: 2, songCount: 20);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, tile));

        var article = cut.Find("article.media-tile");
        Assert.Contains("is-square", article.ClassList);
        Assert.DoesNotContain("is-series-outlined", article.ClassList);
        Assert.Equal(["is-slot-tl", "is-slot-bl"], Slots(cut, "img.media-tile-quadrant"));
        Assert.Equal(["is-slot-tr", "is-slot-br"], Slots(cut, ".media-tile-quadrant.is-empty"));
    }

    private static List<string> Slots(IRenderedComponent<MediaTile> cut, string selector) =>
        cut.FindAll(selector).Select(node => node.ClassList.Single(name => name.StartsWith("is-slot-", StringComparison.Ordinal))).ToList();

    private static ArtworkStackItem Book(string url) => Cover(url, "Book", ArtworkShape.Portrait);

    private static ArtworkStackItem Cover(string url, string mediaType, ArtworkShape shape) =>
        new() { Id = url, Title = url, ImageUrl = url, MediaType = mediaType, Shape = shape };

    private static MediaTileViewModel Series(MediaTilePresentation presentation) =>
        new() { GroupKind = MediaTileGroupKind.Series, Presentation = presentation };

    // A person's tile: the grid around the person's photo.
    private static MediaTileViewModel Person(params ArtworkStackItem[] covers)
    {
        var id = Guid.NewGuid();
        return MediaPersonGroupTileComposer.Compose(
            id, "Ava DuVernay", id, "/persons/ava/headshot", ["Director"], "Movies", covers.Length, 2012, 2023, DateTimeOffset.UtcNow,
            covers, MediaPersonGroupTileComposer.NavigationUrl(id, "Ava DuVernay"), "#000");
    }

    private static MediaTileViewModel Group(params ArtworkStackItem[] covers) =>
        Group(new MediaTileViewModel { GroupKind = MediaTileGroupKind.Collection }, covers);

    private static MediaTileViewModel Group(MediaTileViewModel kind, params ArtworkStackItem[] covers) => new()
    {
        Id = Guid.NewGuid(),
        CollectionId = Guid.NewGuid(),
        Title = "Group",
        IsCollection = true,
        UseGroupTile = true,
        GroupKind = kind.GroupKind,
        Presentation = kind.Presentation,
        Shape = MediaTileShape.Landscape,
        ArtworkStackItems = covers,
        NavigationUrl = "/details/collection/group",
        PreviewTotalCount = covers.Length,
    };
}
