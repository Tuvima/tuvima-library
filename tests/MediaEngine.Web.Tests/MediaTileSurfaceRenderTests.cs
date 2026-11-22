using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class MediaTileSurfaceRenderTests : AsyncBunitContext
{
    private int _profileRequestCount;
    private int _managedCollectionRequestCount;
    private int _createCollectionRequestCount;

    public MediaTileSurfaceRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
            {
                Interlocked.Increment(ref _profileRequestCount);
                return Task.FromResult(new List<ProfileViewModel>
                {
                    new(
                        Guid.Parse("00000000-0000-0000-0000-000000000001"),
                        "Test User",
                        "#C9922E",
                        "Administrator",
                        DateTimeOffset.UtcNow),
                });
            });
            stub.SetHandler(nameof(IEngineApiClient.GetManagedCollectionsAsync), _ =>
            {
                Interlocked.Increment(ref _managedCollectionRequestCount);
                return Task.FromResult(new List<ManagedCollectionViewModel>());
            });
            stub.SetHandler(nameof(IEngineApiClient.CreateCollectionAsync), _ =>
            {
                Interlocked.Increment(ref _createCollectionRequestCount);
                return Task.FromResult(true);
            });
        });
        Services.AddSingleton(api);
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<MediaReactionService>();
        Services.AddScoped<SavedItemService>();
        Services.AddScoped(_ => new PlaybackSessionController(null!, api));
    }

    [Fact]
    public void PickUpRowIsOneMixedRowOfLandscapeTiles()
    {
        MediaTileViewModel Item(string kind, MediaTileShape shape) => new()
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Title = $"Continue {kind}",
            Creator = "Fixture creator",
            MediaKind = kind,
            Shape = shape,
            TileImageUrl = "/art.jpg",
            ProgressPct = 42,
            DetailsNavigationUrl = "/details/work/fixture",
            PrimaryNavigationUrl = "/resume/fixture",
        };
        var items = new List<MediaTileViewModel> { Item("Book", MediaTileShape.Portrait), Item("TV", MediaTileShape.Landscape), Item("Audiobook", MediaTileShape.Square) };
        var cut = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel { Items = items }));
        Assert.Equal("Pick up where you left off", cut.Find("h2").TextContent);
        Assert.Empty(cut.FindAll(".continue-group"));
        Assert.Empty(cut.FindAll(".pickup-card"));
        // Every kind is the same landscape tile (details-and-tile-fixes D3); a book or audiobook says its kind in the top-left label.
        var cards = cut.FindAll("article.continue-tile");
        Assert.Equal(3, cards.Count);
        // The type is said in words on the description line, never as a label on the picture (issue 1193).
        Assert.All(cards, card => Assert.Null(card.QuerySelector(".media-tile-label--kind")));
        Assert.Equal("Book", cards.Single(c => c.TextContent.Contains("Continue Book")).QuerySelector(".continue-tile__line")!.TextContent.Trim());
        Assert.Equal("Audiobook", cards.Single(c => c.TextContent.Contains("Continue Audiobook")).QuerySelector(".continue-tile__line")!.TextContent.Trim());
        Assert.Equal("Show", cards.Single(c => c.TextContent.Contains("Continue TV")).QuerySelector(".continue-tile__line")!.TextContent.Trim());
        var book = cards.Single(c => c.TextContent.Contains("Continue Book"));
        // No content rating or year known: the second row is just the type, and the percent is a badge, not a text row (review mock-ups D6).
        Assert.Null(book.QuerySelector(".continue-tile__progress-text"));
        Assert.Equal("42%", book.QuerySelector(".continue-tile__percent")!.TextContent.Trim());
        Assert.All(cut.FindAll("[role=progressbar]"), bar => Assert.Equal("42", bar.GetAttribute("aria-valuenow")));
    }

    [Fact]
    public void MediaTileGrid_RendersAndReordersDistinctResumeAssetsForOneWork()
    {
        var workId = Guid.NewGuid();
        var firstAsset = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        var first = CreateResumeAsset(workId, firstAsset, "/listen/player/first", 18);
        var second = CreateResumeAsset(workId, secondAsset, "/listen/player/second", 73);

        var cut = Render<MediaTileGrid>(parameters => parameters.Add(component => component.Items, [first, second]));
        var initial = cut.FindComponents<MediaTile>()
            .ToDictionary(component => component.Instance.Item.AssetId!.Value, component => component.Instance);

        Assert.Equal(2, initial.Count);
        Assert.Equal(18d, initial[firstAsset].Item.ProgressPct!.Value);
        Assert.Equal("/listen/player/first", initial[firstAsset].Item.PrimaryNavigationUrl);
        Assert.Equal(73d, initial[secondAsset].Item.ProgressPct!.Value);
        Assert.Equal("/listen/player/second", initial[secondAsset].Item.PrimaryNavigationUrl);

        cut.Render(parameters => parameters.Add(component => component.Items, [second, first]));
        var reordered = cut.FindComponents<MediaTile>()
            .ToDictionary(component => component.Instance.Item.AssetId!.Value, component => component.Instance);

        Assert.Same(initial[firstAsset], reordered[firstAsset]);
        Assert.Same(initial[secondAsset], reordered[secondAsset]);
    }

    private static MediaTileViewModel CreateResumeAsset(Guid workId, Guid assetId, string resumeUrl, double progress) => new()
    {
        Id = workId,
        WorkId = workId,
        AssetId = assetId,
        Title = "Project Hail Mary",
        Subtitle = "Andy Weir",
        MediaKind = "Audiobook",
        Shape = MediaTileShape.Square,
        SurfaceKind = MediaTileSurfaceKind.CoverSquare,
        NavigationUrl = $"/details/audiobook/{workId:D}",
        DetailsNavigationUrl = $"/details/audiobook/{workId:D}",
        PrimaryNavigationUrl = resumeUrl,
        ProgressPct = progress,
    };

    [Fact]
    public void MediaTile_DetailsSurfaceIsSemanticLinkWithVisibleKeyboardFocus()
    {
        const string details = "/details/work/semantic-card";
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Semantic Card",
            MediaKind = "Book",
            Shape = MediaTileShape.Portrait,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            TileImageUrl = "/art/semantic-card.jpg",
            NavigationUrl = details,
            DetailsNavigationUrl = details,
        };
        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        var card = cut.Find("article.media-tile");
        var detailsLink = cut.Find("a.media-tile-link");

        Assert.Null(card.GetAttribute("tabindex"));
        Assert.Equal(details, detailsLink.GetAttribute("href"));
        Assert.Equal("View details for Semantic Card", detailsLink.GetAttribute("aria-label"));

        detailsLink.Click();

        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith(details, nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaTile_ComicPortraitHoverDoesNotRenderArtworkOrIdentityText()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Chapter Three",
            Subtitle = "Issue 3 in Saga",
            MediaKind = "Comic",
            Shape = MediaTileShape.Portrait,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            TileImageUrl = "/art/saga-three.jpg",
            NavigationUrl = "/comic/saga-three",
            DetailsNavigationUrl = "/comic/saga-three",
            HoverFacts = ["Image", "2012", "144 pages"],
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));
        Assert.Empty(cut.FindAll(".media-tile-hover-identity-strip"));
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
        Assert.Empty(cut.FindAll("button:not(.media-tile-action):not([hidden])"));
    }

    [Fact]
    public void MediaTileShelf_PreservesCardInstancesWhenItemsReorder()
    {
        var first = CreateShelfItem("First");
        var second = CreateShelfItem("Second");
        var cut = Render<MediaTileShelf>(parameters => parameters.Add(
            component => component.Shelf,
            new MediaTileShelfViewModel
            {
                Key = "reorder-test",
                Title = "Reorder test",
                Items = [first, second],
            }));
        var initialInstances = cut.FindComponents<MediaTile>()
            .ToDictionary(component => component.Instance.Item.Id, component => component.Instance);

        cut.Render(parameters => parameters.Add(
            component => component.Shelf,
            new MediaTileShelfViewModel
            {
                Key = "reorder-test",
                Title = "Reorder test",
                Items = [second, first],
            }));
        var reorderedInstances = cut.FindComponents<MediaTile>()
            .ToDictionary(component => component.Instance.Item.Id, component => component.Instance);

        Assert.Same(initialInstances[first.Id], reorderedInstances[first.Id]);
        Assert.Same(initialInstances[second.Id], reorderedInstances[second.Id]);
    }

    [Fact]
    public void MediaTileGrid_DrawsAGroupAsALandscapeCollageBesideASingleTitlesPoster()
    {
        var group = CreateGroupTile();
        var ordinary = CreateShelfItem("Ordinary title");

        var cut = Render<MediaTileGrid>(parameters => parameters.Add(
            component => component.Items,
            [group, ordinary]));

        var tiles = cut.FindAll("article.media-tile");
        Assert.Equal(2, tiles.Count);
        Assert.Single(cut.FindAll("article.media-tile.is-group"));
        Assert.Contains("is-landscape", cut.Find("article.media-tile.is-group").ClassList);
        Assert.Contains("is-portrait", tiles.Single(tile => !tile.ClassList.Contains("is-group")).ClassList);
    }

    [Fact]
    public void MediaTileGrid_DrawsEveryNonTvCollectionAsAGroupPoster()
    {
        var collection = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "Cross-media universe",
            IsCollection = true,
            Presentation = MediaTilePresentation.Default,
            NavigationUrl = "/details/collection/cross-media",
        };
        var tvShow = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Owned TV show",
            IsCollection = true,
            Presentation = MediaTilePresentation.TvSeries,
            NavigationUrl = "/watch/tv/show/owned",
        };

        var cut = Render<MediaTileGrid>(parameters => parameters.Add(
            component => component.Items,
            [collection, tvShow]));

        Assert.Equal(2, cut.FindAll("article.media-tile").Count);
        Assert.Single(cut.FindAll("article.media-tile.is-group"));
    }

    [Theory]
    [InlineData(MediaTileGroupKind.Collection, "Collection")]
    [InlineData(MediaTileGroupKind.Series, "Series")]
    [InlineData(MediaTileGroupKind.Universe, "Universe")]
    [InlineData(MediaTileGroupKind.List, "List")]
    [InlineData(MediaTileGroupKind.Set, "Set")]
    public void AGroupTile_NamesItsKindToScreenReadersAndDrawsItInTheTopLeftSlot(MediaTileGroupKind kind, string word)
    {
        var group = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Group",
            IsCollection = true,
            UseGroupTile = true,
            GroupKind = kind,
            TileImageUrl = "/art/group.jpg",
            NavigationUrl = "/details/collection/x",
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, group));

        Assert.Equal($"Group, {word}", cut.Find("a.media-tile-link").GetAttribute("aria-label"));
        Assert.Equal(word, cut.Find(".media-tile-label--status").TextContent.Trim());
    }

    [Fact]
    public void AGroupTile_DrawsItsMemberCoversInTheCollage_NotASingleCoverOrAFannedStack()
    {
        var group = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Halloween",
            IsCollection = true,
            UseGroupTile = true,
            CoverUrl = "/art/halloween.jpg",
            TileImageUrl = "/art/halloween.jpg",
            NavigationUrl = "/details/collection/halloween",
            ArtworkStackItems = [new ArtworkStackItem { Id = "1", Title = "Film", ImageUrl = "/covers/film.jpg", MediaType = "Movie", Shape = ArtworkShape.Portrait }],
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, group));

        Assert.Empty(cut.FindAll("img.media-tile-image"));
        Assert.Empty(cut.FindAll(".media-tile-stack"));
        Assert.Empty(cut.FindAll("img.media-tile-quadrant"));
        Assert.Single(cut.FindAll(".group-collage__cover img"));
        Assert.Contains("film.jpg", cut.Find(".group-collage__cover img").GetAttribute("src"), StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupTileWithMemberCovers_DrawsThemInTheCollage_AndThePlainTileWhenItHoldsNone()
    {
        var withMembers = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Cross-Media Archive",
            IsCollection = true,
            UseGroupTile = true,
            NavigationUrl = "/details/collection/cross-media",
            ArtworkStackItems =
            [
                new ArtworkStackItem { Id = "book-1", Title = "Book 1", ImageUrl = "/covers/book-1.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait },
                new ArtworkStackItem { Id = "movie", Title = "Movie", ImageUrl = "/covers/movie.jpg", MediaType = "Movie", Shape = ArtworkShape.Portrait },
            ],
        };
        var empty = new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Nothing yet", IsCollection = true, UseGroupTile = true, NavigationUrl = "/details/collection/none" };

        var filled = Render<MediaTile>(parameters => parameters.Add(component => component.Item, withMembers));
        var plain = Render<MediaTile>(parameters => parameters.Add(component => component.Item, empty));

        Assert.Equal(2, filled.FindAll(".group-collage__cover img").Count);
        Assert.Contains("book-1.jpg", filled.FindAll(".group-collage__cover img")[0].GetAttribute("src"), StringComparison.Ordinal);
        Assert.Empty(filled.FindAll("img.media-tile-image"));
        Assert.Empty(plain.FindAll(".group-collage"));
        Assert.Single(plain.FindAll(".media-tile-fallback"));
    }

    [Fact]
    public void MediaTile_DrawsAPersonWithNoCoversAsASquarePhotoTile()
    {
        var personId = Guid.NewGuid();
        var group = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Ava DuVernay",
            NavigationUrl = $"/details/person/{personId:D}",
            IsCollection = true,
            UseGroupTile = true,
            MediaCounts = [new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Movie, "Movies", 2)],
            Person = new MediaTilePersonViewModel { Id = personId, Name = "Ava DuVernay", ImageUrl = $"/persons/{personId:D}/headshot", Roles = ["Director"] },
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, group));

        Assert.Single(cut.FindAll("a.person-tile"));
        Assert.Equal($"/details/person/{personId:D}", cut.Find("a.person-tile").GetAttribute("href"));
        Assert.Empty(cut.FindAll("article.media-tile"));
        Assert.Equal("Ava DuVernay", cut.Find(".person-tile__name").TextContent.Trim());
        Assert.Equal("2 movies", cut.Find(".media-tile-label--bottom").TextContent.Trim());
    }

    [Fact]
    public void AGroupTile_NetworkGroupShowsItsResolvedLogoWithoutChangingTheGroupRoute()
    {
        var group = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "HBO",
            LogoUrl = "/images/brands/hbo.svg",
            ShowLogoAsBrand = true,
            NavigationUrl = "/watch/tv?browse=networks&network=HBO",
            DetailsNavigationUrl = "/watch/tv?browse=networks&network=HBO",
            IsCollection = true,
            UseGroupTile = true,
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, group));

        Assert.Equal("/images/brands/hbo.svg", cut.Find(".media-tile-brand").GetAttribute("src"));
        Assert.Equal("/watch/tv?browse=networks&network=HBO", cut.Find("a.media-tile-link").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".media-tile-quadrants"));
        Assert.Empty(cut.FindAll(".media-tile-label--kind"));
    }

    [Fact]
    public void AGroupTile_DrawsOnlyTheCoversTheNameTheKindWordTheCountAndTheMenu_NoSourceOrBadgeOnTheArt()
    {
        var collection = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "Cross-Media Archive",
            IsCollection = true,
            UseGroupTile = true,
            ArtworkStackItems = [new ArtworkStackItem { Id = "book-1", Title = "Book 1", ImageUrl = "/covers/book-1.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait }],
            NavigationUrl = "/details/collection/cross-media",
            MediaCounts =
            [
                new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.MenuBook, "Books", 4),
                new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Headphones, "Audiobooks", 1),
                new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Movie, "Movies", 1),
            ],
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, collection));

        // The count sits on the collage's solid band under the name, not in a label over the covers.
        Assert.Equal("6 titles", cut.Find(".group-collage__count").TextContent.Trim());
        Assert.Equal("Cross-Media Archive", cut.Find(".group-collage__name").TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-tile-label--bottom"));
        Assert.Equal("Collection", cut.Find(".media-tile-label--status").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-group-source], [data-unmatched-badge]"));
        Assert.Empty(cut.FindAll(".media-tile-fallback__title"));
        Assert.Equal("Cross-Media Archive, Collection, 4 books, 1 audiobooks, 1 movies", cut.Find("a.media-tile-link").GetAttribute("aria-label"));
        Assert.Single(cut.FindAll("[data-media-tile-actions]"));
    }

    [Fact]
    public void MediaTileShelf_CardsDoNotLoadActionState()
    {
        var items = Enumerable.Range(1, 24)
            .Select(index => new MediaTileViewModel
            {
                Id = Guid.NewGuid(),
                WorkId = Guid.NewGuid(),
                Title = $"Card {index}",
                NavigationUrl = $"/details/{index}",
            })
            .ToList();

        Render<MediaTileShelf>(parameters => parameters.Add(
            component => component.Shelf,
            new MediaTileShelfViewModel
            {
                Key = "request-count-test",
                Title = "Request count test",
                Items = items,
            }));

        Assert.Equal(0, _profileRequestCount);
        Assert.Equal(0, _managedCollectionRequestCount);
        Assert.Equal(0, _createCollectionRequestCount);
    }

    private static MediaTileViewModel CreateShelfItem(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        NavigationUrl = $"/details/{title.ToLowerInvariant()}",
    };

    private static MediaTileViewModel CreateGroupTile() => new()
    {
        Id = Guid.NewGuid(),
        CollectionId = Guid.NewGuid(),
        Title = "Foundation Series",
        MediaKind = "Book",
        Shape = MediaTileShape.Portrait,
        Presentation = MediaTilePresentation.BookSeries,
        GroupKind = MediaTileGroupKind.Series,
        SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
        NavigationUrl = "/details/bookseries/foundation",
        PrimaryNavigationUrl = "/details/bookseries/foundation",
        PrimaryActionLabel = "Open Series",
        Description = "A classic science-fiction sequence about civilization and change.",
        IsCollection = true,
        UseGroupTile = true,
        PreviewTotalCount = 2,
        HoverFacts = ["2 owned titles"],
        GroupSummary = new MediaTileGroupSummaryViewModel
        {
            OwnedCount = 2,
            KnownTotalCount = 3,
            CompletedCount = 1,
            InProgressCount = 1,
            EarliestYear = 1951,
            LatestYear = 1952,
            SequenceRange = "Books 1\u20132 owned",
            RelationshipLabel = "Ordered series",
        },
        MediaCounts = [new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.MenuBook, "Books", 2)],
        ArtworkStackItems =
        [
            new ArtworkStackItem
            {
                Id = "1",
                WorkId = Guid.NewGuid(),
                Title = "Foundation",
                ImageUrl = "/covers/1.jpg",
                MediaType = "Book",
                NavigationUrl = "/details/work/1?mode=read",
                Shape = ArtworkShape.Portrait,
                Position = "1",
                Description = "The first Foundation novel.",
                Facts = ["Isaac Asimov", "1951", "★ 4.4"],
            },
            new ArtworkStackItem
            {
                Id = "2",
                WorkId = Guid.NewGuid(),
                Title = "Foundation and Empire",
                ImageUrl = "/covers/2.jpg",
                MediaType = "Book",
                NavigationUrl = "/details/work/2?mode=read",
                Shape = ArtworkShape.Portrait,
                Position = "2",
                Description = "The second Foundation novel.",
                Facts = ["Isaac Asimov", "1952", "★ 4.5"],
            },
        ],
    };

    [Fact]
    public void MediaTile_SeriesIsALandscapeCollageOfItsCoversWithTheKindAndCount()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "Foundation Series",
            MediaKind = "Book",
            Shape = MediaTileShape.Portrait,
            Presentation = MediaTilePresentation.BookSeries,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            TileImageUrl = "/art/foundation-cover.jpg",
            NavigationUrl = "/details/bookseries/foundation",
            PrimaryNavigationUrl = "/details/bookseries/foundation",
            PrimaryActionLabel = "Open Series",
            IsCollection = true,
            GroupKind = MediaTileGroupKind.Series,
            PreviewTotalCount = 6,
            ArtworkStackItems =
            [
                new ArtworkStackItem { Id = "1", Title = "Foundation", ImageUrl = "/covers/1.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait, Position = "1" },
                new ArtworkStackItem { Id = "2", Title = "Foundation and Empire", ImageUrl = "/covers/2.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait, Position = "2" },
            ],
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        // A Series is a group of titles: a landscape collage of its member covers, the kind word at top left and the count on the name band.
        var tile = cut.Find("article.media-tile");
        Assert.Contains("is-group", tile.ClassList);
        Assert.Contains("is-landscape", tile.ClassList);
        Assert.Equal(2, cut.FindAll(".group-collage__cover img").Count);
        Assert.Empty(cut.FindAll("img.media-tile-image"));
        Assert.Equal("Book series", cut.Find(".media-tile-label--status").TextContent.Trim());
        Assert.Equal("6 books", cut.Find(".group-collage__count").TextContent.Trim());
        Assert.Equal("Foundation Series, Book series", cut.Find("a.media-tile-link").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
    }

    [Fact]
    public void MediaArtworkGroupPreview_CapsArtworkAtFourAndShowsOverflow()
    {
        var items = new List<ArtworkStackItem>
        {
            new() { Id = "1", Title = "One", ImageUrl = "/covers/1.jpg" },
            new() { Id = "2", Title = "Two", ImageUrl = "/covers/2.jpg" },
            new() { Id = "3", Title = "Three", ImageUrl = "/covers/3.jpg" },
            new() { Id = "4", Title = "Four", ImageUrl = "/covers/4.jpg" },
            new() { Id = "5", Title = "Five", ImageUrl = "/covers/5.jpg" },
        };

        var cut = Render<MediaEngine.Web.Components.Shared.MediaArtworkGroupPreview>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.TotalCount, 7));

        Assert.Equal(4, cut.FindAll(".media-artwork-group-preview__artwork").Count);
        Assert.Equal("+3", cut.Find(".media-artwork-group-preview__overflow").TextContent);
    }

    [Theory]
    [InlineData(2, "has-two")]
    [InlineData(3, "has-three")]
    [InlineData(4, "has-four")]
    public void MediaArtworkGroupPreview_StripKeepsTwoToFourCoversInOneStaticRow(
        int itemCount,
        string countClass)
    {
        var items = Enumerable.Range(1, itemCount)
            .Select(index => new ArtworkStackItem
            {
                Id = index.ToString(),
                Title = $"Title {index}",
                ImageUrl = $"/covers/{index}.jpg",
                Shape = ArtworkShape.Portrait,
            })
            .ToList();

        var cut = Render<MediaEngine.Web.Components.Shared.MediaArtworkGroupPreview>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.TotalCount, itemCount)
            .Add(component => component.Layout, MediaArtworkGroupPreviewLayout.Strip)
            .Add(component => component.ShowOverflowCount, false));

        var root = cut.Find(".media-artwork-group-preview");
        Assert.Contains("is-strip-layout", root.ClassList);
        Assert.Contains(countClass, root.ClassList);
        Assert.Equal(itemCount, cut.FindAll(".media-artwork-group-preview__artwork").Count);
        Assert.Empty(cut.FindAll(".media-artwork-group-preview__overflow"));

        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Shared/MediaArtworkGroupPreview.razor.css"));
        Assert.Contains($".media-artwork-group-preview.is-strip-layout.{countClass}", css, StringComparison.Ordinal);
        Assert.Contains("height: auto !important", css, StringComparison.Ordinal);
        Assert.Contains("align-self: center", css, StringComparison.Ordinal);
        Assert.Contains("object-fit: contain", css, StringComparison.Ordinal);
        Assert.DoesNotContain("is-mosaic-layout", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, ArtworkShape.Portrait, "has-two", "is-portrait-cluster", "shape-p2-s0-w0")]
    [InlineData(3, ArtworkShape.Portrait, "has-three", "is-portrait-cluster", "shape-p3-s0-w0")]
    [InlineData(4, ArtworkShape.Portrait, "has-four", "is-portrait-cluster", "shape-p4-s0-w0")]
    [InlineData(4, ArtworkShape.Square, "has-four", "is-square-cluster", "shape-p0-s4-w0")]
    public void MediaArtworkGroupPreview_AdaptiveUsesApprovedCountAndShapeTemplates(
        int itemCount,
        ArtworkShape shape,
        string countClass,
        string clusterClass,
        string signatureClass)
    {
        var items = Enumerable.Range(1, itemCount)
            .Select(index => new ArtworkStackItem
            {
                Id = index.ToString(),
                Title = $"Title {index}",
                ImageUrl = $"/covers/{index}.jpg",
                Shape = shape,
            })
            .ToList();

        var cut = Render<MediaEngine.Web.Components.Shared.MediaArtworkGroupPreview>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.TotalCount, itemCount)
            .Add(component => component.Layout, MediaArtworkGroupPreviewLayout.Adaptive)
            .Add(component => component.ShowOverflowCount, false));

        var root = cut.Find(".media-artwork-group-preview");
        Assert.Contains("is-adaptive-layout", root.ClassList);
        Assert.Contains(countClass, root.ClassList);
        Assert.Contains(clusterClass, root.ClassList);
        Assert.Contains(signatureClass, root.ClassList);
        Assert.Equal(itemCount, cut.FindAll(".media-artwork-group-preview__slot").Count);
        Assert.Equal(itemCount, cut.FindAll(".media-artwork-group-preview__artwork").Count);

        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Shared/MediaArtworkGroupPreview.razor.css"));
        Assert.Contains("approved count/shape matrix", css, StringComparison.Ordinal);
        Assert.Contains("aspect-ratio: auto !important", css, StringComparison.Ordinal);
        Assert.Contains("object-fit: contain", css, StringComparison.Ordinal);
        Assert.Contains("@container (max-width: 420px)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaArtworkGroupPreview_AdaptiveKeepsMissingArtworkAsShapeAwareFallback()
    {
        var cut = Render<MediaEngine.Web.Components.Shared.MediaArtworkGroupPreview>(parameters => parameters
            .Add(component => component.Items, new List<ArtworkStackItem>
            {
                new() { Id = "1", Title = "Missing book", ImageUrl = string.Empty, MediaType = "Book", Shape = ArtworkShape.Portrait },
                new() { Id = "2", Title = "Album", ImageUrl = "/covers/album.jpg", MediaType = "Music", Shape = ArtworkShape.Square },
            })
            .Add(component => component.Layout, MediaArtworkGroupPreviewLayout.Adaptive));

        Assert.Single(cut.FindAll(".media-artwork-group-preview__artwork-fallback"));
        Assert.Single(cut.FindAll(".media-artwork-group-preview__artwork"));
        Assert.Contains("shape-p1-s1-w0", cut.Find(".media-artwork-group-preview").ClassList);
    }

    [Fact]
    public void MediaTile_CardClickAlwaysOpensDetails()
    {
        var details = "/details/work/1";
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Title = "Project Hail Mary",
            MediaKind = "Book",
            Shape = MediaTileShape.Portrait,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            TileImageUrl = "/art/hail-mary.jpg",
            HoverImageUrl = "/art/hail-mary.jpg",
            NavigationUrl = details,
            DetailsNavigationUrl = details,
            PrimaryNavigationUrl = "/read/1",
            PrimaryActionLabel = "Read",
        };
        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        cut.Find(".media-tile-media").Click();
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith(details, nav.Uri, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button:not(.media-tile-action):not([hidden])"));
    }

    [Theory]
    [InlineData("Book")]
    [InlineData("Comic")]
    [InlineData("Movie")]
    [InlineData("TV")]
    public void MediaTile_AllMediaKindsOpenDetailsInsteadOfLaunchingAssetPlayer(string mediaKind)
    {
        var assetId = Guid.NewGuid();
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            AssetId = assetId,
            Title = "Playable title",
            MediaKind = mediaKind,
            Shape = mediaKind is "Movie" or "TV" ? MediaTileShape.Landscape : MediaTileShape.Portrait,
            SurfaceKind = mediaKind is "Movie" or "TV" ? MediaTileSurfaceKind.BannerLandscape : MediaTileSurfaceKind.CoverPortrait,
            TileImageUrl = "/art/playable.jpg",
            HoverImageUrl = "/art/playable.jpg",
            NavigationUrl = "/details/work",
            DetailsNavigationUrl = "/details/work",
            PrimaryNavigationUrl = "/details/work",
            PrimaryActionLabel = mediaKind is "Book" or "Comic" ? "Read" : "Play",
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        Assert.Empty(cut.FindAll("button:not(.media-tile-action):not([hidden])"));
        cut.Find(".media-tile-media").Click();

        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/details/work", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaTile_MusicCardOpensDetailsWithoutStartingPlayback()
    {
        var assetId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var item = new MediaTileViewModel
        {
            Id = workId,
            WorkId = workId,
            AssetId = assetId,
            Title = "Playable song",
            Subtitle = "Test Artist",
            MediaKind = "Music",
            Shape = MediaTileShape.Square,
            SurfaceKind = MediaTileSurfaceKind.CoverSquare,
            TileImageUrl = "/art/song.jpg",
            HoverImageUrl = "/art/song.jpg",
            NavigationUrl = "/listen/music/songs",
            DetailsNavigationUrl = "/listen/music?browse=songs&track=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            PrimaryNavigationUrl = "/listen/music/songs",
            PrimaryActionLabel = "Play",
        };
        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        Assert.Empty(cut.FindAll("button:not(.media-tile-action):not([hidden])"));
        cut.Find(".media-tile-media").Click();

        var playback = Services.GetRequiredService<PlaybackSessionController>();
        Assert.Contains("/listen/music?browse=songs&track=", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Null(playback.CurrentItem);
    }

    [Fact]
    public void MediaTile_TvEpisodeKeepsSeasonEpisodeInResumeAction()
    {
        var details = "/watch/tv/show/1?episode=3";
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Title = "Episode Three",
            MediaKind = "TV",
            Shape = MediaTileShape.Landscape,
            SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
            TileImageUrl = "/episodes/3.jpg",
            HoverImageUrl = "/episodes/3.jpg",
            NavigationUrl = details,
            DetailsNavigationUrl = details,
            PrimaryNavigationUrl = "/watch/player/3",
            PrimaryActionLabel = "Resume S1 E3",
            ProgressPct = 42,
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        Assert.Empty(cut.FindAll("button:not(.media-tile-action):not([hidden])"));
        Assert.DoesNotContain("is-cinematic-hover", cut.Find("article.media-tile").ClassList);

        cut.Find(".media-tile-media").Click();
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith(details, nav.Uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("TV")]
    public void PickUpTileResumesFromTheWholePictureAndSaysTheContentRatingNotTheStars(string kind)
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Continue title",
            MediaKind = kind,
            Shape = MediaTileShape.Landscape,
            TileImageUrl = "/episode-still.jpg",
            ProgressPct = 30,
            ContentRating = "TV-MA",
            HoverFacts = ["TV-MA", "2021", "\u2605 8.1"],
            SortYear = 2021,
            DetailsNavigationUrl = "/details/work/continue",
            PrimaryNavigationUrl = "/watch/player/continue",
        };
        var cut = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel { Items = [item] }));
        Assert.Equal("/watch/player/continue", cut.Find(".continue-tile__link").GetAttribute("href"));
        Assert.Equal("Resume Continue title", cut.Find(".continue-tile__link").GetAttribute("aria-label"));
        Assert.EndsWith("TV-MA \u00b7 2021", cut.Find(".continue-tile__line").TextContent.Trim());
        Assert.DoesNotContain("\u2605", cut.Find(".continue-tile__text").TextContent);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "registerMediaTileHover");
    }

    [Fact]
    public void PickUpEpisodeTileReadsCodeColonTitleThenTheRatingAndYear_WithThePercentAsABadge()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Night 1",
            MediaKind = "TV",
            Shape = MediaTileShape.Landscape,
            TileImageUrl = "/still.jpg",
            ProgressPct = 3,
            ContentRating = "TV-14",
            SortYear = 2019,
            EpisodeContext = new DisplayEpisodeContextDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "The Continental", "Night 1", 3, 8, DisplayContinuationState.InProgress, null, null),
            PrimaryNavigationUrl = "/watch/player/continue",
        };
        var cut = Render<ContinueWatchingOrListeningTile>(p => p.Add(c => c.Item, item));
        Assert.Equal("S3 E8: Night 1", cut.Find(".continue-tile__title").TextContent.Trim());
        Assert.Equal("Episode · TV-14 · 2019", cut.Find(".continue-tile__line").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll(".continue-tile__progress [role=progressbar]"));
        // The percent is a small badge on the picture, never in the text rows, and there is no separate code on the picture.
        Assert.Equal("3%", cut.Find(".continue-tile__frame .continue-tile__percent").TextContent.Trim());
        Assert.DoesNotContain("3%", cut.Find(".continue-tile__text").TextContent);
        Assert.Empty(cut.FindAll(".continue-tile__episode-code"));
    }

    [Theory]
    [InlineData("TV-MA", 2021, "TV-MA · 2021")]
    [InlineData(null, 2021, "2021")]
    [InlineData("PG", 0, "PG")]
    public void AContinueTileSecondRow_IsTheContentRatingThenTheYear_OrJustTheYear_NeverStars(string? rating, int year, string expected)
    {
        foreach (var kind in new[] { "Movie", "Book", "Audiobook", "Music" })
        {
            var item = new MediaTileViewModel
            {
                Id = Guid.NewGuid(),
                Title = "Row check",
                MediaKind = kind,
                CoverUrl = "/cover.jpg",
                ProgressPct = 20,
                ContentRating = rating,
                SortYear = year,
                HoverFacts = ["★ 4.3"],
                PrimaryNavigationUrl = "/play/continue",
            };

            var cut = Render<ContinueWatchingOrListeningTile>(p => p.Add(c => c.Item, item));

            Assert.EndsWith(expected, cut.Find(".continue-tile__line").TextContent.Trim());
            Assert.DoesNotContain("★", cut.Find(".continue-tile__text").TextContent);
            Assert.DoesNotContain("%", cut.Find(".continue-tile__text").TextContent);
        }
    }

    [Fact]
    public void AContinueTileWithNoLandscapePicture_ShowsTheWholeCoverCentered_AndSwapsToThePictureWhenItArrives()
    {
        MediaTileViewModel Movie(string? background) => new()
        {
            Id = Guid.NewGuid(),
            Title = "Swap",
            MediaKind = "Movie",
            CoverUrl = "/poster.jpg",
            BackgroundUrl = background,
            ProgressPct = 10,
            PrimaryNavigationUrl = "/watch/player/swap",
        };

        var cut = Render<ContinueWatchingOrListeningTile>(p => p.Add(c => c.Item, Movie(null)));

        Assert.Single(cut.FindAll("img.continue-tile__cover"));
        Assert.Single(cut.FindAll("img.continue-tile__soft"));
        Assert.Empty(cut.FindAll("img.continue-tile__backdrop"));

        cut.Render(p => p.Add(c => c.Item, Movie("/landscape.jpg")));

        Assert.Single(cut.FindAll("img.continue-tile__backdrop"));
        Assert.Empty(cut.FindAll("img.continue-tile__cover"));
    }

    [Fact]
    public void AnAlbumOrAudiobookContinueTileWithNoLandscapePicture_ShowsASquareCoverAtAboutHalfTheWidth()
    {
        foreach (var kind in new[] { "Music", "Audiobook" })
        {
            var item = new MediaTileViewModel
            {
                Id = Guid.NewGuid(),
                Title = "Square",
                MediaKind = kind,
                Shape = MediaTileShape.Square,
                CoverUrl = "/square.jpg",
                ProgressPct = 30,
                PrimaryNavigationUrl = "/listen/player/square",
            };

            var cut = Render<ContinueWatchingOrListeningTile>(p => p.Add(c => c.Item, item));

            Assert.Contains("is-square", cut.Find("img.continue-tile__cover").ClassList);
        }

        var portrait = new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Tall", MediaKind = "Book", CoverUrl = "/tall.jpg", ProgressPct = 30, PrimaryNavigationUrl = "/read/tall" };
        Assert.DoesNotContain("is-square", Render<ContinueWatchingOrListeningTile>(p => p.Add(c => c.Item, portrait)).Find("img.continue-tile__cover").ClassList);
    }

    [Fact]
    public void PickUpBookTileSaysItsYearOnTheSecondRow_NeverStarsOrTheAuthorOrTheProgress()
    {
        MediaTileViewModel Book(params string[] facts) => new()
        {
            Id = Guid.NewGuid(),
            Title = "Dune",
            MediaKind = "Book",
            Creator = "Frank Herbert",
            Shape = MediaTileShape.Portrait,
            CoverUrl = "/cover.jpg",
            SortYear = 1965,
            ProgressPct = 60,
            HoverFacts = facts,
            PrimaryNavigationUrl = "/read/continue",
        };
        var cut = Render<ContinueWatchingOrListeningTile>(p => p.Add(c => c.Item, Book("Frank Herbert", "\u2605 4.3")));
        Assert.Equal("Book · 1965", cut.Find(".continue-tile__line").TextContent.Trim());
        Assert.Equal("60%", cut.Find(".continue-tile__percent").TextContent.Trim());
        Assert.Empty(cut.FindAll(".continue-tile__progress-text"));
        // With no landscape picture the whole cover shows centered on the soft wash (never cropped, never pushed left).
        Assert.NotEmpty(cut.FindAll("img.continue-tile__cover"));
        Assert.NotEmpty(cut.FindAll(".continue-tile__soft"));
    }

    [Fact]
    public void ContinueKeepsAccessibleProgressWithoutVisibleBarLabel()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Continue movie",
            MediaKind = "Movie",
            Shape = MediaTileShape.Landscape,
            TileImageUrl = "/background.jpg",
            ProgressPct = 41,
            ContinuationState = MediaEngine.Contracts.Display.DisplayContinuationState.InProgress,
            DetailsNavigationUrl = "/details/work/continue"
        };
        var cut = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel { Items = [item] }));
        Assert.Equal("41", cut.Find("[role=progressbar]").GetAttribute("aria-valuenow"));
        var lane = Render<ContinueWatchingOrListeningTile>(p => p.Add(c => c.Item, item));
        Assert.Equal("41", lane.Find("[role=progressbar]").GetAttribute("aria-valuenow"));
        Assert.Contains("continue-tile", lane.Find("article").ClassList);
        // The title is always on the tile, even with no logo or backdrop (blank-tile fix).
        Assert.Equal("Continue movie", lane.Find(".continue-tile__title").TextContent);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
