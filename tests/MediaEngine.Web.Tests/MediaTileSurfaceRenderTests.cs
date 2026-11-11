using Bunit;
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
    public void PickUpRowIsOneMixedRowOfCompactCards()
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
        var cards = cut.FindAll(".pickup-card");
        Assert.Equal(3, cards.Count);
        Assert.Contains("Read", cards.Single(c => c.TextContent.Contains("Continue Book")).QuerySelector(".pickup-kind")!.TextContent);
        Assert.Contains("Watch", cards.Single(c => c.TextContent.Contains("Continue TV")).QuerySelector(".pickup-kind")!.TextContent);
        Assert.Contains("Listen", cards.Single(c => c.TextContent.Contains("Continue Audiobook")).QuerySelector(".pickup-kind")!.TextContent);
        Assert.Contains("Fixture creator", cards[0].TextContent);
        Assert.Contains("42% complete", cards[0].TextContent);
        Assert.Equal("42", cut.Find("[role=progressbar]").GetAttribute("aria-valuenow"));
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
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
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
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
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
    public void MediaTileGrid_RoutesOnlyGroupsToTheGroupTileAndSinglesToTheItemTile()
    {
        var group = CreateGroupTile();
        var ordinary = CreateShelfItem("Ordinary title");

        var cut = Render<MediaTileGrid>(parameters => parameters.Add(
            component => component.Items,
            [group, ordinary]));

        Assert.Same(group, cut.FindComponent<MediaGroupTile>().Instance.Item);
        Assert.Single(cut.FindAll(".media-group-tile"));
        Assert.Single(cut.FindAll("article.media-tile"));
    }

    [Fact]
    public void MediaTileGrid_RoutesEveryNonTvCollectionToTheGroupTile()
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

        Assert.Equal("Cross-media universe", cut.FindComponent<MediaGroupTile>().Instance.Item.Title);
        Assert.Single(cut.FindAll(".media-group-tile"));
        Assert.Single(cut.FindAll("article.media-tile"));
    }

    [Theory]
    [InlineData(MediaTileGroupKind.Collection, "Collection")]
    [InlineData(MediaTileGroupKind.Series, "Series")]
    [InlineData(MediaTileGroupKind.Universe, "Universe")]
    [InlineData(MediaTileGroupKind.List, "List")]
    [InlineData(MediaTileGroupKind.Set, "Set")]
    public void MediaGroupTile_CornerWordNamesTheKindOfGroup(MediaTileGroupKind kind, string word)
    {
        var group = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Group",
            IsCollection = true,
            UseLandscapeGroupTile = true,
            GroupKind = kind,
            NavigationUrl = "/details/collection/x",
        };

        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, group));

        Assert.Equal(word, cut.Find(".media-group-tile__kind").TextContent.Trim());
    }

    [Fact]
    public void MediaGroupTile_CustomArtworkReplacesTheCoverStack()
    {
        var group = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Halloween",
            IsCollection = true,
            UseLandscapeGroupTile = true,
            HasCustomArtwork = true,
            CoverUrl = "/art/halloween.jpg",
            NavigationUrl = "/details/collection/halloween",
            ArtworkStackItems = [new ArtworkStackItem { Id = "1", Title = "Film", ImageUrl = "/covers/film.jpg", MediaType = "Movie", Shape = ArtworkShape.Portrait }],
        };

        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, group));

        Assert.Single(cut.FindAll(".media-group-tile__custom"));
        Assert.Empty(cut.FindAll(".media-group-tile__stack"));
    }

    [Fact]
    public void MediaTile_DrawsAPersonGroupAsTheRoundPersonTile()
    {
        var personId = Guid.NewGuid();
        var group = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Ava DuVernay",
            NavigationUrl = $"/details/person/{personId:D}",
            IsCollection = true,
            UseLandscapeGroupTile = true,
            MediaCounts = [new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Movie, "Movies", 2)],
            Person = new MediaTilePersonViewModel { Id = personId, Name = "Ava DuVernay", ImageUrl = $"/persons/{personId:D}/headshot", Roles = ["Director"] },
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, group));

        Assert.Single(cut.FindAll("a.person-tile"));
        Assert.Equal($"/details/person/{personId:D}", cut.Find("a.person-tile").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".media-group-tile"));
        Assert.Equal("2 movies", cut.Find(".media-count-line__item").ParentElement!.GetAttribute("data-playback-tooltip"));
    }

    [Fact]
    public void MediaSquareTile_LaysOutAMosaicOfMemberCoversWhenThereIsNoCover()
    {
        var playlist = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Mix",
            IsPlaylist = true,
            NavigationUrl = "/listen/music/playlists/2",
            PreviewImages = ["/c/1.jpg", "/c/2.jpg", "/c/3.jpg", "/c/4.jpg"],
        };

        var cut = Render<MediaSquareTile>(parameters => parameters.Add(component => component.Item, playlist));

        Assert.Equal(4, cut.FindAll(".media-square-tile__mosaic img").Count);
    }

    [Fact]
    public void MediaGroupTile_NetworkGroupShowsItsResolvedLogoWithoutChangingTheGroupRoute()
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
            UseLandscapeGroupTile = true,
        };

        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, group));

        Assert.Equal("/images/brands/hbo.svg", cut.Find(".media-group-tile__brand").GetAttribute("src"));
        Assert.Equal("/watch/tv?browse=networks&network=HBO", cut.Find("a.media-group-tile__link").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".media-group-tile__stack"));
    }

    [Fact]
    public void MediaGroupTile_FansTheFirstThreeCoversInTheOrderTheEngineSent()
    {
        var collection = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Cross-Media Archive",
            IsCollection = true,
            UseLandscapeGroupTile = true,
            NavigationUrl = "/details/collection/cross-media",
            MediaCounts =
            [
                new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.MenuBook, "Books", 4),
                new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Headphones, "Audiobooks", 1),
                new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Movie, "Movies", 1),
            ],
            ArtworkStackItems =
            [
                new ArtworkStackItem { Id = "book-1", Title = "Book 1", ImageUrl = "/covers/book-1.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait },
                new ArtworkStackItem { Id = "album", Title = "Album", ImageUrl = "/covers/album.jpg", MediaType = "Music", Shape = ArtworkShape.Square },
                new ArtworkStackItem { Id = "movie", Title = "Movie", ImageUrl = "/covers/movie.jpg", MediaType = "Movie", Shape = ArtworkShape.Portrait },
                new ArtworkStackItem { Id = "book-2", Title = "Book 2", ImageUrl = "/covers/book-2.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait },
            ],
        };

        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, collection));
        var sources = cut.FindAll(".media-group-tile__cover").Select(image => image.GetAttribute("src")).ToList();

        Assert.Equal(3, sources.Count);
        Assert.Contains("book-1.jpg", sources[0], StringComparison.Ordinal);
        Assert.Contains("album.jpg", sources[1], StringComparison.Ordinal);
        Assert.Contains("movie.jpg", sources[2], StringComparison.Ordinal);
        Assert.Equal("6 titles", cut.Find(".media-tile-label--bottom").TextContent.Trim());
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
        Shape = MediaTileShape.Landscape,
        Presentation = MediaTilePresentation.BookSeries,
        GroupKind = MediaTileGroupKind.Series,
        SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
        NavigationUrl = "/details/bookseries/foundation",
        PrimaryNavigationUrl = "/details/bookseries/foundation",
        PrimaryActionLabel = "Open Series",
        Description = "A classic science-fiction sequence about civilization and change.",
        IsCollection = true,
        UseLandscapeGroupTile = true,
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
    public void MediaTile_PortraitSeriesUsesStaticShapeAwarePreview()
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
            HoverLayout = MediaTileHoverLayout.BannerPopover,
            TileImageUrl = "/art/foundation-bg.jpg",
            HoverImageUrl = "/art/foundation-bg.jpg",
            NavigationUrl = "/details/bookseries/foundation",
            PrimaryNavigationUrl = "/details/bookseries/foundation",
            PrimaryActionLabel = "Open Series",
            IsCollection = true,
            ArtworkStackItems =
            [
                new ArtworkStackItem { Id = "1", Title = "Foundation", ImageUrl = "/covers/1.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait, Position = "1" },
                new ArtworkStackItem { Id = "2", Title = "Foundation and Empire", ImageUrl = "/covers/2.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait, Position = "2" },
            ],
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        // A series is a group of titles: it draws as the one group tile, not as an item tile with a series look.
        Assert.Single(cut.FindAll(".media-group-tile"));
        Assert.Equal(2, cut.FindAll(".media-group-tile__cover").Count);
        Assert.Equal("Series", cut.Find(".media-group-tile__kind").TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-tile"));
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
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
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
            HoverLayout = mediaKind is "Movie" or "TV" ? MediaTileHoverLayout.BannerPopover : MediaTileHoverLayout.ArtOnlyPopover,
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
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
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
            HoverLayout = MediaTileHoverLayout.BannerPopover,
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
    public void PickUpCardResumesFromThePlayButtonAndOpensDetailsFromTheTitle(string kind)
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Continue title",
            MediaKind = kind,
            Shape = MediaTileShape.Landscape,
            TileImageUrl = "/episode-still.jpg",
            ProgressPct = 30,
            DetailsNavigationUrl = "/details/work/continue",
            PrimaryNavigationUrl = "/watch/player/continue",
        };
        var cut = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel { Items = [item] }));
        Assert.Equal("/watch/player/continue", cut.Find(".pickup-play").GetAttribute("href"));
        Assert.Equal("Resume Continue title", cut.Find(".pickup-play").GetAttribute("aria-label"));
        Assert.Equal("/details/work/continue", cut.Find(".pickup-title").GetAttribute("href"));
        Assert.Contains("Watch", cut.Find(".pickup-kind").TextContent);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "registerMediaTileHover");
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
        Assert.Contains("41% complete", cut.Find(".pickup-progress-text").TextContent);
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
