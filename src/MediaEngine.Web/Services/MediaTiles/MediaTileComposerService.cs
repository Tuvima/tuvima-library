using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.MediaTiles;

public sealed class MediaTileComposerService
{
    private const int DisplayReadAttempts = 3;
    private static readonly StreamingServiceLogoResolver SourceLogos = new();

    private readonly IEngineApiClient _api;

    public MediaTileComposerService(IEngineApiClient api)
    {
        _api = api;
    }

    public async Task<DiscoveryPageViewModel> BuildHomeAsync(Guid? profileId = null, CancellationToken ct = default) =>
        FromDisplayPage(await RequireDisplayPageAsync(() => _api.GetDisplayHomeAsync(profileId, ct), "home", ct));

    public async Task<DiscoveryPageViewModel> BuildReadAsync(CancellationToken ct = default) =>
        FromDisplayPage(await RequireDisplayPageAsync(
            () => _api.GetDisplayBrowseAsync(lane: "read", grouping: "all", ct: ct),
            "read",
            ct));

    public async Task<DiscoveryPageViewModel> BuildWatchAsync(CancellationToken ct = default) =>
        FromDisplayPage(await RequireDisplayPageAsync(
            () => _api.GetDisplayBrowseAsync(lane: "watch", grouping: "all", ct: ct),
            "watch",
            ct));

    public async Task<DiscoveryPageViewModel> BuildListenAsync(Guid? profileId = null, CancellationToken ct = default) =>
        FromDisplayPage(await RequireDisplayPageAsync(
            () => _api.GetDisplayBrowseAsync(lane: "listen", grouping: "all", profileId: profileId, ct: ct),
            "listen",
            ct));

    public static bool IsUserVisiblePlaylist(ManagedCollectionViewModel collection)
    {
        return string.Equals(collection.CollectionType, "Playlist", StringComparison.OrdinalIgnoreCase)
               || string.Equals(collection.CollectionType, "Smart", StringComparison.OrdinalIgnoreCase)
               || string.Equals(collection.CollectionType, "PlaylistFolder", StringComparison.OrdinalIgnoreCase);
    }

    public static MediaTileViewModel FromPlaylist(ManagedCollectionViewModel playlist)
    {
        var route = $"/listen/music/playlists/{playlist.Id:D}";
        var smallArtwork = MediaTileArtworkUrl.Sized(playlist.CoverArtworkUrl, "s");
        var mediumArtwork = MediaTileArtworkUrl.Sized(playlist.CoverArtworkUrl, "m");
        return new MediaTileViewModel
        {
            Id = playlist.Id,
            CollectionId = playlist.Id,
            Title = playlist.Name,
            Subtitle = playlist.ItemCount == 1 ? "1 item" : $"{playlist.ItemCount} items",
            Description = playlist.Description,
            CoverUrl = playlist.CoverArtworkUrl,
            MediaKind = "Music",
            AccentColor = "var(--tl-media-audio)",
            Shape = MediaTileShape.Square,
            HoverArtworkShape = MediaTileShape.Square,
            Presentation = MediaTilePresentation.Album,
            SurfaceKind = MediaTileSurfaceKind.CoverSquare,
            HoverMode = MediaTileHoverMode.GlowOnly,
            TileTextMode = MediaTileTextMode.Caption,
            TileImageUrl = smallArtwork ?? playlist.CoverArtworkUrl,
            TileImageSrcSet = MediaTileArtworkUrl.SrcSet(smallArtwork, mediumArtwork),
            HoverImageUrl = mediumArtwork ?? playlist.CoverArtworkUrl,
            NavigationUrl = route,
            DetailsNavigationUrl = route,
            PrimaryNavigationUrl = route,
            PrimaryActionLabel = "Open",
            SortTimestamp = playlist.ModifiedAt ?? playlist.CreatedAt,
            IsCollection = true,
        };
    }

    public static DiscoveryPageViewModel FromDisplayPage(DisplayPageDto page)
    {
        var shelves = page.Shelves
            .Select(shelf => new MediaTileShelfViewModel
            {
                Key = shelf.Key,
                Title = shelf.Title,
                Subtitle = shelf.Subtitle,
                Items = shelf.Items.Select(FromDisplayCard).ToList(),
                SeeAllRoute = shelf.SeeAllRoute,
            })
            .ToList();

        var hero = page.Hero is null ? null : FromDisplayHero(page.Hero);

        return new DiscoveryPageViewModel
        {
            Key = page.Key,
            AccentColor = "var(--tl-accent-primary)",
            Hero = hero,
            Spotlights = BuildSpotlights(page, hero),
            Shelves = shelves,
            Catalog = page.Catalog.Select(FromDisplayCard).ToList(),
            EmptyTitle = "Your home screen is waiting for its first story",
            EmptySubtitle = "Once media lands in the library, home becomes the personalized view across everything you own.",
        };
    }

