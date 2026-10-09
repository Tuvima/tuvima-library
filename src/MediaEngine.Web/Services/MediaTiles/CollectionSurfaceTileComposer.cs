using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// Shared card composition for the Collections section. Collection pages stay
/// focused on browse state while this class owns the established group-card
/// artwork, count, route, and person presentation contract.
/// </summary>
public static class CollectionSurfaceTileComposer
{
    public static MediaTileViewModel FromContentGroup(ContentGroupViewModel group)
    {
        var mediaType = NormalizeMediaType(group.PrimaryMediaType);
        var navigationUrl = SeriesNavigationUrl(group, mediaType);
        var artworkItems = group.PreviewItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
            .Take(4)
            .Select(item => new ArtworkStackItem
            {
                Id = item.WorkId.ToString("D"),
                WorkId = item.WorkId,
                Title = item.Title,
                ImageUrl = MediaTileArtworkUrl.Sized(item.ImageUrl, "s") ?? item.ImageUrl,
                MediaType = group.PrimaryMediaType,
                NavigationUrl = MediaNavigation.ForMedia(group.PrimaryMediaType, item.WorkId),
                Shape = ToArtworkShape(item.Shape, group.PrimaryMediaType),
                Description = item.Description,
                Facts = item.Facts ?? [],
            })
            .ToList();
        var primaryArtwork = StringHelpers.FirstNonBlank(
            group.CoverUrl,
            artworkItems.FirstOrDefault()?.ImageUrl,
            group.BackgroundUrl,
            group.BannerUrl);
        var count = group.DistinctTitleCount ?? group.WorkCount;

        return new MediaTileViewModel
        {
            Id = group.CollectionId,
            CollectionId = group.CollectionId,
            RepresentativeEntityId = group.RootWorkId,
            Title = NormalizeSeriesTitle(group.DisplayName),
            Subtitle = SeriesCountLabel(mediaType, count),
            Description = group.Description,
            CoverUrl = primaryArtwork,
            BackgroundUrl = group.BackgroundUrl,
            BannerUrl = group.BannerUrl,
            LogoUrl = group.LogoUrl,
            PreviewImages = artworkItems.Select(item => item.ImageUrl).ToList(),
            ArtworkStackItems = artworkItems,
            MediaCounts = [ToMediaCount(mediaType, count)],
            GroupSummary = new MediaTileGroupSummaryViewModel
            {
                OwnedCount = count,
                EarliestYear = group.EarliestYear,
                LatestYear = group.LatestYear,
                RelationshipLabel = "Ordered series",
            },
            HoverFacts =
            [
                SeriesCountLabel(mediaType, count),
                .. group.EarliestYear.HasValue && group.LatestYear.HasValue
                    ? new[] { group.EarliestYear == group.LatestYear ? group.EarliestYear.Value.ToString() : $"{group.EarliestYear}-{group.LatestYear}" }
                    : [],
            ],
            MediaKind = "Shelf",
            AccentColor = AccentForMedia(mediaType),
            SecondaryAccentColor = "#111827",
            Shape = MediaTileShape.Landscape,
            HoverArtworkShape = MediaTileShape.Landscape,
            Presentation = PresentationForMedia(mediaType),
            SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
            TileTextMode = MediaTileTextMode.CoverOnly,
            TileImageUrl = primaryArtwork,
            HoverImageUrl = StringHelpers.FirstNonBlank(group.BackgroundUrl, group.BannerUrl, primaryArtwork),
            HeroBackgroundImageUrl = StringHelpers.FirstNonBlank(group.HeroUrl, group.BackgroundUrl, group.BannerUrl),
            NavigationUrl = navigationUrl,
            PrimaryNavigationUrl = navigationUrl,
            DetailsNavigationUrl = navigationUrl,
            PrimaryActionLabel = "Open",
            CollectionKey = mediaType,
            PreviewTotalCount = count,
            SortYear = group.LatestYear ?? group.EarliestYear ?? 0,
            SortTimestamp = group.CreatedAt,
            IsCollection = true,
            UseLandscapeGroupTile = true,
        };
    }

