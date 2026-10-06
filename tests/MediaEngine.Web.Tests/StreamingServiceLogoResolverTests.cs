using MediaEngine.Web.Components.Shared;
using System.Buffers.Binary;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Branding;

namespace MediaEngine.Web.Tests;

public sealed class StreamingServiceLogoResolverTests
{
    private const string AssetRoot = "src/MediaEngine.Web/wwwroot";
    private const string ServiceLogoRoot = "src/MediaEngine.Web/wwwroot/images/streaming-services";

    [Theory]
    [InlineData("Hulu", "/images/streaming-services/hulu.png")]
    [InlineData("FX on Hulu", "/images/streaming-services/hulu.png")]
    [InlineData("Apple TV+", "/images/streaming-services/apple-tv-plus.png")]
    [InlineData("The Roku Channel", "/images/streaming-services/roku-channel.png")]
    [InlineData("Vudu", "/images/streaming-services/fandango-at-home.png")]
    [InlineData("HBO Max", "/images/streaming-services/max.png")]
    [InlineData("Max", "/images/streaming-services/max.png")]
    public void Resolver_MapsKnownServiceAliases(string label, string expectedPath)
    {
        var resolver = new StreamingServiceLogoResolver();

        Assert.Equal(expectedPath, resolver.ResolveLogoPath(label));
    }

    [Fact]
    public void Resolver_LeavesUnknownLabelsUnmapped()
    {
        var resolver = new StreamingServiceLogoResolver();

        Assert.Null(resolver.ResolveLogoPath("Totally Unknown Network"));
    }

    [Fact]
    public void Manifest_ContainsValidNormalizedPngAssets()
    {
        var root = FindRepoRoot();
        var manifestPath = Path.Combine(root, ServiceLogoRoot, "manifest.json");
        var manifest = JsonSerializer.Deserialize<List<StreamingServiceLogoManifestEntry>>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        var expectedServiceIds = new[]
        {
            "netflix", "disney-plus", "hulu", "max", "prime-video", "apple-tv-plus",
            "paramount-plus", "peacock", "tubi", "pluto-tv", "roku-channel", "crunchyroll",
            "youtube", "youtube-tv", "starz", "showtime", "amc-plus", "britbox", "acorn-tv",
            "shudder", "mubi", "criterion-channel", "fandango-at-home", "plex", "kanopy",
            "hoopla", "discovery-plus", "mgm-plus", "dazn", "espn"
        };

        Assert.Equal(expectedServiceIds.Length, manifest.Count);
        Assert.Equal(expectedServiceIds.Length, manifest.Select(entry => entry.ServiceId).Distinct(StringComparer.Ordinal).Count());

        foreach (var expectedServiceId in expectedServiceIds)
        {
            var entry = Assert.Single(manifest, item => item.ServiceId == expectedServiceId);
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.NotEmpty(entry.Aliases);
            Assert.Equal(entry.Aliases.Count, entry.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal($"/images/streaming-services/{entry.ServiceId}.png", entry.LogoPath);
            Assert.False(string.IsNullOrWhiteSpace(entry.SourceUrl));
            Assert.True(DateTimeOffset.TryParse(entry.RetrievedAt, out _), $"Invalid retrievedAt for {entry.ServiceId}");
            Assert.False(string.IsNullOrWhiteSpace(entry.UsageNotes));

            var assetPath = Path.Combine(root, AssetRoot, entry.LogoPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(assetPath), $"Missing logo asset for {entry.ServiceId}: {assetPath}");
            Assert.True(new FileInfo(assetPath).Length > 1_000, $"Logo asset is unexpectedly empty for {entry.ServiceId}");

            var dimensions = ReadPngDimensions(assetPath);
            Assert.InRange(dimensions.Width, 200, 900);
            Assert.InRange(dimensions.Height, 120, 240);
            Assert.Equal(6, ReadPngColorType(assetPath));
        }
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        var header = File.ReadAllBytes(path).AsSpan(0, 26);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header[..8].ToArray());

        return (
            BinaryPrimitives.ReadInt32BigEndian(header[16..20]),
            BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }

    private static int ReadPngColorType(string path)
    {
        var header = File.ReadAllBytes(path).AsSpan(0, 26);
        return header[25];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed class StreamingServiceLogoManifestEntry
    {
        public string ServiceId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = [];
        public string LogoPath { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public string RetrievedAt { get; set; } = string.Empty;
        public string UsageNotes { get; set; } = string.Empty;
    }
}

public sealed class StreamingServiceHeroRenderTests : AsyncBunitContext
{
    private static readonly DetailEntityType[] EditableMediaTypes =
    [
        DetailEntityType.Work,
        DetailEntityType.Movie,
        DetailEntityType.TvShow,
        DetailEntityType.TvSeason,
        DetailEntityType.TvEpisode,
        DetailEntityType.Book,
        DetailEntityType.Audiobook,
        DetailEntityType.ComicIssue,
        DetailEntityType.MusicAlbum,
    ];

    public static IEnumerable<object[]> EditableMediaTypeData
        => EditableMediaTypes.Select(entityType => new object[] { entityType });

    public StreamingServiceHeroRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
    }

    [Fact]
    public void DetailHero_RendersStreamingServiceLogoSeparatelyFromShowLogo()
    {
        var model = new DetailPageViewModel
        {
            Id = "show-1",
            EntityType = DetailEntityType.TvShow,
            PresentationContext = DetailPresentationContext.Watch,
            Title = "Shogun",
            Artwork = new ArtworkSet
            {
                LogoUrl = "/images/show-title-logos/shogun.png",
                HeroArtwork = new HeroArtworkViewModel
                {
                    HasImage = true,
                    Mode = HeroArtworkMode.BackdropWithLogo,
                    Url = "/images/backdrops/shogun.jpg",
                },
            },
            HeroBrand = new HeroBrandViewModel
            {
                Label = "Hulu",
                ImageUrl = "/images/streaming-services/hulu.png",
            },
        };

        using var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<DetailHero>(1);
            builder.AddAttribute(2, "Model", model);
            builder.CloseComponent();
        });

        Assert.Equal("/images/show-title-logos/shogun.png", cut.Find(".tl-detail-hero__logo").GetAttribute("src"));
        Assert.Equal("/images/streaming-services/hulu.png", cut.Find(".tl-detail-hero-brand img").GetAttribute("src"));
        Assert.Empty(cut.FindAll(".tl-detail-hero-brand-badge img"));
    }

