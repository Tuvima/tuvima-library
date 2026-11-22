using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// The words a playlist tile's type line is made of, already in the person's language (the caller reads them from the string table), so the
/// composer stays free of the localizer: "Playlist", "Smart playlist", "Built in", "1 song" and the "{0} songs" format.
/// </summary>
public sealed record PlaylistTileWords(string Playlist, string SmartPlaylist, string BuiltIn, string OneSong, string ManySongsFormat);

/// <summary>
/// Turns a playlist into the one shared tile (spec group-pages-and-tiles D1, D1a): the square album quadrant of <c>MediaTile</c>, the same
/// 2 by 2 an artist has, filled with the first four albums of the playlist's songs, a missing quarter a faint dashed square. A playlist the
/// person gave a cover of their own shows that cover whole instead. With no covers at all it is the shared "no covers" tile with the playlist
/// icon. The type line ("Playlist · 48 songs · 3 h 12 min") is the bottom label. This is the only place a playlist becomes a tile.
/// </summary>
public static class PlaylistTileComposer
{
    /// <summary>The quadrant holds four albums.</summary>
    public const int MaxAlbumCovers = 4;

    /// <summary>The tile of one playlist (hand-made, Smart or built in from the profile's own list).</summary>
    /// <param name="playlist">The playlist as the page knows it.</param>
    /// <param name="albumCovers">The album covers of its songs in song order; the first four different ones are drawn. Ignored when the person chose a cover.</param>
    /// <param name="typeLine">The bottom label; null leaves the plain song count.</param>
    public static MediaTileViewModel Compose(ManagedCollectionViewModel playlist, IEnumerable<string>? albumCovers = null, string? typeLine = null)
    {
        ArgumentNullException.ThrowIfNull(playlist);
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
            IsPlaylist = true,
            AcceptsSongDrops = CollectionAuthoringRule.IsAddTarget(playlist, playlist: true),
            TypeLine = typeLine,
            ArtworkStackItems = CoverItems(playlist, albumCovers),
            MediaCounts = playlist.ItemCount > 0
                ? [new MediaTileMediaCountViewModel(AppMaterialIcons.Outlined.MusicNote, playlist.ItemCount == 1 ? "song" : "songs", playlist.ItemCount)]
                : [],
        };
    }

    /// <summary>
    /// The tile of Favorites, the built-in list of the profile's hearted songs: the same tile, with no menu and no drops (it has no
    /// playlist record), saying "Built in · 126 songs".
    /// </summary>
    public static MediaTileViewModel ComposeBuiltIn(string title, string route, int? count, PlaylistTileWords words, IEnumerable<string>? albumCovers = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        return new MediaTileViewModel
        {
            Title = title,
            MediaKind = "Music",
            AccentColor = "var(--tl-media-audio)",
            Shape = MediaTileShape.Square,
            Presentation = MediaTilePresentation.Album,
            SurfaceKind = MediaTileSurfaceKind.CoverSquare,
            NavigationUrl = route,
            DetailsNavigationUrl = route,
            PrimaryNavigationUrl = route,
            IsCollection = true,
            IsPlaylist = true,
            TypeLine = BuiltInTypeLine(count, words),
            ArtworkStackItems = AlbumCoverItems(albumCovers, title),
        };
    }

    /// <summary>"Playlist · 48 songs · 3 h 12 min" or "Smart playlist · 32 songs"; the length is left out when it is not known or <paramref name="includeLength"/> is false (a phone).</summary>
    public static string TypeLine(ManagedCollectionViewModel playlist, PlaylistTileWords words, bool includeLength)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(words);
        var line = $"{(PlaylistTileRules.IsSmart(playlist) ? words.SmartPlaylist : words.Playlist)}{Separator}{Songs(playlist.ItemCount, words)}";
        return includeLength && DisplayFormat.FormatDurationHoursMinutesOrNull(playlist.TotalDurationSeconds) is { } length
            ? $"{line}{Separator}{length}"
            : line;
    }

    /// <summary>"Built in · 126 songs", or just "Built in" while the count is not known.</summary>
    public static string BuiltInTypeLine(int? count, PlaylistTileWords words)
    {
        ArgumentNullException.ThrowIfNull(words);
        return count is { } songs ? $"{words.BuiltIn}{Separator}{Songs(songs, words)}" : words.BuiltIn;
    }

    private const string Separator = " · ";

    private static string Songs(int count, PlaylistTileWords words) =>
        count == 1 ? words.OneSong : string.Format(System.Globalization.CultureInfo.CurrentCulture, words.ManySongsFormat, DisplayFormat.FormatCount(count));

    // A cover the person chose fills the tile whole (a one-cover quadrant); otherwise the first four different album covers.
    private static IReadOnlyList<ArtworkStackItem> CoverItems(ManagedCollectionViewModel playlist, IEnumerable<string>? albumCovers) =>
        !string.IsNullOrWhiteSpace(playlist.CoverArtworkUrl)
            ? AlbumCoverItems([playlist.CoverArtworkUrl], playlist.Name)
            : AlbumCoverItems(albumCovers, playlist.Name);

    private static IReadOnlyList<ArtworkStackItem> AlbumCoverItems(IEnumerable<string>? covers, string title) =>
    [
        .. (covers ?? [])
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAlbumCovers)
            .Select(url => new ArtworkStackItem
            {
                Id = url,
                Title = title,
                ImageUrl = url,
                MediaType = "Music",
                Shape = ArtworkShape.Square,
            }),
    ];
}
