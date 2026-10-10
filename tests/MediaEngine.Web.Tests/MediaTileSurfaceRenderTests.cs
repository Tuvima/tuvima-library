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

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    public void ContinueGroupsKeepMediaPartitionedAndOmitEmptySections(bool mixed, int expectedGroups)
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
            HoverMode = MediaTileHoverMode.GlowOnly
        };
        var items = new List<MediaTileViewModel> { Item("Book", MediaTileShape.Portrait) };
        if (mixed) { items.Add(Item("TV", MediaTileShape.Landscape)); items.Add(Item("Audiobook", MediaTileShape.Square)); }
        var cut = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel { Items = items }));
        Assert.Equal(expectedGroups, cut.FindAll(".continue-group").Count);
        Assert.Single(cut.FindAll(".continue-reading article"));
        if (mixed)
        {
            Assert.Single(cut.FindAll(".continue-watching article.is-landscape"));
            Assert.Single(cut.FindAll(".continue-listening article.is-square"));
        }
        else
        {
            Assert.Empty(cut.FindAll(".continue-watching")); Assert.Empty(cut.FindAll(".continue-listening"));
        }
        Assert.Equal(items.Count, cut.FindAll("article a").Count);
        Assert.Empty(cut.FindAll(".media-tile-progress-caption"));
        Assert.DoesNotContain("42%", cut.Find(".continue-reading article").TextContent);
        Assert.Contains("Fixture creator", cut.Find(".continue-reading article").TextContent);
    }

    [Fact]
    public void EpisodeCardHasOneDetailLinkVisibleIdentityAndAccessibleSavedProgress()
    {
        var show = Guid.NewGuid(); var episode = Guid.NewGuid(); var asset = Guid.NewGuid();
        var item = new MediaTileViewModel
        {
            Id = episode,
            WorkId = episode,
            AssetId = asset,
            Title = "The episode",
            Subtitle = "S2 E5",
            MediaKind = "TV",
            Subject = MediaEngine.Contracts.Display.DisplaySubjectKind.TvEpisode,
            ContinuationState = MediaEngine.Contracts.Display.DisplayContinuationState.InProgress,
            Shape = MediaTileShape.Landscape,
            SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
            TileImageUrl = "/episode-small.jpg",
            HoverImageUrl = "/episode-medium.jpg",
            BackgroundUrl = "/episode-medium.jpg",
            HoverLayout = MediaTileHoverLayout.BannerPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            Description = "This episode's short synopsis.",
            ProgressPct = 42,
            RemainingSeconds = 600,
            DetailsNavigationUrl = MediaEngine.Contracts.Details.TvEpisodeDetailRoute.Build(show, episode, "watch"),
        };
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, item).Add(c => c.IsHomeSurface, true).Add(c => c.ShowCompactCaption, true));
        var link = Assert.Single(cut.FindAll("a")); Assert.Equal(item.DetailsNavigationUrl, link.GetAttribute("href"));
        Assert.Contains("S2 E5", cut.Find(".media-tile-episode-caption").TextContent);
        Assert.Empty(cut.FindAll(".media-tile-progress-caption"));
        Assert.Contains("10 min remaining", cut.Find("[role=progressbar]").GetAttribute("aria-valuetext"));
        Assert.Empty(cut.FindAll(".media-tile-hover-description"));
        Assert.Same(link, cut.Find(".media-tile-episode-caption").Closest("a"));
        link.Click();
        Assert.EndsWith(item.DetailsNavigationUrl, Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
        Assert.DoesNotContain("button", cut.Find("article").InnerHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MediaTile_ReadCardKeepsItsRestingGeometryAndShowsNoHoverText()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Title = "Leviathan Wakes",
            Subtitle = "The Expanse, Book 1",
            Description = "This belongs on the detail page only.",
            HoverFacts = ["James S. A. Corey", "M", "2011", "592 pages", "★ 4.3"],
            MediaKind = "Book",
            Shape = MediaTileShape.Portrait,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            TileImageUrl = "/art/leviathan-wakes.jpg",
            HoverImageUrl = "/art/leviathan-wakes.jpg",
            NavigationUrl = "/book/1",
            DetailsNavigationUrl = "/book/1",
            PrimaryNavigationUrl = "/read/1",
            PrimaryActionLabel = "Read",
            ProgressPct = 42,
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        Assert.Equal("Leviathan Wakes", cut.Find(".media-tile").GetAttribute("aria-label"));
        Assert.NotEmpty(cut.FindAll(".media-tile-image"));
        Assert.Empty(cut.FindAll(".media-tile-caption"));
        Assert.Empty(cut.FindAll(".media-tile-badge"));
        Assert.Empty(cut.FindAll(".media-tile-quality-badge"));
        Assert.Empty(cut.FindAll(".media-tile-source-badge"));
        Assert.Empty(cut.FindAll(".media-tile-progress-strip"));
        Assert.Empty(cut.FindAll(".media-tile-logo"));
        Assert.NotEmpty(cut.FindAll("div[style*='display: contents']"));
        Assert.Contains("--media-tile-hover-image", cut.Markup);
        Assert.Contains("is-hover-glow-only", cut.Find("article.media-tile").ClassList);
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
        Assert.Empty(cut.FindAll(".media-tile-static-hover"));
        Assert.Empty(cut.FindAll(".media-tile-hover-identity-strip"));
        Assert.DoesNotContain(item.Description, cut.Markup);
        Assert.Empty(cut.FindAll("button"));
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "registerMediaTileHover");
    }

    [Fact]
    public void MediaTileGrid_WatchCardsOnlyHighlightCoverArt()
    {
        var movie = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Title = "The Shining",
            Description = "This cinematic copy belongs on Home, Discover, and details.",
            MediaKind = "Movie",
            Shape = MediaTileShape.Portrait,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            HoverLayout = MediaTileHoverLayout.BannerPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            TileImageUrl = "/art/the-shining-cover.jpg",
            HoverImageUrl = "/art/the-shining-background.jpg",
            HeroBackgroundImageUrl = "/art/the-shining-background.jpg",
            NavigationUrl = "/watch/movie/the-shining",
            DetailsNavigationUrl = "/watch/movie/the-shining",
        };

        var cut = Render<MediaTileGrid>(parameters => parameters.Add(component => component.Items, [movie]));
        var tile = cut.FindComponent<MediaTile>();

        Assert.Equal(MediaTileHoverMode.GlowOnly, tile.Instance.HoverMode);
        Assert.Contains("is-hover-glow-only", cut.Find("article.media-tile").ClassList);
        Assert.DoesNotContain("is-cinematic-hover", cut.Find("article.media-tile").ClassList);
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
        Assert.Empty(cut.FindAll(".media-tile-static-hover"));
        Assert.Empty(cut.FindAll(".media-tile-hover-identity-strip"));
        Assert.DoesNotContain(movie.Description, cut.Markup);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "registerMediaTileHover");
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
        HoverMode = MediaTileHoverMode.GlowOnly,
        NavigationUrl = $"/details/audiobook/{workId:D}",
        DetailsNavigationUrl = $"/details/audiobook/{workId:D}",
        PrimaryNavigationUrl = resumeUrl,
        ProgressPct = progress,
    };

    [Fact]
    public void MediaTileGrid_CanRenderCompactCaptionAndAspectSafeUserSizing()
    {
        var movie = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Title = "All of Us Strangers",
            Subtitle = "Andrew Scott",
            SortYear = 2023,
            MediaKind = "Movie",
            Shape = MediaTileShape.Portrait,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            TileImageUrl = "/art/all-of-us-strangers.jpg",
            NavigationUrl = "/watch/movie/all-of-us-strangers",
            DetailsNavigationUrl = "/watch/movie/all-of-us-strangers",
        };

        var cut = Render<MediaTileGrid>(parameters => parameters
            .Add(component => component.Items, [movie])
            .Add(component => component.ShowCompactCaptions, true)
            .Add(component => component.TileSizePx, 168));

        var grid = cut.Find(".media-tile-grid");
        Assert.Contains("is-size-controlled", grid.ClassList);
        Assert.Contains("media-tile-grid--size-168", grid.ClassList);
        Assert.Null(grid.GetAttribute("style"));
        Assert.Equal("All of Us Strangers", cut.Find(".media-tile-caption__title").TextContent);
        Assert.Empty(cut.FindAll(".media-tile-caption__subtitle"));
        Assert.Equal("2023", cut.Find(".media-tile-caption__year").TextContent);
        Assert.True(cut.FindComponent<MediaTile>().Instance.ShowCompactCaption);
    }

    [Fact]
    public void MediaTile_UsesCanonicalTvShowYearRangeInCompactCaption()
    {
        var show = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "The Wire",
            Subtitle = "TV show in The Wire",
            SortYear = 2002,
            MediaKind = "TV",
            Shape = MediaTileShape.Portrait,
            Presentation = MediaTilePresentation.TvSeries,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            TileImageUrl = "/art/the-wire.jpg",
            NavigationUrl = "/details/tvshow/the-wire",
            GroupSummary = new MediaTileGroupSummaryViewModel
            {
                EarliestYear = 2002,
                LatestYear = 2008,
            },
        };

        var cut = Render<MediaTile>(parameters => parameters
            .Add(component => component.Item, show)
            .Add(component => component.ShowCompactCaption, true));

        Assert.Equal("2002\u20132008", cut.Find(".media-tile-caption__year").TextContent);
        Assert.Empty(cut.FindAll(".media-tile-caption__subtitle"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MediaTile_ShowsStillBeingMatchedMarkerOnlyWhileSettling(bool isSettling)
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Dune",
            MediaKind = "Book",
            Shape = MediaTileShape.Portrait,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            TileImageUrl = "/art/dune.jpg",
            NavigationUrl = "/book/dune",
            DetailsNavigationUrl = "/book/dune",
            IsSettling = isSettling,
        };

        var cut = Render<MediaTile>(parameters => parameters
            .Add(component => component.Item, item)
            .Add(component => component.ShowCompactCaption, true));

        var dots = cut.FindAll(".media-tile-caption__title .media-tile-settling-dot");
        Assert.Equal(isSettling, dots.Count == 1);
        Assert.Equal(isSettling, cut.Markup.Contains("Still being matched", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".media-tile-frame .media-tile-settling-dot"));
        Assert.Empty(cut.FindAll("[aria-live]"));
        if (isSettling)
        {
            Assert.Equal("Still being matched", dots[0].GetAttribute("title"));
            Assert.Equal("Still being matched", cut.Find(".media-tile-settling-dot__label").TextContent);
            Assert.Contains("Still being matched", cut.Find("a.media-tile-link").GetAttribute("aria-label"));
        }
    }

    [Theory]
    [InlineData("compact", ".media-tile-caption__title .media-tile-settling-dot")]
    [InlineData("no-caption", ".media-tile-frame > .media-tile-settling-dot.is-overlay")]
    [InlineData("custom-caption", ".media-tile-frame > .media-tile-settling-dot.is-overlay")]
    [InlineData("episode", ".media-tile-episode-caption strong .media-tile-settling-dot")]
    public void MediaTile_SettlingDot_AppearsExactlyOnceInEveryCaptionVariant(string variant, string dotSelector)
    {
        var episode = variant == "episode";
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = episode ? "Pilot" : "Discovery",
            MediaKind = episode ? "TV" : "Music",
            Subject = episode ? MediaEngine.Contracts.Display.DisplaySubjectKind.TvEpisode : MediaEngine.Contracts.Display.DisplaySubjectKind.Album,
            Shape = episode ? MediaTileShape.Portrait : MediaTileShape.Square,
            SurfaceKind = episode ? MediaTileSurfaceKind.CoverPortrait : MediaTileSurfaceKind.CoverSquare,
            TileImageUrl = "/art/x.jpg",
            NavigationUrl = "/details/x",
            DetailsNavigationUrl = "/details/x",
            IsSettling = true,
            EpisodeContext = episode ? new MediaEngine.Contracts.Display.DisplayEpisodeContextDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "The Show", "Pilot", 1, 1, MediaEngine.Contracts.Display.DisplayContinuationState.Unstarted, null, null) : null,
        };

        var cut = Render<MediaTile>(parameters =>
        {
            parameters.Add(component => component.Item, item);
            parameters.Add(component => component.ShowCompactCaption, variant == "compact");
            if (variant == "custom-caption")
            {
                parameters.Add(component => component.CaptionContent, (Microsoft.AspNetCore.Components.RenderFragment)(builder => builder.AddContent(0, "Custom")));
            }
        });

        Assert.Single(cut.FindAll(dotSelector));
        Assert.Single(cut.FindAll(".media-tile-settling-dot"));
        Assert.Contains("Still being matched", cut.Find("a.media-tile-link").GetAttribute("aria-label"));
    }

    [Fact]
    public void MediaTileGrid_CanHideRedundantTvShowGroupIndicator()
    {
        var show = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "The Expanse",
            MediaKind = "TV",
            Shape = MediaTileShape.Portrait,
            Presentation = MediaTilePresentation.TvSeries,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            IsCollection = true,
            TileImageUrl = "/art/the-expanse.jpg",
            NavigationUrl = "/watch/tv/the-expanse",
        };

        var cut = Render<MediaTileGrid>(parameters => parameters
            .Add(component => component.Items, [show])
            .Add(component => component.HideGroupIndicators, true));

        Assert.Empty(cut.FindAll(".media-tile-group-indicators"));
        Assert.True(cut.FindComponent<MediaTile>().Instance.HideGroupIndicators);
    }

    [Fact]
    public void MediaTile_DetailsSurfaceIsSemanticLinkWithVisibleKeyboardFocus()
    {
        const string details = "/book/semantic-card";
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
            HoverMode = MediaTileHoverMode.Expanded,
            TileImageUrl = "/art/saga-three.jpg",
            NavigationUrl = "/comic/saga-three",
            DetailsNavigationUrl = "/comic/saga-three",
            HoverFacts = ["Image", "2012", "144 pages"],
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));
        Assert.Empty(cut.FindAll(".media-tile-hover-identity-strip"));
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
        Assert.Empty(cut.FindAll("button"));
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
    public void MediaTileGrid_RoutesOnlyOptedInGroupsToDedicatedLandscapeComponent()
    {
        var group = CreateGroupTile();
        var ordinary = CreateShelfItem("Ordinary title");

        var cut = Render<MediaTileGrid>(parameters => parameters.Add(
            component => component.Items,
            [group, ordinary]));

        var renderedGroup = cut.FindComponent<MediaGroupTile>();
        var renderedOrdinary = cut.FindComponent<MediaTile>();

        Assert.Same(group, renderedGroup.Instance.Item);
        Assert.Same(ordinary, renderedOrdinary.Instance.Item);
        Assert.Single(cut.FindAll(".media-group-tile"));
        Assert.Single(cut.FindAll(".media-tile"));
    }

    [Fact]
    public void MediaTileGrid_RoutesEveryNonTvCollectionToTheDedicatedGroupTile()
    {
        var collection = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "Cross-media universe",
            IsCollection = true,
            Presentation = MediaTilePresentation.Default,
            NavigationUrl = "/collection/cross-media",
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

        Assert.Single(cut.FindComponents<MediaGroupTile>());
        Assert.Single(cut.FindComponents<MediaTile>());
        Assert.Equal("Cross-media universe", cut.FindComponent<MediaGroupTile>().Instance.Item.Title);
        Assert.Equal("Owned TV show", cut.FindComponent<MediaTile>().Instance.Item.Title);
    }

    [Fact]
    public void MediaTileGrid_UsesTheUnifiedRestedIdentityForGroupTiles()
    {
        var group = CreateGroupTile();
        var cut = Render<MediaTileGrid>(parameters => parameters.Add(component => component.Items, [group]));
        var renderedGroup = cut.FindComponent<MediaGroupTile>();

        Assert.Equal(MediaTileHoverMode.GlowOnly, renderedGroup.Instance.HoverMode);
        Assert.Contains("is-interactive", cut.Find("article.media-group-tile").ClassList);
        Assert.Equal("Foundation Series", cut.Find(".media-group-tile__headline h3").TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-group-tile__overlay"));
    }

    [Fact]
    public void MediaGroupTile_RestedIdentityShowsYearSpanAndIconCounts()
    {
        var group = CreateGroupTile();
        var cut = Render<MediaGroupTile>(parameters => parameters
            .Add(component => component.Item, group)
            .Add(component => component.HoverMode, MediaTileHoverMode.GlowOnly));

        Assert.Equal("Foundation Series", cut.Find(".media-group-tile__headline h3").TextContent.Trim());
        Assert.Equal("1951\u20131952", cut.Find(".media-group-tile__year").TextContent.Trim());
        Assert.Equal("2 books", cut.Find(".media-group-tile__media-count").GetAttribute("title"));
        Assert.DoesNotContain("A classic science-fiction sequence about civilization and change.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".media-group-tile__description"));
        Assert.Single(cut.FindAll(".media-group-tile__open-cue"));
        Assert.Empty(cut.FindAll(".media-group-tile__overlay"));
        Assert.Empty(cut.FindAll(".media-group-tile__kind"));
        Assert.Empty(cut.FindAll(".media-group-tile__caption"));

        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/MediaTiles/MediaGroupTile.razor.css"));
        Assert.Contains("text-transform: uppercase", css, StringComparison.Ordinal);
        Assert.Contains(".media-group-tile__headline ::deep .media-group-tile__mark", css, StringComparison.Ordinal);
        Assert.Contains("var(--tl-status-warning)", css, StringComparison.Ordinal);
        Assert.Contains(".media-group-tile__artwork", css, StringComparison.Ordinal);
        Assert.Contains("inset: clamp(12px, 2.4cqw, 18px);", css, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaGroupTile_IsOneArtworkLedLinkWithAStableHoverCue()
    {
        var group = CreateGroupTile();
        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, group));
        var root = cut.Find("article.media-group-tile");

        Assert.Contains(root.Attributes, attribute => attribute.Name.StartsWith("b-", StringComparison.Ordinal));
        Assert.Equal("Foundation Series", cut.Find(".media-group-tile__headline h3").TextContent.Trim());
        Assert.Equal("Open Series", cut.Find(".media-group-tile__open-cue").TextContent.Trim());
        Assert.Single(cut.FindAll(".media-artwork-group-preview.is-cluster-layout"));
        Assert.Single(cut.FindAll("a.media-group-tile__surface"));
        Assert.Empty(cut.FindAll(".media-group-tile__base-copy"));
        Assert.Empty(cut.FindAll(".media-group-tile__description"));
        Assert.Empty(cut.FindAll(".media-group-tile__overview"));
        Assert.Empty(cut.FindAll(".media-artwork-group-preview.is-mosaic-layout"));
        Assert.Empty(cut.FindAll(".media-tile"));
        Assert.Empty(cut.FindAll("button"));
        Assert.Equal("/details/bookseries/foundation", cut.Find("a.media-group-tile__surface").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".media-group-tile__item-action"));
        Assert.Empty(cut.FindAll(".media-artwork-carousel"));
        Assert.Empty(cut.FindAll("button[aria-label^='Next']"));

        cut.Find("a.media-group-tile__surface").Click();
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/details/bookseries/foundation", nav.Uri, StringComparison.Ordinal);

        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/MediaTiles/MediaGroupTile.razor.css"));
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/MediaTiles/MediaGroupTile.razor"));
        Assert.Contains("--media-group-tile-width: clamp(560px, 40vw, 740px)", css, StringComparison.Ordinal);
        Assert.Contains("--media-group-tile-height: clamp(300px, 20vw, 365px)", css, StringComparison.Ordinal);
        Assert.Contains("inset: 0", css, StringComparison.Ordinal);
        Assert.Contains("MediaArtworkGroupPreviewLayout.Cluster", source, StringComparison.Ordinal);
        Assert.Contains("The API orders series previews by progress", source, StringComparison.Ordinal);
        Assert.Contains(".media-group-tile.is-interactive .media-group-tile__surface:hover", css, StringComparison.Ordinal);
        Assert.Contains(".media-group-tile__surface:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("-webkit-line-clamp: 2", css, StringComparison.Ordinal);
        Assert.Contains("@media (hover: none), (pointer: coarse)", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
        Assert.DoesNotContain("media-group-tile__highlights", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".media-group-tile:hover {\n    transform:", css.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void MediaGroupTile_PersonCollectionShowsPortraitAndOpensPersonDetail()
    {
        var personId = Guid.NewGuid();
        var group = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "Ava DuVernay Collection",
            MediaKind = "Collection",
            Shape = MediaTileShape.Landscape,
            Presentation = MediaTilePresentation.Default,
            SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
            HoverMode = MediaTileHoverMode.Expanded,
            NavigationUrl = $"/details/person/{personId:D}",
            DetailsNavigationUrl = $"/details/person/{personId:D}",
            IsCollection = true,
            UseLandscapeGroupTile = true,
            PreviewTotalCount = 2,
            GroupSummary = new MediaTileGroupSummaryViewModel
            {
                OwnedCount = 2,
                EarliestYear = 2014,
                LatestYear = 2023,
            },
            MediaCounts = [new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Movie, "Movies", 2)],
            Person = new MediaTilePersonViewModel
            {
                Id = personId,
                Name = "Ava DuVernay",
                ImageUrl = $"/persons/{personId:D}/headshot",
                Roles = ["Director", "Producer"],
            },
            ArtworkStackItems =
            [
                new ArtworkStackItem { Id = "1", Title = "Selma", ImageUrl = "/covers/selma.jpg", MediaType = "Movie", Shape = ArtworkShape.Portrait },
                new ArtworkStackItem { Id = "2", Title = "Origin", ImageUrl = "/covers/origin.jpg", MediaType = "Movie", Shape = ArtworkShape.Portrait },
            ],
        };

        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, group));

        Assert.Contains("has-person", cut.Find("article.media-group-tile").ClassList);
        Assert.Equal($"/persons/{personId:D}/headshot", cut.Find(".media-group-tile__person img").GetAttribute("src"));
        Assert.Equal("Director • Producer", cut.Find(".media-group-tile__person-context").TextContent.Trim());
        Assert.Equal("Open Person", cut.Find(".media-group-tile__open-cue").TextContent.Trim());
        Assert.Equal($"/details/person/{personId:D}", cut.Find("a.media-group-tile__surface").GetAttribute("href"));
        Assert.Equal(2, cut.FindAll(".media-artwork-group-preview__artwork").Count);

        cut.Find("a.media-group-tile__surface").Click();
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith($"/details/person/{personId:D}", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaGroupTile_NetworkGroupShowsItsResolvedLogoWithoutChangingTheGroupRoute()
    {
        var group = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "HBO",
            MediaKind = "TV",
            Shape = MediaTileShape.Landscape,
            Presentation = MediaTilePresentation.Default,
            SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
            HoverMode = MediaTileHoverMode.Expanded,
            LogoUrl = "/images/brands/hbo.svg",
            ShowLogoAsBrand = true,
            NavigationUrl = "/watch/tv?browse=networks&network=HBO",
            DetailsNavigationUrl = "/watch/tv?browse=networks&network=HBO",
            IsCollection = true,
            UseLandscapeGroupTile = true,
            PreviewTotalCount = 1,
            ArtworkStackItems =
            [
                new ArtworkStackItem
                {
                    Id = "1",
                    Title = "Test Show",
                    ImageUrl = "/covers/test-show.jpg",
                    MediaType = "TV",
                    Shape = ArtworkShape.Portrait,
                },
            ],
        };

        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, group));

        Assert.Contains("has-brand", cut.Find("article.media-group-tile").ClassList);
        Assert.Equal("/images/brands/hbo.svg", cut.Find(".media-group-tile__brand img").GetAttribute("src"));
        Assert.Equal("/watch/tv?browse=networks&network=HBO", cut.Find("a.media-group-tile__surface").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".media-group-tile__person"));
    }

    [Fact]
    public void MediaGroupTile_MixedCollectionUsesRepresentativeMediaBreadthWithoutChangingSeriesOrder()
    {
        var collection = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "Cross-Media Archive",
            MediaKind = "Collection",
            Shape = MediaTileShape.Landscape,
            Presentation = MediaTilePresentation.Default,
            SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
            HoverMode = MediaTileHoverMode.Expanded,
            NavigationUrl = "/collection/cross-media",
            IsCollection = true,
            UseLandscapeGroupTile = true,
            PreviewTotalCount = 6,
            GroupSummary = new MediaTileGroupSummaryViewModel { OwnedCount = 6, RelationshipLabel = "Smart collection" },
            MediaCounts =
            [
                new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.MenuBook, "Read", 4),
                new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Headphones, "Listen", 1),
                new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Movie, "Watch", 1),
            ],
            ArtworkStackItems =
            [
                new ArtworkStackItem { Id = "book-1", Title = "Book 1", ImageUrl = "/covers/book-1.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait },
                new ArtworkStackItem { Id = "book-2", Title = "Book 2", ImageUrl = "/covers/book-2.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait },
                new ArtworkStackItem { Id = "book-3", Title = "Book 3", ImageUrl = "/covers/book-3.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait },
                new ArtworkStackItem { Id = "book-4", Title = "Book 4", ImageUrl = "/covers/book-4.jpg", MediaType = "Book", Shape = ArtworkShape.Portrait },
                new ArtworkStackItem { Id = "album", Title = "Album", ImageUrl = "/covers/album.jpg", MediaType = "Music", Shape = ArtworkShape.Square },
                new ArtworkStackItem { Id = "movie", Title = "Movie", ImageUrl = "/covers/movie.jpg", MediaType = "Movie", Shape = ArtworkShape.Portrait },
            ],
        };

        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, collection));
        var sources = cut.FindAll(".media-artwork-group-preview__artwork")
            .Select(image => image.GetAttribute("src"))
            .ToList();

        Assert.Equal(["/covers/book-1.jpg", "/covers/album.jpg", "/covers/movie.jpg", "/covers/book-2.jpg"], sources);
        Assert.Equal(["4 read", "1 listen", "1 watch"], cut.FindAll(".media-group-tile__media-count").Select(node => node.GetAttribute("title")));
        Assert.Empty(cut.FindAll(".media-group-tile__kind"));
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
                HoverMode = MediaTileHoverMode.Expanded,
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

    [Fact]
    public void MediaTile_EnrichedLogoReplacesHoverTitleButNotRestingArtwork()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Title = "Arrival",
            HoverFacts = ["PG-13", "2016", "1h 56m", "★ 7.9"],
            MediaKind = "Movie",
            Shape = MediaTileShape.Portrait,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            HoverLayout = MediaTileHoverLayout.BannerPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            TileImageUrl = "/art/arrival-poster.jpg",
            HoverImageUrl = "/art/arrival-background.jpg",
            LogoUrl = "/art/arrival-logo.png",
            NavigationUrl = "/watch/movie/1",
            PrimaryNavigationUrl = "/watch/player/1",
            PrimaryActionLabel = "Play",
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item).Add(component => component.IsHomeSurface, true));

        Assert.Empty(cut.FindAll(".media-tile-logo"));
        Assert.NotEmpty(cut.FindAll(".media-tile-hover-logo"));
        Assert.Empty(cut.FindAll(".media-tile-hover-title"));
        Assert.Empty(cut.FindAll(".media-tile-hover-facts"));
        Assert.Empty(cut.FindAll(".media-tile-hover-body"));
        Assert.Equal("/art/arrival-background.jpg", cut.Find(".media-tile-hover-image").GetAttribute("src"));
        Assert.Equal("/art/arrival-logo.png", cut.Find(".media-tile-hover-logo").GetAttribute("src"));
    }

    private static MediaTileViewModel CreateShelfItem(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        NavigationUrl = $"/details/{title.ToLowerInvariant()}",
        HoverMode = MediaTileHoverMode.None,
    };

    private static MediaTileViewModel CreateGroupTile() => new()
    {
        Id = Guid.NewGuid(),
        CollectionId = Guid.NewGuid(),
        Title = "Foundation Series",
        MediaKind = "Book",
        Shape = MediaTileShape.Landscape,
        Presentation = MediaTilePresentation.BookSeries,
        SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
        HoverMode = MediaTileHoverMode.Expanded,
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
                NavigationUrl = "/book/1?mode=read",
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
                NavigationUrl = "/book/2?mode=read",
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
            HoverMode = MediaTileHoverMode.Expanded,
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

        Assert.NotEmpty(cut.FindAll(".media-tile.is-portrait.is-ordered-series-card"));
        Assert.Equal(2, cut.Find(".media-tile-media").QuerySelectorAll(".media-artwork-group-preview__artwork").Length);
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
        Assert.Empty(cut.FindAll(".media-tile-static-hover"));
        Assert.Empty(cut.FindAll(".media-tile-collection-copy"));
        Assert.Empty(cut.FindAll(".media-tile-caption"));
        Assert.Contains("Book Series", cut.Find(".media-tile-group-kind").TextContent);
        Assert.Empty(cut.FindAll(".media-tile-hover-series-stack"));
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
    public void MediaTile_TvShowDisplaysOwnedEpisodeCountWithoutEpisodeCollage()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Foundation",
            MediaKind = "TV",
            WorkId = Guid.NewGuid(),
            Shape = MediaTileShape.Portrait,
            Presentation = MediaTilePresentation.TvSeries,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            HoverLayout = MediaTileHoverLayout.BannerPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            TileImageUrl = "/shows/foundation.jpg",
            HoverImageUrl = "/shows/foundation-background.jpg",
            NavigationUrl = "/watch/tv/show/1",
            IsCollection = true,
            PreviewTotalCount = 12,
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item).Add(component => component.IsHomeSurface, true));

        Assert.Contains("TV Show", cut.Find(".media-tile-group-kind").TextContent);
        Assert.Equal("12 episodes owned", cut.Find(".media-tile-group-count").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".media-artwork-group-preview"));
        Assert.DoesNotContain("is-collection-card", cut.Find("article.media-tile").ClassList);
        Assert.DoesNotContain("is-collection-hover", cut.Find(".media-tile-hover-panel").ClassList);
        Assert.Contains("is-banner-popover", cut.Find(".media-tile-hover-panel").ClassList);
        Assert.Equal("/shows/foundation-background.jpg", cut.Find(".media-tile-hover-image").GetAttribute("src"));
        Assert.Empty(cut.FindAll(".media-tile-hover-body"));
    }

    [Fact]
    public void MediaTile_MovieWithoutCinematicArtUsesArtPopover()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Portrait Only",
            MediaKind = "Movie",
            WorkId = Guid.NewGuid(),
            Shape = MediaTileShape.Portrait,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            TileImageUrl = "/movies/portrait.jpg",
            HoverImageUrl = "/movies/portrait.jpg",
            NavigationUrl = "/watch/movie/1",
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item).Add(component => component.IsHomeSurface, true));

        var panel = cut.Find(".media-tile-hover-panel");
        Assert.Contains("is-art-popover", panel.ClassList);
        Assert.DoesNotContain("is-banner-popover", panel.ClassList);
    }

    [Fact]
    public void MediaTile_CanHideRedundantTvKindWithoutHidingEpisodeCount()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Foundation",
            MediaKind = "TV",
            Shape = MediaTileShape.Portrait,
            Presentation = MediaTilePresentation.TvSeries,
            IsCollection = true,
            PreviewTotalCount = 12,
        };

        var cut = Render<MediaTile>(parameters => parameters
            .Add(component => component.Item, item)
            .Add(component => component.HideGroupKind, true));

        Assert.Empty(cut.FindAll(".media-tile-group-kind"));
        Assert.Equal("12 episodes owned", cut.Find(".media-tile-group-count").GetAttribute("aria-label"));
    }

    [Fact]
    public void MediaTile_CardClickAlwaysOpensDetails()
    {
        var details = "/book/1";
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
        Assert.Empty(cut.FindAll("button"));
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
            HoverMode = MediaTileHoverMode.Expanded,
            TileImageUrl = "/art/playable.jpg",
            HoverImageUrl = "/art/playable.jpg",
            NavigationUrl = "/details/work",
            DetailsNavigationUrl = "/details/work",
            PrimaryNavigationUrl = "/details/work",
            PrimaryActionLabel = mediaKind is "Book" or "Comic" ? "Read" : "Play",
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        Assert.Empty(cut.FindAll("button"));
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
            HoverMode = MediaTileHoverMode.Expanded,
            TileImageUrl = "/art/song.jpg",
            HoverImageUrl = "/art/song.jpg",
            NavigationUrl = "/listen/music/songs",
            DetailsNavigationUrl = "/listen/music?browse=songs&track=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            PrimaryNavigationUrl = "/listen/music/songs",
            PrimaryActionLabel = "Play",
        };
        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        Assert.Empty(cut.FindAll("button"));
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

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item).Add(component => component.IsHomeSurface, true));

        Assert.Empty(cut.FindAll("button"));
        Assert.Contains("is-cinematic-hover", cut.Find("article.media-tile").ClassList);

        cut.Find(".media-tile-media").Click();
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith(details, nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaTile_AudioUsesStaticCoverHoverWithoutActions()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Title = "Long Audiobook",
            MediaKind = "Audiobook",
            Shape = MediaTileShape.Square,
            HoverArtworkShape = MediaTileShape.Portrait,
            SurfaceKind = MediaTileSurfaceKind.CoverSquare,
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            TileImageUrl = "/art/audio.jpg",
            HoverImageUrl = "/art/audio.jpg",
            NavigationUrl = "/listen/audiobook/1",
            PrimaryNavigationUrl = "/listen/audiobook/1",
            PrimaryActionLabel = "Play",
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        Assert.Empty(cut.FindAll("button"));
        Assert.Contains("is-hover-glow-only", cut.Find("article.media-tile").ClassList);
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
    }

    [Fact]
    public void MediaTile_AlbumUsesStaticSquareCoverIdentityWithoutTrackPreviewActions()
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "Midnight Echo",
            Subtitle = "Nova Vale",
            MediaKind = "Music",
            Shape = MediaTileShape.Square,
            HoverArtworkShape = MediaTileShape.Square,
            Presentation = MediaTilePresentation.Album,
            SurfaceKind = MediaTileSurfaceKind.CoverSquare,
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            TileImageUrl = "/art/midnight-echo.jpg",
            HoverImageUrl = "/art/midnight-echo.jpg",
            NavigationUrl = "/listen/music/albums/midnight-echo",
            PrimaryNavigationUrl = "/listen/music/albums/midnight-echo",
            PrimaryActionLabel = "Play Album",
            IsCollection = true,
            HoverFacts = ["2026", "12 tracks", "43 min"],
            ArtworkStackItems = Enumerable.Range(1, 4)
                .Select(index => new ArtworkStackItem
                {
                    Id = index.ToString(),
                    WorkId = Guid.NewGuid(),
                    AssetId = Guid.NewGuid(),
                    Title = $"Track {index}",
                    ImageUrl = "/art/midnight-echo.jpg",
                    MediaType = "Music",
                    Shape = ArtworkShape.Square,
                    Position = index.ToString(),
                    Facts = [$"{index + 2}:00"],
                })
                .ToList(),
        };

        var cut = Render<MediaTile>(parameters => parameters.Add(component => component.Item, item));

        Assert.Contains("is-hover-glow-only", cut.Find("article.media-tile").ClassList);
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void MediaTile_CssAndJavascriptSeparateStaticCoverAndCinematicHoverModes()
    {
        var root = FindRepoRoot();
        var css = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/MediaTiles/MediaTile.razor.css"));
        var script = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/app.js"));
        Assert.Contains("is-viewport-mounted", css);
        Assert.Contains("position:fixed", css.Replace(" ", ""));
        Assert.Contains("offsetHeight", script);
        Assert.Contains("window.updateMediaTileShelfStableHeight", script);
        var show = script[script.IndexOf("window.showMediaTileHover = function")..script.IndexOf("window.clearMediaTileHover = function")];
        Assert.Contains("panel.classList.add('is-inline-expanded')", show);
        Assert.DoesNotContain("window.mountMediaTileHover", show);
        Assert.Contains("cardEl.closest('.media-tile-grid')", script);
        Assert.Contains("prefers-reduced-motion: reduce", script);
        Assert.DoesNotContain("window.lockMediaTileHoverRowScroll(cardEl);", script);
        Assert.DoesNotContain("media-tile-hover-actions", css);
        Assert.DoesNotContain("media-tile-hover-host", File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Shared/MainLayout.razor")));
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("TV")]
    public void ContinueWatchCardsUseStillAndOneFixedOverlayWithoutJavascriptHover(string kind)
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Continue title",
            MediaKind = kind,
            Shape = MediaTileShape.Landscape,
            TileImageUrl = "/episode-still.jpg",
            HoverImageUrl = "/background.jpg",
            DetailsNavigationUrl = "/details/work/continue",
            Subtitle = "S1 E2"
        };
        var cut = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel { Items = [item] }));
        Assert.Contains("is-hover-overlay", cut.Find("article").ClassList);
        Assert.Equal("/episode-still.jpg", cut.Find(".media-tile-image").GetAttribute("src"));
        Assert.Single(cut.FindAll(".media-tile-static-hover"));
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
        Assert.Single(cut.FindAll("article a"));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "registerMediaTileHover");
    }

    [Fact]
    public void ContinueEpisodeOverlaySeparatesEpisodeIdentityFromShowCaption()
    {
        var show = Guid.NewGuid(); var episode = Guid.NewGuid(); var asset = Guid.NewGuid();
        var item = new MediaTileViewModel
        {
            Id = episode,
            Title = "Solo Leveling",
            MediaKind = "TV",
            Subject = MediaEngine.Contracts.Display.DisplaySubjectKind.TvEpisode,
            Shape = MediaTileShape.Landscape,
            TileImageUrl = "/still.jpg",
            EpisodeContext = new(show, episode, asset, "Solo Leveling", "I am Used to It", 1, 1,
                MediaEngine.Contracts.Display.DisplayContinuationState.InProgress, 120, 1440),
            DetailsNavigationUrl = "/details/tvshow/episode"
        };
        var cut = Render<ContinueWatchingOrListeningTile>(p => p.Add(c => c.Item, item).Add(c => c.IsHomeSurface, true));
        var overlay = cut.Find(".media-tile-static-hover").TextContent;
        Assert.Contains("S1 E1", overlay);
        Assert.Contains("I am Used to It", overlay);
        Assert.DoesNotContain("Solo Leveling", overlay);
        Assert.Equal("Solo Leveling", cut.Find(".media-tile-episode-caption span").TextContent);
        Assert.Contains("S1 E1", cut.Find(".media-tile-episode-caption strong").TextContent);
        Assert.Single(cut.FindAll("a"));
        var home = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel { Items = [item] }));
        Assert.Equal("S1 E1 · I am Used to It", home.Find(".media-tile-identity-caption strong").TextContent);
        Assert.Equal("Solo Leveling", home.Find(".media-tile-identity-caption span").TextContent);
        Assert.Contains("I am Used to It", home.Find(".media-tile-static-hover").TextContent);
        Assert.DoesNotContain("Solo Leveling", home.Find(".media-tile-static-hover").TextContent);
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("TV")]
    public void OnlyHomeWatchDiscoveryRegistersExpansion(string kind)
    {
        var item = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "Title",
            MediaKind = kind,
            Shape = MediaTileShape.Portrait,
            TileImageUrl = "/cover.jpg",
            HoverImageUrl = "/background.jpg",
            HoverMode = MediaTileHoverMode.Expanded,
            DetailsNavigationUrl = "/details/work/title"
        };
        var watch = Render<MediaTile>(p => p.Add(c => c.Item, item));
        Assert.Empty(watch.FindAll(".media-tile-hover-panel"));
        Assert.Contains("is-hover-glow-only", watch.Find("article").ClassList);
        Assert.DoesNotContain(JSInterop.Invocations, c => c.Identifier == "registerMediaTileHover");
        var home = Render<MediaTile>(p => p.Add(c => c.Item, item).Add(c => c.IsHomeSurface, true));
        Assert.Single(home.FindAll(".media-tile-hover-panel"));
        Assert.Contains(JSInterop.Invocations, c => c.Identifier == "registerMediaTileHover");
    }

    [Fact]
    public void ContinueKeepsFixedArtworkAndAccessibleProgressWithoutVisiblePercentage()
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
        Assert.Empty(cut.FindAll(".media-tile-continue-progress"));
        Assert.Equal("41", cut.Find("[role=progressbar]").GetAttribute("aria-valuenow"));
        Assert.DoesNotContain("41%", cut.Find(".media-tile-static-hover").TextContent);
        Assert.Contains("Continue movie", cut.Find(".media-tile-static-hover").TextContent);
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
        var lane = Render<ContinueWatchingOrListeningTile>(p => p.Add(c => c.Item, item));
        Assert.Single(lane.FindAll(".media-tile-static-hover"));
        Assert.Contains("is-hover-overlay", lane.Find("article").ClassList);
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