    private static DiscoveryHeroViewModel FromDisplayHero(DisplayHeroDto hero)
    {
        var heroSurfaceKind = ResolveHeroSurfaceKind(hero.Artwork);
        var heroPreviewSurfaceKind = ResolvePreviewSurfaceKind(hero.Artwork);
        var primaryAction = hero.Progress?.ResumeAction ?? hero.Actions.FirstOrDefault();
        var secondaryAction = hero.Actions.Skip(1).FirstOrDefault();

        return new DiscoveryHeroViewModel
        {
            Eyebrow = hero.Eyebrow ?? "From your library",
            Title = hero.Title,
            Subtitle = hero.Subtitle,
            Tagline = hero.Tagline,
            Description = hero.Description,
            BackgroundImageUrl = StringHelpers.FirstNonBlank(hero.Artwork.BackgroundUrl, hero.Artwork.BackgroundLargeUrl, hero.Artwork.BackgroundMediumUrl, hero.Artwork.BannerUrl, hero.Artwork.BannerLargeUrl, hero.Artwork.BannerMediumUrl),
            HeroBackgroundImageUrl = StringHelpers.FirstNonBlank(hero.Artwork.BackgroundUrl, hero.Artwork.BackgroundLargeUrl, hero.Artwork.BackgroundMediumUrl, hero.Artwork.BannerUrl, hero.Artwork.BannerLargeUrl, hero.Artwork.BannerMediumUrl),
            BannerImageUrl = StringHelpers.FirstNonBlank(hero.Artwork.BannerUrl, hero.Artwork.BannerLargeUrl, hero.Artwork.BannerMediumUrl),
            PreviewImageUrl = StringHelpers.FirstNonBlank(hero.Artwork.CoverUrl, hero.Artwork.CoverLargeUrl, hero.Artwork.CoverMediumUrl, hero.Artwork.SquareUrl, hero.Artwork.SquareLargeUrl, hero.Artwork.SquareMediumUrl),
            SurfaceKind = heroSurfaceKind,
            PreviewSurfaceKind = heroPreviewSurfaceKind,
            LogoUrl = hero.Artwork.LogoUrl,
            AccentColor = hero.Artwork.AccentColor ?? "var(--tl-accent-primary)",
            MetaText = string.Join(" / ", hero.Facts),
            MetaPills = hero.Facts,
            ProgressPct = hero.Progress?.Percent,
            RepresentativeEntityId = hero.Id,
            WorkId = hero.WorkId,
            CollectionId = hero.CollectionId,
            MediaKind = hero.MediaType,
            Presentation = hero.Presentation,
            Genres = hero.Genres,
            PreviewItems = hero.PreviewItems.Select(item => ToArtworkStackItem(item, hero.MediaType)).ToList(),
            PreviewTotalCount = hero.PreviewTotalCount,
            PrimaryActionLabel = primaryAction?.Label ?? "Open",
            PrimaryNavigationUrl = primaryAction?.WebUrl ?? "/",
            SecondaryActionLabel = "Details",
            SecondaryNavigationUrl = secondaryAction?.WebUrl,
        };
    }

