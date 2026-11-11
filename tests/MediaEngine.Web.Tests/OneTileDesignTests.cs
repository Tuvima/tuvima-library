using Bunit;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>One tile design, everywhere (spec one-tile-design): the same tile face on every page, no caption under it, "..." always visible, and the words it draws come from one place.</summary>
public sealed class OneTileDesignTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public OneTileDesignTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
    }

    private static MediaTileViewModel Movie(string? art = "/art/dune.jpg", string kind = "Movie", string title = "Dune", string? subtitle = "Denis Villeneuve", string? newLabel = null, bool settling = false, bool kept = false, double? progress = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Title = title,
        Subtitle = subtitle,
        SortYear = 2021,
        MediaKind = kind,
        NewLabel = newLabel,
        IsSettling = settling,
        IsKept = kept,
        ProgressPct = progress,
        Shape = MediaTileShape.Portrait,
        SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
        TileImageUrl = art,
        DetailsNavigationUrl = "/details/movie/dune",
        NavigationUrl = "/details/movie/dune",
    };

    private static MediaTileViewModel Video(string title, string? subtitle = null, int? folderCount = null) => new()
    {
        IsCollection = folderCount is not null,
        PreviewTotalCount = folderCount,
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Title = title,
        Subtitle = subtitle,
        MediaKind = "Videos",
        Shape = MediaTileShape.Portrait,
        SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
        DetailsNavigationUrl = "/details/video/x",
        NavigationUrl = "/details/video/x",
    };

    [Fact]
    public void ACoverWithArt_DrawsNoCaptionAndNoHoverPanelUnderOrOverIt()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Movie()));

        Assert.Empty(cut.FindAll(".media-tile-caption, .media-tile-caption__title, .media-tile-hover-panel, .media-tile-episode-caption"));
        Assert.Single(cut.FindAll("button[aria-label='More actions']"));
        Assert.Empty(cut.FindAll("button.media-tile-action--list"));
    }

    [Fact]
    public void AnEpisodeListKeepsItsNumberAndTitleUnderTheStill_OnlyWhenAsked()
    {
        var plain = Render<MediaTile>(p => p.Add(c => c.Item, Movie()));
        Assert.Empty(plain.FindAll(".media-tile-episode-caption"));
    }

    [Fact]
    public void AVideoWithNoCoverArt_IsTheTwoPartTile_WithTitleAndOneDetailLine()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Video("Beach day", "2:14")));

        Assert.NotNull(cut.Find(".media-tile.is-video-still"));
        Assert.Equal("Beach day", cut.Find(".media-tile-video-text__title").TextContent.Trim());
        Assert.Equal("2:14", cut.Find(".media-tile-video-text__detail").TextContent.Trim());
        Assert.Single(cut.FindAll(".media-tile-video-still"));
    }

    [Fact]
    public void ABookWithNoArt_IsAPlainCoverWithItsTitleAndAuthor()
    {
        var book = Movie(art: null, kind: "Book", title: "Untitled Manuscript", subtitle: "A. Writer");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, book));

        Assert.Equal("Untitled Manuscript", cut.Find(".media-tile-fallback__title").TextContent.Trim());
        Assert.Equal("A. Writer", cut.Find(".media-tile-fallback__detail").TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-tile-video-still"));
    }

    [Fact]
    public void ATitleStillBeingMatched_ShowsGettingDetailsInTheTopLeftLabel()
    {
        var item = Movie(settling: true);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, item));

        Assert.Equal("Getting details", cut.Find(".media-tile-label--status").TextContent.Trim());
    }

    [Fact]
    public void TheNewLabel_ComesFromTheEngine_AndIsOnlyPlacedByTheTile()
    {
        var item = Movie(newLabel: "New");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, item));

        Assert.Equal("New", cut.Find(".media-tile-label--status").TextContent.Trim());
        Assert.Empty(Render<MediaTile>(p => p.Add(c => c.Item, Movie())).FindAll(".media-tile-label--status"));
    }

    [Fact]
    public void AGroupTile_CarriesItsNameOnTheArt_AndItsCountInTheBottomLabel()
    {
        var group = new MediaTileViewModel
        {
            Id = Guid.NewGuid(),
            Title = "The Expanse",
            IsCollection = true,
            UseLandscapeGroupTile = true,
            NavigationUrl = "/details/collection/expanse",
            MediaCounts = [new MediaTileMediaCountViewModel(MediaEngine.Web.Components.Shared.AppMaterialIcons.Filled.MenuBook, "Books", 9)],
        };

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, group));

        Assert.Contains("The Expanse", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("9 books", cut.Find(".media-tile-label--bottom").TextContent.Trim());
    }

    [Theory]
    [InlineData("Movie", 1, "movie")]
    [InlineData("TV", 12, "episodes")]
    [InlineData("Videos", 3, "videos")]
    [InlineData("Music", 1, "song")]
    public void TheCountNoun_FollowsWhatTheGroupHolds(string kind, int count, string noun)
    {
        var item = new MediaTileViewModel { Id = Guid.NewGuid(), Title = "x", MediaKind = kind };
        Assert.Equal(noun, MediaTileLabels.Noun(item, count));
    }

    [Fact]
    public void AVideoFolder_ReadsItsCountAsItsDetailLine()
    {
        var folder = Video("Holiday 2024", folderCount: 3);

        Assert.Equal("3 videos", MediaTileLabels.VideoDetail(folder));
        Assert.Equal("3 videos", MediaTileLabels.Bottom(folder, "Kept"));
    }

    [Fact]
    public void AGroupOfOne_ReadsInTheSingular()
    {
        var album = new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Single", Presentation = MediaTilePresentation.Album, PreviewTotalCount = 1 };
        Assert.Equal("1 song", MediaTileLabels.CountText(album));
    }

    [Fact]
    public void ASingleTitle_HasNoBottomLabel_UnlessKept()
    {
        Assert.Null(MediaTileLabels.Bottom(Movie(), "Kept"));
        Assert.Equal("Kept", MediaTileLabels.Bottom(Movie(kept: true), "Kept"));
    }

    [Fact]
    public void TheStatusLabel_PrefersGettingDetailsOverNew()
    {
        Assert.Equal("Getting details", MediaTileLabels.Status(Movie(newLabel: "New", settling: true), "Getting details"));
    }

    [Fact]
    public async Task AContinueTile_MenuStartsWithGoToAndHasRemoveFromContinue_AndTextBelowThePicture()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var item = Movie(progress: 40);

        var cut = Render<ContinueWatchingOrListeningTile>(p => p.Add(c => c.Item, item));

        Assert.Equal("Dune", cut.Find(".continue-tile__title").TextContent.Trim());
        cut.Find("button[aria-label='More actions']").Click();
        var labels = cut.FindAll("[role='menuitem']").Select(node => node.TextContent.Trim()).ToList();
        Assert.StartsWith("Go to", labels[0], StringComparison.Ordinal);
        Assert.Contains("Remove from Continue", labels);
        Assert.True(labels.IndexOf("Add to My List") < labels.IndexOf("Remove from Continue"));
        Assert.Empty(cut.FindAll(".continue-tile__remove"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void NoBrowsePage_OffersATileSizeControl_OrReadsATileQuery()
    {
        var web = Path.Combine(RepoRoot(), "src", "MediaEngine.Web");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("TileSizePx", text, StringComparison.Ordinal);
            Assert.DoesNotContain("tile-size", text, StringComparison.Ordinal);
        }

        var browseQuery = File.ReadAllText(Path.Combine(web, "Components", "Browse", "BrowseQueryBuilder.cs"));
        Assert.DoesNotContain("\"tile\"", browseQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheContinueAndNoArtTiles_DrawTextBeyondTheArt()
    {
        var tile = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MediaEngine.Web", "Components", "MediaTiles", "MediaTile.razor"));
        Assert.DoesNotContain("media-tile-caption", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("media-tile-hover", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("registerMediaTileHover", tile, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTileSize_ComesFromTheTileTokens()
    {
        var tokens = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-tile-poster-w:214px", tokens.Replace(": ", ":"), StringComparison.Ordinal);
        Assert.Contains("--tl-tile-square-w:321px", tokens.Replace(": ", ":"), StringComparison.Ordinal);
        Assert.Contains("--tl-tile-landscape-w:320px", tokens.Replace(": ", ":"), StringComparison.Ordinal);
    }
}