    public static MediaTileViewModel FromContributorShelf(ContributorShelfDto shelf)
    {
        var route = $"/details/person/{shelf.PersonId:D}";
        var artworkItems = shelf.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.CoverUrl))
            .Take(4)
            .Select(item => new ArtworkStackItem
            {
                Id = item.WorkId.ToString("D"),
                WorkId = item.WorkId,
                Title = item.Title,
                ImageUrl = MediaTileArtworkUrl.Sized(item.CoverUrl, "s") ?? item.CoverUrl!,
                MediaType = item.MediaType,
                NavigationUrl = MediaNavigation.ForMedia(item.MediaType, item.WorkId),
                Shape = ToArtworkShape(null, item.MediaType),
                Facts = item.Year.HasValue ? [item.Year.Value.ToString()] : [],
            })
            .ToList();
        var primaryArtwork = artworkItems.FirstOrDefault()?.ImageUrl;
        var countLabel = $"{shelf.OwnedCount} owned {(shelf.OwnedCount == 1 ? "work" : "works")}";

        return new MediaTileViewModel
        {
            Id = StableGuid(shelf.Key),
            Title = shelf.Title,
            Subtitle = countLabel,
            CoverUrl = primaryArtwork,
            PreviewImages = artworkItems.Select(item => item.ImageUrl).ToList(),
            ArtworkStackItems = artworkItems,
            MediaCounts =
            [
                new MediaTileMediaCountViewModel(ShelfRoleIcon(shelf.Role), shelf.Lane, shelf.OwnedCount),
            ],
            GroupSummary = new MediaTileGroupSummaryViewModel
            {
                OwnedCount = shelf.OwnedCount,
                EarliestYear = shelf.EarliestYear,
                LatestYear = shelf.LatestYear,
                RelationshipLabel = shelf.Role,
            },
            HoverFacts =
            [
                countLabel,
                .. shelf.EarliestYear.HasValue && shelf.LatestYear.HasValue
                    ? new[] { shelf.EarliestYear == shelf.LatestYear ? shelf.EarliestYear.Value.ToString() : $"{shelf.EarliestYear}-{shelf.LatestYear}" }
                    : [],
            ],
            MediaKind = "Shelf",
            AccentColor = "var(--tl-accent-primary)",
            SecondaryAccentColor = "#111827",
            Shape = MediaTileShape.Landscape,
            HoverArtworkShape = MediaTileShape.Landscape,
            Presentation = MediaTilePresentation.Default,
            SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
            TileTextMode = MediaTileTextMode.CoverOnly,
            TileImageUrl = primaryArtwork,
            HoverImageUrl = primaryArtwork,
            NavigationUrl = route,
            PrimaryNavigationUrl = route,
            DetailsNavigationUrl = route,
            PrimaryActionLabel = "Open",
            CollectionKey = shelf.ShelfType,
            PreviewTotalCount = shelf.OwnedCount,
            SortYear = shelf.LatestYear ?? shelf.EarliestYear ?? 0,
            IsCollection = true,
            UseLandscapeGroupTile = true,
            Person = new MediaTilePersonViewModel
            {
                Id = shelf.PersonId,
                Name = shelf.PersonName,
                ImageUrl = shelf.HeadshotUrl,
                Roles = [shelf.Role],
            },
        };
    }

    public static MediaTileViewModel FromCollection(CollectionManagementCatalogViewModel collection)
    {
        var navigationUrl = collection.Person is null
            ? $"/details/collection/{collection.Id:D}"
            : $"/details/person/{collection.Person.Id:D}";
        var previewImages = collection.ArtworkItems
            .Select(item => MediaTileArtworkUrl.Sized(item.CoverUrl, "s"))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        var artworkStackItems = collection.ArtworkItems
            .Select(ToArtworkStackItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
            .Take(4)
            .ToList();
        var primaryArtwork = PrimaryArtwork(collection);
        var primarySmall = MediaTileArtworkUrl.Sized(primaryArtwork, "s");
        var primaryMedium = MediaTileArtworkUrl.Sized(primaryArtwork, "m");
        var surface = MediaTileArtworkResolver.Resolve(
            MediaTileBucket.Other,
            MediaTilePresentation.Default,
            [new MediaTileArtworkVariant(ArtworkRole.Cover, primarySmall, primaryMedium)],
            preferLandscapeTile: true);
        var heroArtwork = HeroBackground(collection);
        var heroMedium = MediaTileArtworkUrl.Sized(heroArtwork, "m");
        var paletteArtwork = PaletteArtwork(collection);
        var accentColor = StringHelpers.FirstNonBlank(
            paletteArtwork?.PrimaryColor,
            paletteArtwork?.AccentColor,
            "var(--tl-accent-primary)")!;
        var secondaryAccentColor = StringHelpers.FirstNonBlank(
            paletteArtwork?.SecondaryColor,
            paletteArtwork?.AccentColor,
            paletteArtwork?.PrimaryColor,
            "#111827")!;

        return new MediaTileViewModel
        {
            Id = collection.Id,
            CollectionId = collection.Id,
            Title = collection.Name,
            Subtitle = CollectionSubtitle(collection),
            Description = collection.Description,
            CoverUrl = primaryArtwork,
            BackgroundUrl = heroArtwork,
            PreviewImages = previewImages,
            ArtworkStackItems = artworkStackItems,
            StatusText = collection.StatusLabel,
            MediaCounts = BuildMediaCounts(collection),
            GroupSummary = new MediaTileGroupSummaryViewModel
            {
                OwnedCount = collection.ItemCount,
                EarliestYear = collection.EarliestYear,
                LatestYear = collection.LatestYear,
                RelationshipLabel = CollectionRelationshipLabel(collection.CollectionType),
            },
            ContextLines = BuildContextLines(collection),
            HoverFacts =
            [
                .. BuildMediaCountLabels(collection),
                collection.IsGlobal ? "Published" : "Unpublished",
            ],
            MediaKind = "Collection",
            AccentColor = accentColor,
            SecondaryAccentColor = secondaryAccentColor,
            ArtworkPalette = collection.ArtworkPalette,
            Shape = surface.Shape,
            HoverArtworkShape = surface.HoverArtworkShape,
            Presentation = MediaTilePresentation.Default,
            SurfaceKind = surface.SurfaceKind,
            HoverLayout = surface.HoverLayout,
            TileTextMode = MediaTileTextMode.CoverOnly,
            TileImageUrl = surface.TileImageUrl,
            TileImageSrcSet = surface.TileImageSrcSet,
            HoverImageUrl = surface.HoverImageUrl,
            HoverImageSrcSet = surface.HoverImageSrcSet,
            HeroBackgroundImageUrl = heroMedium,
            NavigationUrl = navigationUrl,
            PrimaryNavigationUrl = navigationUrl,
            DetailsNavigationUrl = navigationUrl,
            PrimaryActionLabel = "Open",
            Person = collection.Person is null
                ? null
                : new MediaTilePersonViewModel
                {
                    Id = collection.Person.Id,
                    Name = collection.Person.Name,
                    ImageUrl = collection.Person.HeadshotUrl,
                    Roles = collection.Person.Roles,
                },
            CollectionKey = collection.CollectionType,
            PreviewTotalCount = collection.ItemCount,
            SortYear = collection.LatestYear ?? collection.EarliestYear ?? 0,
            SortTimestamp = collection.ModifiedAt ?? collection.CreatedAt,
            IsCollection = true,
            UseLandscapeGroupTile = true,
        };
    }

    private static IReadOnlyList<string> BuildContextLines(CollectionManagementCatalogViewModel collection)
    {
        var lines = new List<string>();
        if (collection.ArtworkItems.Count > 0)
        {
            lines.Add(string.Join(" / ", collection.ArtworkItems.Take(3).Select(item => item.Title)));
        }

        if (!string.IsNullOrWhiteSpace(collection.Description))
        {
            lines.Add(collection.Description);
        }

        return lines;
    }

    private static IReadOnlyList<MediaTileMediaCountViewModel> BuildMediaCounts(
        CollectionManagementCatalogViewModel collection)
    {
        var mediaTypeCounts = new[]
        {
            ToMediaCount("Movies", collection.MovieCount),
            ToMediaCount("TV", collection.TvCount),
            ToMediaCount("Books", collection.BookCount),
            ToMediaCount("Comics", collection.ComicCount),
            ToMediaCount("Music", collection.MusicCount),
            ToMediaCount("Audiobooks", collection.AudiobookCount),
        }
            .Where(count => count.Count > 0)
            .ToList();
        if (mediaTypeCounts.Count > 0)
        {
            if (collection.OtherCount > 0)
            {
                mediaTypeCounts.Add(new MediaTileMediaCountViewModel(
                    AppMaterialIcons.Filled.MoreHoriz,
                    "Other",
                    collection.OtherCount));
            }

            return mediaTypeCounts;
        }

        var aggregateCounts = new List<MediaTileMediaCountViewModel>();
        if (collection.WatchCount > 0)
        {
            aggregateCounts.Add(new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.PlayArrow, "Watch", collection.WatchCount));
        }

        if (collection.ReadCount > 0)
        {
            aggregateCounts.Add(new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.MenuBook, "Read", collection.ReadCount));
        }

        if (collection.ListenCount > 0)
        {
            aggregateCounts.Add(new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Headphones, "Listen", collection.ListenCount));
        }

        if (collection.OtherCount > 0)
        {
            aggregateCounts.Add(new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.MoreHoriz, "Other", collection.OtherCount));
        }

        if (aggregateCounts.Count > 0)
        {
            return aggregateCounts;
        }

        return collection.ArtworkItems
            .GroupBy(item => NormalizeMediaType(item.MediaType), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => ToMediaCount(group.Key, group.Count()))
            .Where(count => count.Count > 0)
            .OrderBy(count => MediaCountSort(count.Label))
            .ToList();
    }

    private static string? CollectionRelationshipLabel(string? collectionType) => collectionType?.Trim() switch
    {
        "Universe" => "Shared universe",
        "Franchise" => "Franchise collection",
        "Series" => "Series rollup",
        "ContentGroup" => "Related works",
        "Custom" => "Curated collection",
        _ => null,
    };

    private static IReadOnlyList<string> BuildMediaCountLabels(CollectionManagementCatalogViewModel collection) =>
        BuildMediaCounts(collection)
            .Select(count => $"{count.Count} {count.Label}")
            .ToList();

    private static MediaTileMediaCountViewModel ToMediaCount(string mediaType, int count) => mediaType switch
    {
        "Movies" => new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Movie, "Movies", count),
        "TV" => new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.LiveTv, "TV", count),
        "Music" => new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.MusicNote, "Music", count),
        "Audiobooks" => new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Headphones, "Audiobooks", count),
        "Comics" => new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.AutoStories, "Comics", count),
        "Books" => new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.MenuBook, "Books", count),
        _ => new MediaTileMediaCountViewModel(AppMaterialIcons.Filled.Theaters, "Media", count),
    };

    private static string NormalizeMediaType(string? mediaType)
    {
        var value = mediaType ?? string.Empty;
        if (value.Contains("tv", StringComparison.OrdinalIgnoreCase) || value.Contains("show", StringComparison.OrdinalIgnoreCase))
        {
            return "TV";
        }

        if (value.Contains("movie", StringComparison.OrdinalIgnoreCase) || value.Contains("film", StringComparison.OrdinalIgnoreCase))
        {
            return "Movies";
        }

        if (value.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return "Audiobooks";
        }

        if (value.Contains("music", StringComparison.OrdinalIgnoreCase)
            || value.Contains("song", StringComparison.OrdinalIgnoreCase)
            || value.Contains("album", StringComparison.OrdinalIgnoreCase))
        {
            return "Music";
        }

        if (value.Contains("comic", StringComparison.OrdinalIgnoreCase))
        {
            return "Comics";
        }

        if (value.Contains("book", StringComparison.OrdinalIgnoreCase) || value.Contains("epub", StringComparison.OrdinalIgnoreCase))
        {
            return "Books";
        }

        return string.Empty;
    }

    private static string SeriesNavigationUrl(ContentGroupViewModel group, string mediaType) => mediaType switch
    {
        "Movies" => $"/details/movieseries/{group.CollectionId:D}?context=watch",
        "Books" => $"/details/bookseries/{group.CollectionId:D}?context=read",
        "Comics" => $"/details/comicseries/{group.CollectionId:D}?context=comics",
        "Music" => $"/details/musicalbum/{(group.RootWorkId ?? group.CollectionId):D}?context=listen",
        "Audiobooks" => $"/details/collection/{group.CollectionId:D}?context=listen",
        _ => $"/details/collection/{group.CollectionId:D}",
    };

    private static MediaTilePresentation PresentationForMedia(string mediaType) => mediaType switch
    {
        "Movies" => MediaTilePresentation.MovieSeries,
        "Books" => MediaTilePresentation.BookSeries,
        "Comics" => MediaTilePresentation.ComicSeries,
        "Audiobooks" => MediaTilePresentation.AudiobookSeries,
        "Music" => MediaTilePresentation.Album,
        _ => MediaTilePresentation.Default,
    };

    private static string AccentForMedia(string mediaType) =>
        AppMediaPresentation.AccentFor(mediaType);

    private static string SeriesCountLabel(string mediaType, int count)
    {
        var noun = mediaType switch
        {
            "Movies" => count == 1 ? "movie" : "movies",
            "Books" => count == 1 ? "book" : "books",
            "Comics" => count == 1 ? "owned issue" : "owned issues",
            "Music" => count == 1 ? "track" : "tracks",
            "Audiobooks" => count == 1 ? "audiobook" : "audiobooks",
            _ => count == 1 ? "item" : "items",
        };
        return $"{count} {noun}";
    }

    private static string NormalizeSeriesTitle(string title)
    {
        var normalized = title.Trim();
        foreach (var suffix in new[] { " collection", " series" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && normalized.Length > suffix.Length)
            {
                return normalized[..^suffix.Length].TrimEnd();
            }
        }

        return normalized;
    }

    private static int MediaCountSort(string label) => label switch
    {
        "Movies" => 0,
        "TV" => 1,
        "Books" => 2,
        "Comics" => 3,
        "Music" => 4,
        "Audiobooks" => 5,
        _ => 6,
    };

    private static string CollectionSubtitle(CollectionManagementCatalogViewModel collection) =>
        string.Join(" / ", BuildMediaCountLabels(collection));

    private static string? PrimaryArtwork(CollectionManagementCatalogViewModel collection) =>
        StringHelpers.FirstNonBlank(collection.CoverArtworkUrl, collection.ArtworkItems.FirstOrDefault()?.CoverUrl);

    private static string? HeroBackground(CollectionManagementCatalogViewModel collection) =>
        StringHelpers.FirstNonBlank(collection.ArtworkItems.Skip(1).FirstOrDefault()?.CoverUrl, PrimaryArtwork(collection));

    private static ArtworkStackItem ToArtworkStackItem(CollectionArtworkItemDto item) => new()
    {
        Id = item.WorkId.ToString("D"),
        WorkId = item.WorkId,
        Title = item.Title,
        ImageUrl = MediaTileArtworkUrl.Sized(item.CoverUrl, "s") ?? string.Empty,
        MediaType = item.MediaType,
        NavigationUrl = MediaNavigation.ForMedia(item.MediaType, item.WorkId),
        Shape = ToArtworkShape(item.ArtworkShape, item.MediaType),
        Description = item.Description,
        Facts = item.Facts,
    };

    private static ArtworkShape ToArtworkShape(string? shape, string? mediaType)
    {
        if (string.Equals(shape, "square", StringComparison.OrdinalIgnoreCase))
        {
            return ArtworkShape.Square;
        }

        if (string.Equals(shape, "wide", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shape, "landscape", StringComparison.OrdinalIgnoreCase))
        {
            return ArtworkShape.Wide;
        }

        if (string.Equals(shape, "portrait", StringComparison.OrdinalIgnoreCase))
        {
            return ArtworkShape.Portrait;
        }

        var normalized = NormalizeMediaType(mediaType);
        return normalized is "Audiobooks" or "Music"
            ? ArtworkShape.Square
            : ArtworkShape.Portrait;
    }

    private static CollectionArtworkItemDto? PaletteArtwork(CollectionManagementCatalogViewModel collection)
    {
        var candidates = collection.ArtworkItems
            .Where(item => !string.IsNullOrWhiteSpace(item.PrimaryColor)
                || !string.IsNullOrWhiteSpace(item.SecondaryColor)
                || !string.IsNullOrWhiteSpace(item.AccentColor))
            .OrderBy(item => StableHash(collection.Id, item.WorkId))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var index = (StableHash(collection.Id, collection.Id) & int.MaxValue) % candidates.Count;
        return candidates[index];
    }

    private static int StableHash(Guid collectionId, Guid valueId)
    {
        var bytes = collectionId.ToByteArray().Concat(valueId.ToByteArray()).ToArray();
        unchecked
        {
            var hash = 17;
            foreach (var value in bytes)
            {
                hash = (hash * 31) + value;
            }

            return hash;
        }
    }

    private static string ShelfRoleIcon(string role) => role switch
    {
        "Director" => AppMaterialIcons.Outlined.Movie,
        "Artist" => AppMaterialIcons.Outlined.Album,
        "Narrator" => AppMaterialIcons.Outlined.RecordVoiceOver,
        "Writer" => AppMaterialIcons.Outlined.AutoStories,
        _ => AppMaterialIcons.Outlined.MenuBook,
    };

    private static Guid StableGuid(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }
}