    private static IReadOnlyList<DiscoveryHeroViewModel> BuildSpotlights(DisplayPageDto page, DiscoveryHeroViewModel? hero)
    {
        var slides = new List<DiscoveryHeroViewModel>();
        if (hero is not null)
        {
            slides.Add(hero);
        }

        foreach (var card in SpotlightCards(page))
        {
            if (slides.Count >= 5)
            {
                break;
            }

            if (hero is not null
                && string.Equals(card.Title, hero.Title, StringComparison.OrdinalIgnoreCase)
                && string.Equals(card.Subtitle, hero.Subtitle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            slides.Add(FromDisplayHero(new DisplayHeroDto(
                card.Title,
                card.Subtitle,
                EyebrowForCard(card),
                card.Artwork,
                card.Progress,
                card.Actions)
            {
                Facts = card.Facts,
                Id = card.Id,
                WorkId = card.WorkId,
                CollectionId = card.CollectionId,
                MediaType = card.MediaType,
                Presentation = card.Presentation,
                Tagline = card.Tagline,
                Description = card.Description,
                Genres = card.Genres,
                PreviewItems = card.PreviewItems,
                PreviewTotalCount = card.PreviewTotalCount,
            }));
        }

        return slides;
    }

    private static IEnumerable<DisplayCardDto> SpotlightCards(DisplayPageDto page)
    {
        if (string.Equals(page.Key, "watch", StringComparison.OrdinalIgnoreCase))
        {
            var continuingMovies = page.Shelves
                .FirstOrDefault(shelf => string.Equals(shelf.Key, "continue-watching", StringComparison.OrdinalIgnoreCase))?
                .Items
                .Where(card => !string.Equals(card.MediaType, "TV", StringComparison.OrdinalIgnoreCase))
                ?? [];
            var tvShows = page.Shelves
                .FirstOrDefault(shelf => string.Equals(shelf.Key, "tv-shows", StringComparison.OrdinalIgnoreCase))?
                .Items
                ?? [];
            var movies = page.Shelves
                .FirstOrDefault(shelf => string.Equals(shelf.Key, "movies", StringComparison.OrdinalIgnoreCase))?
                .Items
                ?? [];

            // TV landing spotlights use show cards exclusively. Continue cards keep
            // their episode stills in the shelf, but never donate them to the hero.
            return continuingMovies
                .Concat(tvShows)
                .Concat(movies)
                .DistinctBy(card => card.Id)
                .Take(5);
        }

        var preferredShelf = page.Shelves.FirstOrDefault(shelf =>
            string.Equals(shelf.Key, "continue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shelf.Key, "continue-watching", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shelf.Key, "continue-reading", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shelf.Key, "continue-listening", StringComparison.OrdinalIgnoreCase));

        preferredShelf ??= page.Shelves.FirstOrDefault(shelf =>
            string.Equals(shelf.Key, "new-tracks-added", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shelf.Key, "albums", StringComparison.OrdinalIgnoreCase));

        return preferredShelf is null
            ? page.Catalog.Take(5)
            : preferredShelf.Items.Take(5);
    }

    private static string EyebrowForCard(DisplayCardDto card) =>
        card.MediaType switch
        {
            "Book" or "Comic" => card.Progress is not null ? "Continue Reading" : "Read",
            "Audiobook" or "Music" => card.Progress is not null ? "Continue Listening" : "Listen",
            "Movie" or "TV" => card.Progress is not null ? "Continue Watching" : "Watch",
            _ => "From your library",
        };

    public static MediaTileViewModel FromDisplayCard(DisplayCardDto card)
    {
        var bucket = GetBucket(card.MediaType);
        var presentation = card.Presentation switch
        {
            "tvSeries" => MediaTilePresentation.TvSeries,
            "movieSeries" => MediaTilePresentation.MovieSeries,
            "bookSeries" => MediaTilePresentation.BookSeries,
            "comicSeries" => MediaTilePresentation.ComicSeries,
            "audiobookSeries" => MediaTilePresentation.AudiobookSeries,
            "album" => MediaTilePresentation.Album,
            "artist" => MediaTilePresentation.Artist,
            _ => MediaTilePresentation.Default,
        };
        var isTvSeries = presentation == MediaTilePresentation.TvSeries;
        var isAlbum = presentation == MediaTilePresentation.Album;
        var isTypedGroup = card.Flags.IsCollection
                           && presentation is MediaTilePresentation.TvSeries
                               or MediaTilePresentation.MovieSeries
                               or MediaTilePresentation.BookSeries
                               or MediaTilePresentation.ComicSeries
                               or MediaTilePresentation.AudiobookSeries
                               or MediaTilePresentation.Album;
        var isTvEpisode = bucket == MediaTileBucket.Tv && !card.Flags.IsCollection;
        // TV shows keep their show-level cover at rest; individual episode cards use
        // the managed episode still as their landscape surface.
        var surface = MediaTileArtworkResolver.Resolve(
            bucket,
            presentation,
            [
                new MediaTileArtworkVariant(ArtworkRole.Background, StringHelpers.FirstNonBlank(card.Artwork.BackgroundSmallUrl, card.Artwork.BackgroundUrl), card.Artwork.BackgroundMediumUrl, card.Artwork.BackgroundLargeUrl, card.Artwork.BackgroundWidthPx, card.Artwork.BackgroundHeightPx),
                new MediaTileArtworkVariant(ArtworkRole.Banner, StringHelpers.FirstNonBlank(card.Artwork.BannerSmallUrl, card.Artwork.BannerUrl), card.Artwork.BannerMediumUrl, card.Artwork.BannerLargeUrl, card.Artwork.BannerWidthPx, card.Artwork.BannerHeightPx),
                new MediaTileArtworkVariant(ArtworkRole.Square, StringHelpers.FirstNonBlank(card.Artwork.SquareSmallUrl, card.Artwork.SquareUrl), card.Artwork.SquareMediumUrl, card.Artwork.SquareLargeUrl, card.Artwork.SquareWidthPx, card.Artwork.SquareHeightPx),
                new MediaTileArtworkVariant(ArtworkRole.Cover, StringHelpers.FirstNonBlank(card.Artwork.CoverSmallUrl, card.Artwork.CoverUrl), card.Artwork.CoverMediumUrl, card.Artwork.CoverLargeUrl, card.Artwork.CoverWidthPx, card.Artwork.CoverHeightPx),
            ],
            preferLandscapeTile: isTvEpisode);
        var artworkStackItems = BuildArtworkStackItems(card);
        var useOrderedSeriesStack = UsesOrderedSeriesStack(presentation, artworkStackItems);
        var useSquareIndividual = isAlbum
                                  || (!card.Flags.IsCollection
                                      && (bucket == MediaTileBucket.Audiobook
                                          || (bucket == MediaTileBucket.Music && surface.Shape == MediaTileShape.Square)));
        // A TV show is structurally a group, but its card is a cinematic media identity.
        // Keep it on MediaTile so the show cover can expand into the show backdrop instead
        // of presenting the owned-episode carousel used by collection and series cards.
        var useLandscapeGroupTile = card.Flags.IsCollection
                                    && presentation is not (MediaTilePresentation.Artist or MediaTilePresentation.TvSeries or MediaTilePresentation.Album);
        var tileShape = useLandscapeGroupTile
                ? MediaTileShape.Landscape
                : isTvEpisode
                ? MediaTileShape.Landscape
                : useSquareIndividual
                ? MediaTileShape.Square
                : MediaTileShape.Portrait;
        var surfaceKind = useLandscapeGroupTile
                ? MediaTileSurfaceKind.BannerLandscape
                : isTvEpisode
                ? MediaTileSurfaceKind.BannerLandscape
                : useSquareIndividual
                ? MediaTileSurfaceKind.CoverSquare
                : MediaTileSurfaceKind.CoverPortrait;
        var hoverLayout = surface.HoverLayout;

        return new MediaTileViewModel
        {
            Id = card.Id,
            WorkId = card.WorkId ?? (isTvSeries ? card.Id : null),
            AssetId = card.AssetId,
            CollectionId = card.CollectionId,
            Title = card.Title,
            IsUpdatingDetails = card.ListMetadata?.IsUpdatingDetails == true,
            Subtitle = card.Subtitle,
            Tagline = card.Tagline,
            Description = card.Description,
            CoverUrl = card.Artwork.CoverUrl,
            BackgroundUrl = card.Artwork.BackgroundUrl,
            BannerUrl = card.Artwork.BannerUrl,
            LogoUrl = card.Artwork.LogoUrl,
            PreviewImages = BuildPreviewImages(card, artworkStackItems),
            ArtworkStackItems = artworkStackItems,
            PreviewTotalCount = card.PreviewTotalCount,
            MetaText = string.Join(" / ", card.Facts),
            QualityBadge = BadgeLabel(card.Badges, "quality"),
            SourceBadgeLabel = BadgeLabel(card.Badges, "source"),
            SourceLogoUrl = SourceLogos.ResolveLogoPath(BadgeLabel(card.Badges, "source")),
            MediaCounts = card.GroupSummary?.MediaCounts.Select(ToMediaCount).ToList() ?? [],
            GroupSummary = ToGroupSummary(card.GroupSummary),
            HoverFacts = card.Facts,
            Genres = card.Genres,
            MediaKind = card.MediaType,
            AccentColor = card.Artwork.AccentColor ?? AccentForBucket(bucket),
            Shape = tileShape,
            HoverArtworkShape = surface.HoverArtworkShape,
            Presentation = presentation,
            SurfaceKind = surfaceKind,
            HoverLayout = hoverLayout,
            HoverMode = SupportsExpandedHover(bucket, isTypedGroup) ? MediaTileHoverMode.Expanded : MediaTileHoverMode.Preview,
            TileTextMode = string.Equals(card.TileTextMode, "coverOnly", StringComparison.OrdinalIgnoreCase)
                ? MediaTileTextMode.CoverOnly
                : MediaTileTextMode.Caption,
            PreviewPlacement = string.Equals(card.PreviewPlacement, "bottom", StringComparison.OrdinalIgnoreCase)
                ? MediaTilePreviewPlacement.Bottom
                : MediaTilePreviewPlacement.Smart,
            TileImageUrl = surface.TileImageUrl,
            TileImageSrcSet = surface.TileImageSrcSet,
            HoverImageUrl = surface.HoverImageUrl,
            HoverImageSrcSet = surface.HoverImageSrcSet,
            HeroBackgroundImageUrl = surface.HeroBackgroundImageUrl,
            PreviewImageUrl = surface.PreviewImageUrl,
            TileImageFitMode = surface.TileImageFitMode,
            HoverImageFitMode = surface.HoverImageFitMode,
            NavigationUrl = ResolveCardNavigationUrl(card),
            DetailsNavigationUrl = ResolveCardDetailsNavigationUrl(card),
            PrimaryNavigationUrl = card.Progress?.ResumeAction?.WebUrl
                ?? card.Actions.FirstOrDefault(action => !string.IsNullOrWhiteSpace(action.WebUrl))?.WebUrl,
            PrimaryActionLabel = card.Actions.FirstOrDefault()?.Label ?? "Open",
            ProgressPct = card.Progress?.Percent,
            ProgressLabel = card.Progress?.Label,
            Creator = card.Subtitle,
            SortYear = card.SortYear,
            SortTimestamp = card.SortTimestamp,
            IsCollection = card.Flags.IsCollection,
            UseLandscapeGroupTile = useLandscapeGroupTile,
        };
    }

    private static string ResolveCardNavigationUrl(DisplayCardDto card)
        => card.Actions.FirstOrDefault(action => !string.IsNullOrWhiteSpace(action.WebUrl))?.WebUrl ?? "/";

    private static string ResolveCardDetailsNavigationUrl(DisplayCardDto card)
    {
        // Album cards may carry a track-resume action as well as their album action.
        // The card itself always opens the album detail; playback stays inside that surface.
        if (string.Equals(card.Presentation, "album", StringComparison.OrdinalIgnoreCase))
        {
            return card.Actions.FirstOrDefault(action =>
                       action.WebUrl?.StartsWith("/details/musicalbum/", StringComparison.OrdinalIgnoreCase) == true)
                   ?.WebUrl
                   ?? ResolveCardNavigationUrl(card);
        }

        return card.Actions.FirstOrDefault(action =>
                   action.Type.StartsWith("open", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(action.WebUrl))
               ?.WebUrl
               ?? card.Actions.Skip(1).FirstOrDefault(action => !string.IsNullOrWhiteSpace(action.WebUrl))?.WebUrl
               ?? ResolveCardNavigationUrl(card);
    }

    private async Task<DisplayPageDto> RequireDisplayPageAsync(
        Func<Task<DisplayPageDto?>> request,
        string surface,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= DisplayReadAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await request();
            if (page is not null)
            {
                return page;
            }

            if (attempt < DisplayReadAttempts)
            {
                var delay = _api.LastStatusCode == StatusCodes.Status429TooManyRequests
                    ? _api.LastRetryAfter ?? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromMilliseconds(150 * attempt);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 100, 60_000)),
                    ct);
            }
        }

        throw new InvalidOperationException(
            $"Display API did not return a page for {surface} after {DisplayReadAttempts} attempts.");
    }

    private static MediaTileBucket GetBucket(string? mediaType)
    {
        var value = (mediaType ?? string.Empty).ToLowerInvariant();
        if (value.Contains("comic") || value.Contains("cbz") || value.Contains("cbr"))
        {
            return MediaTileBucket.Comic;
        }

        if (value.Contains("audiobook") || value.Contains("m4b"))
        {
            return MediaTileBucket.Audiobook;
        }

        if (value.Contains("music"))
        {
            return MediaTileBucket.Music;
        }

        if (value.Contains("movie") || value.Contains("video"))
        {
            return MediaTileBucket.Movie;
        }

        if (value.Contains("tv"))
        {
            return MediaTileBucket.Tv;
        }

        if (value.Contains("book") || value.Contains("epub"))
        {
            return MediaTileBucket.Book;
        }

        return MediaTileBucket.Other;
    }

    private static string AccentForBucket(MediaTileBucket bucket) => bucket switch
    {
        MediaTileBucket.Book => "var(--tl-media-book)",
        MediaTileBucket.Comic => "var(--tl-media-comic)",
        MediaTileBucket.Audiobook => "var(--tl-media-audio)",
        MediaTileBucket.Movie => "var(--tl-status-info)",
        MediaTileBucket.Tv => "var(--tl-media-video)",
        MediaTileBucket.Music => "var(--tl-media-audio)",
        _ => "var(--tl-accent-primary)",
    };

    private static MediaTileGroupSummaryViewModel? ToGroupSummary(DisplayGroupSummaryDto? summary) => summary is null
        ? null
        : new MediaTileGroupSummaryViewModel
        {
            OwnedCount = summary.OwnedCount,
            KnownTotalCount = summary.KnownTotalCount,
            CompletedCount = summary.CompletedCount,
            InProgressCount = summary.InProgressCount,
            EarliestYear = summary.EarliestYear,
            LatestYear = summary.LatestYear,
            SequenceRange = summary.SequenceRange,
            RelationshipLabel = summary.RelationshipLabel,
        };

    private static MediaTileMediaCountViewModel ToMediaCount(DisplayGroupMediaCountDto count) => count.MediaType.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" => new(MudBlazor.Icons.Material.Filled.Movie, "Movies", count.Count),
        "tv" or "tv series" or "tv show" or "tv shows" => new(MudBlazor.Icons.Material.Filled.LiveTv, "TV", count.Count),
        "book" or "books" => new(MudBlazor.Icons.Material.Filled.MenuBook, "Books", count.Count),
        "comic" or "comics" => new(MudBlazor.Icons.Material.Filled.AutoStories, "Comics", count.Count),
        "music" => new(MudBlazor.Icons.Material.Filled.MusicNote, "Music", count.Count),
        "audiobook" or "audiobooks" => new(MudBlazor.Icons.Material.Filled.Headphones, "Audiobooks", count.Count),
        "watch" => new(MudBlazor.Icons.Material.Filled.PlayArrow, "Watch", count.Count),
        "read" => new(MudBlazor.Icons.Material.Filled.MenuBook, "Read", count.Count),
        "listen" => new(MudBlazor.Icons.Material.Filled.Headphones, "Listen", count.Count),
        _ => new(MudBlazor.Icons.Material.Filled.MoreHoriz, "Other", count.Count),
    };