    [Fact]
    public void DetailHero_UsesDenseTitleSizingForLongWatchRenderedTitles()
    {
        var model = new DetailPageViewModel
        {
            Id = "movie-1",
            EntityType = DetailEntityType.Movie,
            PresentationContext = DetailPresentationContext.Watch,
            Title = "Star Wars: Episode V - The Empire Strikes Back",
            Artwork = new ArtworkSet
            {
                HeroArtwork = new HeroArtworkViewModel
                {
                    HasImage = true,
                    Mode = HeroArtworkMode.BackdropWithRenderedTitle,
                    Url = "/images/backdrops/empire.jpg",
                },
            },
        };

        using var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<DetailHero>(1);
            builder.AddAttribute(2, "Model", model);
            builder.CloseComponent();
        });

        var hero = cut.Find(".tl-detail-hero");
        Assert.Contains("tl-detail-hero--title-dense", hero.GetAttribute("class"));
        Assert.Equal(model.Title, cut.Find(".tl-detail-hero__copy h1").TextContent);

        var styles = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src/MediaEngine.Web/Components/Details/DetailHeroContent.razor.css"));
        Assert.Contains("tl-detail-hero--title-dense .tl-detail-hero__copy h1", styles);
    }

    [Theory]
    [MemberData(nameof(EditableMediaTypeData))]
    public void DetailHero_PutsEditInPermissionFilteredMoreMenuForEditableMediaTypes(DetailEntityType entityType)
    {
        using var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<DetailHero>(1);
            builder.AddAttribute(2, "Model", CreateEditableModel(entityType));
            builder.CloseComponent();
        });

        Assert.Empty(cut.FindAll(".tl-detail-inline-edit"));
        Assert.Empty(cut.FindAll("button[aria-label='Edit']"));
        var overflow = Assert.Single(cut.FindComponents<OverflowActionMenu>());
        var edit = Assert.Single(overflow.Instance.Actions);
        Assert.Equal("edit", edit.Key);
        Assert.Equal("Edit", edit.Label);

        var overflowMenuSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src/MediaEngine.Web/Components/Details/OverflowActionMenu.razor"));
        Assert.Contains("Actions.Count > 0", overflowMenuSource);
        Assert.Contains("<AppOverflowMenu", overflowMenuSource);
        Assert.Contains("<ManageActionsMenu", overflowMenuSource);
    }

    [Fact]
    public async Task OverflowActionMenu_InvokesEditAndClosesTheMenu()
    {
        var popovers = Render<AppPopoverHost>();
        DetailAction? selected = null;
        var edit = new DetailAction
        {
            Key = "edit",
            Label = "Edit",
            Icon = "edit",
        };

        using var cut = Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, [edit])
            .Add(component => component.Label, "More")
            .Add(component => component.OnActionSelected, action => selected = action));

        cut.Find("button[aria-label='More actions']").Click();
        Assert.Single(cut.FindAll("[role='menu']"));

        await cut.Find("button[role='menuitem']").ClickAsync(new());

        Assert.Same(edit, selected);
        Assert.Empty(cut.FindAll("[role='menu']"));
    }

    [Theory]
    [InlineData(DetailEntityType.MovieSeries)]
    [InlineData(DetailEntityType.BookSeries)]
    [InlineData(DetailEntityType.ComicSeries)]
    public void DetailHero_HidesEditForGeneratedSeries(DetailEntityType entityType)
    {
        var model = CreateEditableModel(entityType, canEdit: false);

        using var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<DetailHero>(1);
            builder.AddAttribute(2, "Model", model);
            builder.CloseComponent();
        });

        Assert.Empty(cut.FindAll("button[aria-label='Edit']"));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static DetailPageViewModel CreateEditableModel(DetailEntityType entityType, bool canEdit = true)
        => new()
        {
            Id = Guid.NewGuid().ToString("D"),
            EntityType = entityType,
            PresentationContext = PresentationContextFor(entityType),
            Title = $"{entityType} title",
            EditorTarget = canEdit
                ? new DetailEditorTarget
                {
                    EntityId = Guid.NewGuid().ToString("D"),
                    EntityKind = "Work",
                }
                : null,
            Artwork = new ArtworkSet(),
            PrimaryActions =
            [
                new DetailAction
                {
                    Key = PrimaryActionKeyFor(entityType),
                    Label = PrimaryActionLabelFor(entityType),
                    Icon = PrimaryActionIconFor(entityType),
                    IsPrimary = true,
                },
            ],
            SecondaryActions =
            [
                new DetailAction
                {
                    Key = "my-list",
                    Label = LibraryActionLabelFor(entityType),
                    Icon = "favorite",
                    Tooltip = LibraryActionLabelFor(entityType),
                    DisplayStyle = entityType is DetailEntityType.Book or DetailEntityType.ComicIssue ? "button" : "icon",
                },
            ],
            OverflowActions = canEdit
                ?
                [
                    new DetailAction
                    {
                        Key = "edit",
                        Label = "Edit",
                        Icon = "edit",
                    },
                ]
                : [],
        };

    private static DetailPresentationContext PresentationContextFor(DetailEntityType entityType)
        => entityType switch
        {
            DetailEntityType.Movie or DetailEntityType.MovieSeries or DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => DetailPresentationContext.Watch,
            DetailEntityType.Audiobook or DetailEntityType.MusicAlbum => DetailPresentationContext.Listen,
            DetailEntityType.ComicIssue or DetailEntityType.ComicSeries => DetailPresentationContext.Comics,
            _ => DetailPresentationContext.Read,
        };

    private static string PrimaryActionKeyFor(DetailEntityType entityType)
        => PresentationContextFor(entityType) switch
        {
            DetailPresentationContext.Watch => "watch",
            DetailPresentationContext.Listen => entityType is DetailEntityType.Audiobook ? "listen" : "play-album",
            _ => "read",
        };

    private static string PrimaryActionLabelFor(DetailEntityType entityType)
        => PrimaryActionKeyFor(entityType) switch
        {
            "watch" => "Watch",
            "listen" => "Listen",
            "play-album" => "Play",
            _ => "Read",
        };

    private static string PrimaryActionIconFor(DetailEntityType entityType)
        => PrimaryActionKeyFor(entityType) switch
        {
            "watch" or "play-album" => "play_arrow",
            "listen" => "headphones",
            _ => "menu_book",
        };

    private static string LibraryActionLabelFor(DetailEntityType entityType)
        => entityType switch
        {
            DetailEntityType.Book or DetailEntityType.ComicIssue => "Want to Read",
            DetailEntityType.Audiobook => "Want to Listen",
            DetailEntityType.Movie or DetailEntityType.MovieSeries or DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => "Watchlist",
            _ => "My List",
        };
}
