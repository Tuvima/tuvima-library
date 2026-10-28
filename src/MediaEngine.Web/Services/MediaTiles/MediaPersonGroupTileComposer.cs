using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// Creates the one shared person-aware collection card used by grouped browse
/// surfaces in Read, Watch, and Listen.
/// </summary>
public static class MediaPersonGroupTileComposer
{
    public static MediaTileViewModel Compose(
        Guid id,
        string personName,
        Guid? personId,
        string? personPhotoUrl,
        IReadOnlyList<string>? roles,
        string mediaType,
        int ownedCount,
        int? earliestYear,
        int? latestYear,
        DateTimeOffset sortTimestamp,
        IReadOnlyList<ArtworkStackItem> artworkItems,
        string navigationUrl,
        string accentColor,
        int? albumCount = null,
        int? songCount = null)
    {
        var resolvedRoles = (roles ?? [])
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resolvedArtwork = artworkItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
            .Take(4)
            .ToList();
        var primaryArtwork = resolvedArtwork.FirstOrDefault()?.ImageUrl;
        var isMusic = mediaType.Contains("music", StringComparison.OrdinalIgnoreCase);
        var songs = isMusic ? songCount ?? ownedCount : ownedCount;
        var albums = isMusic && albumCount is > 0 ? albumCount : null;
        var mediaCounts = new List<MediaTileMediaCountViewModel>();
        if (albums is { } albumTotal)
        {
            mediaCounts.Add(new MediaTileMediaCountViewModel(AppMaterialIcons.Outlined.Album, albumTotal == 1 ? "album" : "albums", albumTotal));
        }

        mediaCounts.Add(new MediaTileMediaCountViewModel(
            MediaCountIcon(mediaType),
            isMusic ? (songs == 1 ? "song" : "songs") : MediaCountLabel(mediaType, ownedCount),
            isMusic ? songs : ownedCount));

        return new MediaTileViewModel
        {
            Id = id,
            Title = personName,
            CoverUrl = primaryArtwork,
            PreviewImages = resolvedArtwork.Select(item => item.ImageUrl).ToList(),
            ArtworkStackItems = resolvedArtwork,
            PreviewTotalCount = ownedCount,
            MediaCounts = mediaCounts,
            GroupSummary = new MediaTileGroupSummaryViewModel
            {
                OwnedCount = ownedCount,
                EarliestYear = earliestYear,
                LatestYear = latestYear,
                RelationshipLabel = "Person collection",
            },
            MediaKind = "Person collection",
            AccentColor = accentColor,
            SecondaryAccentColor = "#111827",
            Shape = MediaTileShape.Landscape,
            Presentation = MediaTilePresentation.Default,
            SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
            HoverLayout = MediaTileHoverLayout.ArtOnlyPopover,
            TileTextMode = MediaTileTextMode.CoverOnly,
            TileImageUrl = primaryArtwork,
            HoverImageUrl = primaryArtwork,
            NavigationUrl = navigationUrl,
            PrimaryNavigationUrl = navigationUrl,
            DetailsNavigationUrl = navigationUrl,
            PrimaryActionLabel = "Open",
            Person = new MediaTilePersonViewModel
            {
                Id = personId ?? id,
                Name = personName,
                ImageUrl = personPhotoUrl,
                Roles = resolvedRoles,
                FactsLine = FactsLine(mediaType, ownedCount, albums, songs, earliestYear, latestYear),
            },
            CollectionKey = "person",
            SortYear = latestYear ?? earliestYear ?? 0,
            SortTimestamp = sortTimestamp,
            IsCollection = true,
            UseLandscapeGroupTile = true,
        };
    }

    /// <summary>
    /// The facts line every person tile shows, whatever the role: active years first, then the counts that fit
    /// the library (albums and songs for music, books, audiobooks, movies or shows otherwise).
    /// </summary>
    public static string FactsLine(string mediaType, int ownedCount, int? albumCount, int? songCount, int? earliestYear, int? latestYear)
    {
        var parts = new List<string>();
        var earliest = ValidYear(earliestYear);
        var latest = ValidYear(latestYear);
        if (earliest is { } from && latest is { } to)
        {
            parts.Add(from == to ? from.ToString() : $"{from}–{to}");
        }
        else if ((earliest ?? latest) is { } single)
        {
            parts.Add(single.ToString());
        }

        if (mediaType.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            if (albumCount is > 0)
            {
                parts.Add($"{albumCount} {(albumCount == 1 ? "album" : "albums")}");
            }

            var songs = songCount ?? ownedCount;
            parts.Add($"{songs} {(songs == 1 ? "song" : "songs")}");
        }
        else
        {
            parts.Add($"{ownedCount} {MediaCountLabel(mediaType, ownedCount)}");
        }

        return string.Join(" · ", parts);
    }

    private static int? ValidYear(int? year) => year is >= 1000 and <= 9999 ? year : null;

    public static string NavigationUrl(Guid? personId, string personName, string? context = null) => personId.HasValue
        ? $"/details/person/{personId.Value:D}{ContextQuery(context)}"
        : $"/search?q={Uri.EscapeDataString(personName)}&type=person";

    private static string ContextQuery(string? context)
        => string.IsNullOrWhiteSpace(context)
            ? string.Empty
            : $"?context={Uri.EscapeDataString(context.Trim().ToLowerInvariant())}";

    private static string MediaCountIcon(string mediaType)
    {
        if (mediaType.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return AppMaterialIcons.Outlined.Headphones;
        }

        if (mediaType.Contains("book", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("comic", StringComparison.OrdinalIgnoreCase))
        {
            return AppMaterialIcons.Outlined.AutoStories;
        }

        if (mediaType.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            return AppMaterialIcons.Outlined.MusicNote;
        }

        return AppMaterialIcons.Outlined.Movie;
    }

    private static string MediaCountLabel(string mediaType, int count)
    {
        var singular = count == 1;
        if (mediaType.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return singular ? "audiobook" : "audiobooks";
        }

        if (mediaType.Contains("comic", StringComparison.OrdinalIgnoreCase))
        {
            return singular ? "comic" : "comics";
        }

        if (mediaType.Contains("book", StringComparison.OrdinalIgnoreCase))
        {
            return singular ? "book" : "books";
        }

        if (mediaType.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            return singular ? "track" : "tracks";
        }

        if (mediaType.Contains("tv", StringComparison.OrdinalIgnoreCase))
        {
            return singular ? "show" : "shows";
        }

        return singular ? "movie" : "movies";
    }
}