    private static bool SupportsExpandedHover(MediaTileBucket bucket, bool isTypedGroup) =>
        isTypedGroup
        || bucket is MediaTileBucket.Movie
            or MediaTileBucket.Tv
            or MediaTileBucket.Book
            or MediaTileBucket.Comic
            or MediaTileBucket.Audiobook
            or MediaTileBucket.Music;

    private static MediaTileSurfaceKind ResolveHeroSurfaceKind(DisplayArtworkDto artwork)
    {
        // Background and banner are semantic landscape roles. They remain valid
        // hero backdrops even when a provider did not report pixel dimensions.
        if (!string.IsNullOrWhiteSpace(artwork.BackgroundUrl)
            || !string.IsNullOrWhiteSpace(artwork.BackgroundLargeUrl)
            || !string.IsNullOrWhiteSpace(artwork.BackgroundMediumUrl)
            || !string.IsNullOrWhiteSpace(artwork.BannerUrl)
            || !string.IsNullOrWhiteSpace(artwork.BannerLargeUrl)
            || !string.IsNullOrWhiteSpace(artwork.BannerMediumUrl))
        {
            return MediaTileSurfaceKind.BannerLandscape;
        }

        return ResolvePreviewSurfaceKind(artwork);
    }

    private static MediaTileSurfaceKind ResolvePreviewSurfaceKind(DisplayArtworkDto artwork)
    {
        if (!string.IsNullOrWhiteSpace(artwork.CoverUrl))
        {
            return ShapeFromDimensions(artwork.CoverWidthPx, artwork.CoverHeightPx);
        }

        if (!string.IsNullOrWhiteSpace(artwork.SquareUrl))
        {
            return ShapeFromDimensions(artwork.SquareWidthPx, artwork.SquareHeightPx, MediaTileSurfaceKind.CoverSquare);
        }

        if (!string.IsNullOrWhiteSpace(artwork.BannerUrl))
        {
            return MediaTileSurfaceKind.BannerLandscape;
        }

        return MediaTileSurfaceKind.CoverPortrait;
    }

    private static MediaTileSurfaceKind ShapeFromDimensions(int? width, int? height, MediaTileSurfaceKind fallback = MediaTileSurfaceKind.CoverPortrait)
    {
        if (width is not > 0 || height is not > 0)
        {
            return fallback;
        }

        var ratio = width.Value / (double)height.Value;
        if (ratio >= 1.45)
        {
            return MediaTileSurfaceKind.BannerLandscape;
        }

        return ratio >= 0.85
            ? MediaTileSurfaceKind.CoverSquare
            : MediaTileSurfaceKind.CoverPortrait;
    }

    private static bool HasUsableLandscape(string? url, int? width, int? height) =>
        !string.IsNullOrWhiteSpace(url)
        && width is > 0
        && height is > 0
        && width.Value / (double)height.Value >= 1.45;

    private static IReadOnlyList<ArtworkStackItem> BuildArtworkStackItems(DisplayCardDto card) =>
        card.PreviewItems
            .Select(item => ToArtworkStackItem(item, card.MediaType))
            .ToList();

    private static ArtworkStackItem ToArtworkStackItem(DisplayCardPreviewItemDto item, string? fallbackMediaType) => new()
    {
        Id = item.WorkId?.ToString("D") ?? item.AssetId?.ToString("D") ?? item.ImageUrl,
        WorkId = item.WorkId,
        AssetId = item.AssetId,
        Title = item.Title,
        ImageUrl = item.ImageUrl,
        MediaType = item.MediaType ?? fallbackMediaType ?? string.Empty,
        NavigationUrl = item.WebUrl,
        Shape = ToArtworkShape(item.Shape),
        Position = item.Position,
        Description = item.Description,
        Facts = item.Facts ?? [],
    };

    private static ArtworkShape ToArtworkShape(string? shape) =>
        shape?.Trim().ToLowerInvariant() switch
        {
            "square" => ArtworkShape.Square,
            "wide" or "landscape" => ArtworkShape.Wide,
            _ => ArtworkShape.Portrait,
        };

    private static bool UsesOrderedSeriesStack(MediaTilePresentation presentation, IReadOnlyList<ArtworkStackItem> artworkStackItems) =>
        artworkStackItems.Count > 0
        && presentation is MediaTilePresentation.BookSeries
            or MediaTilePresentation.ComicSeries
            or MediaTilePresentation.MovieSeries
            or MediaTilePresentation.AudiobookSeries;

    private static IReadOnlyList<string> BuildPreviewImages(DisplayCardDto card, IReadOnlyList<ArtworkStackItem> artworkStackItems)
    {
        var candidates = artworkStackItems.Select(item => item.ImageUrl).Concat(new[]
        {
            card.Artwork.CoverSmallUrl,
            card.Artwork.CoverMediumUrl,
            card.Artwork.CoverLargeUrl,
            card.Artwork.SquareSmallUrl,
            card.Artwork.SquareMediumUrl,
            card.Artwork.SquareLargeUrl,
            card.Artwork.BackgroundSmallUrl,
            card.Artwork.BackgroundMediumUrl,
            card.Artwork.BackgroundLargeUrl,
            card.Artwork.BannerSmallUrl,
            card.Artwork.BannerMediumUrl,
            card.Artwork.BannerLargeUrl,
            card.Artwork.CoverUrl,
            card.Artwork.SquareUrl,
            card.Artwork.BackgroundUrl,
            card.Artwork.BannerUrl,
        });

        return candidates
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(card.Flags.IsCollection ? 5 : 3)
            .ToList();
    }

    private static string? BadgeLabel(IReadOnlyList<DisplayCardBadgeDto> badges, string kind) =>
        badges.FirstOrDefault(badge => string.Equals(badge.Kind, kind, StringComparison.OrdinalIgnoreCase))?.Label;
}

public enum MediaTileBucket
{
    Other,
    Book,
    Comic,
    Audiobook,
    Movie,
    Tv,
    Music,
}
