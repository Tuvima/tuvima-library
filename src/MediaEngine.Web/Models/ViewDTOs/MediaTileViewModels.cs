using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Models;

namespace MediaEngine.Web.Models.ViewDTOs;

public enum MediaTileContext
{
    Default,
    ContinueWatching,
}

public enum MediaTileShape
{
    Portrait,
    Landscape,
    Square,
}

public enum MediaTilePresentation
{
    Default,
    TvSeries,
    MovieSeries,
    BookSeries,
    ComicSeries,
    AudiobookSeries,
    Album,
    Artist,
}

public enum MediaTileSurfaceKind
{
    BannerLandscape,
    CoverPortrait,
    CoverSquare,
}

public enum MediaTileHoverLayout
{
    ArtOnlyPopover,
    BannerPopover,
}

public enum MediaTileImageFitMode
{
    Fill,
    Contain,
}

public enum MediaTileTextMode
{
    Caption,
    CoverOnly,
}

public enum MediaTilePreviewPlacement
{
    Smart,
    Bottom,
}

public sealed record MediaTileMediaCountViewModel(string Icon, string Label, int Count);

public sealed record MediaTilePersonViewModel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>The one small facts line under the name: active years and what they have in the library.</summary>
    public string? FactsLine { get; init; }
}

public sealed record MediaTileGroupSummaryViewModel
{
    public int OwnedCount { get; init; }
    public int? KnownTotalCount { get; init; }
    public int CompletedCount { get; init; }
    public int InProgressCount { get; init; }
    public int? EarliestYear { get; init; }
    public int? LatestYear { get; init; }
    public string? SequenceRange { get; init; }
    public string? RelationshipLabel { get; init; }
}

public sealed class MediaTileViewModel
{
    /// <summary>True while the item is still being matched or enriched; shows the quiet caption marker.</summary>
    public bool IsSettling { get; init; }
    /// <summary>The Universe this title belongs to when its page can open (Universe entry points spec); drives "Open Universe" in the tile "..." menu. Null draws no row.</summary>
    public string? UniverseQid { get; init; }
    /// <summary>True for an episode TheTVDB has no picture for: the tile draws a marked stand-in (the show's backdrop, dimmed, with the episode number) instead of artwork.</summary>
    public bool IsStandInPicture { get; init; }
    /// <summary>The episode number drawn over a stand-in, such as "E5" or the special's label.</summary>
    public string? StandInLabel { get; init; }
    /// <summary>The date Library checks again for the real picture (yyyy-MM-dd), shown in the stand-in's tooltip.</summary>
    public string? StandInRetryAfter { get; init; }
    public DisplaySubjectKind Subject { get; init; }
    public DisplayContinuationState ContinuationState { get; init; }
    public DisplayEpisodeContextDto? EpisodeContext { get; init; }
    public double? RemainingSeconds { get; init; }
    public double? PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public Guid Id { get; init; }
    public Guid? WorkId { get; init; }
    public Guid? AssetId { get; init; }
    public Guid? CollectionId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? Tagline { get; init; }
    public string? Description { get; init; }
    public string? CoverUrl { get; init; }
    public string? BackgroundUrl { get; init; }
    public string? BannerUrl { get; init; }
    public string? HeroUrl { get; init; }
    public string? LogoUrl { get; init; }
    public bool ShowLogoAsBrand { get; init; }
    public IReadOnlyList<string> PreviewImages { get; init; } = [];
    public IReadOnlyList<ArtworkStackItem> ArtworkStackItems { get; init; } = [];
    public int? PreviewTotalCount { get; init; }
    public string? StatusText { get; init; }
    public string? MetaText { get; init; }
    public string? QualityBadge { get; init; }
    /// <summary>True on a tile in the Unmatched filter whose file is kept as it is; it shows a small Kept label.</summary>
    public bool IsKept { get; init; }

    /// <summary>"New" or "New Episode": the small square label at the top-left of the cover (Home, detail and tiles spec D7); null when the item is not new.</summary>
    public string? NewLabel { get; init; }
    public string? SourceBadgeLabel { get; init; }
    public string? SourceLogoUrl { get; init; }
    public IReadOnlyList<MediaTileMediaCountViewModel> MediaCounts { get; init; } = [];
    public MediaTileGroupSummaryViewModel? GroupSummary { get; init; }
    public IReadOnlyList<string> ContextLines { get; init; } = [];
    public IReadOnlyList<string> HoverFacts { get; init; } = [];
    /// <summary>The content rating of a movie or show (R, PG-13, TV-MA and so on); null when none is known. The Pick up tile shows it instead of a star rating.</summary>
    public string? ContentRating { get; init; }
    public string? Tldr { get; init; }
    public IReadOnlyList<string> VibeTags { get; init; } = [];
    public string MediaKind { get; init; } = string.Empty;
    public string AccentColor { get; init; } = "var(--tl-status-info)";
    public string SecondaryAccentColor { get; init; } = "#111827";
    public ArtworkPalette? ArtworkPalette { get; init; }
    public MediaTileShape Shape { get; init; } = MediaTileShape.Portrait;
    public MediaTileShape? HoverArtworkShape { get; init; }
    public MediaTilePresentation Presentation { get; init; } = MediaTilePresentation.Default;
    public MediaTileSurfaceKind SurfaceKind { get; init; } = MediaTileSurfaceKind.CoverPortrait;
    public MediaTileTextMode TileTextMode { get; init; } = MediaTileTextMode.Caption;
    public MediaTilePreviewPlacement PreviewPlacement { get; init; } = MediaTilePreviewPlacement.Smart;
    public string? TileImageUrl { get; init; }
    public string? TileImageSrcSet { get; init; }
    public string TileImageSizes { get; init; } = "(max-width: 640px) 44vw, 220px";
    public string? HoverImageUrl { get; init; }
    public string? HoverImageSrcSet { get; init; }
    public string HoverImageSizes { get; init; } = "(max-width: 640px) 80vw, 560px";
    public string? HeroBackgroundImageUrl { get; init; }
    public string? PreviewImageUrl { get; init; }
    public MediaTileImageFitMode TileImageFitMode { get; init; } = MediaTileImageFitMode.Fill;
    public MediaTileImageFitMode HoverImageFitMode { get; init; } = MediaTileImageFitMode.Contain;
    public Guid? RepresentativeEntityId { get; init; }
    public string NavigationUrl { get; init; } = "/";
    public string? PrimaryNavigationUrl { get; init; }
    public string? DetailsNavigationUrl { get; init; }
    public string PrimaryActionLabel { get; init; } = "Open";
    public double? ProgressPct { get; init; }
    public string? ProgressLabel { get; init; }
    public string? Creator { get; init; }
    public MediaTilePersonViewModel? Person { get; init; }
    public string? CollectionKey { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public int SortYear { get; init; }
    public DateTimeOffset SortTimestamp { get; init; }
    public bool IsCollection { get; init; }
    public bool UseGroupTile { get; init; }

    /// <summary>The small corner word on a group tile: Collection, Series, Universe, List or Set.</summary>
    public MediaTileGroupKind GroupKind { get; init; }

    /// <summary>True for a playlist, which draws as the square album quadrant (the artist tile's 2 by 2) beside albums.</summary>
    public bool IsPlaylist { get; init; }

    /// <summary>The line a playlist's tile says in the bottom label ("Playlist · 48 songs · 3 h 12 min"); null falls back to the plain count.</summary>
    public string? TypeLine { get; init; }
    /// <summary>True on a hand-made playlist the person may add to: dragged songs can be dropped on its tile (Listen music spec D10). A rule-based or system playlist is false.</summary>
    public bool AcceptsSongDrops { get; init; }

    /// <summary>
    /// The cover a person chose for a group (a curated collection's own cover). When set it replaces the four member covers; null (the
    /// normal case, and always for a Series, which takes no cover of its own) draws the quadrants.
    /// </summary>
    public string? CustomCoverUrl { get; init; }

    /// <summary>A group of titles (collection, Series, Universe, list, set) draws as a quadrant tile: four member covers in a 2 by 2 grid.</summary>
    public bool RenderAsGroupTile =>
        !IsPlaylist
        && (UseGroupTile
            || Presentation is MediaTilePresentation.MovieSeries
                or MediaTilePresentation.BookSeries
                or MediaTilePresentation.ComicSeries
                or MediaTilePresentation.AudiobookSeries
            || (IsCollection
                && Presentation is not (MediaTilePresentation.TvSeries
                    or MediaTilePresentation.Album
                    or MediaTilePresentation.Artist)));

    /// <summary>The first four member covers of a group, in order: the pictures of the quadrant tile. Blank addresses are skipped.</summary>
    public IReadOnlyList<string> GroupCovers => GroupCoverItems
        .Select(item => item.ImageUrl)
        .ToList();

    /// <summary>The same first four member covers with their shapes and media kinds, so the tile keeps each cover's natural shape (review mock-ups D4, D5).</summary>
    public IReadOnlyList<ArtworkStackItem> GroupCoverItems => ArtworkStackItems
        .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
        .Take(4)
        .ToList();

    /// <summary>
    /// True when the covers shown are of more than one media kind (a book beside an audiobook, a movie beside a book): the group is a
    /// Collection, not a Series (review mock-ups D5). Blank kinds are ignored; "Book" and "Books" are one kind.
    /// </summary>
    public bool HoldsMixedMedia => GroupCoverItems
        .Select(item => NormaliseMediaKind(item.MediaType))
        .Where(kind => kind.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .Skip(1)
        .Any();

    private static string NormaliseMediaKind(string? mediaType)
    {
        var kind = (mediaType ?? string.Empty).Trim().ToLowerInvariant();
        return kind.Length > 3 && kind.EndsWith('s') ? kind[..^1] : kind;
    }

    /// <summary>A person with works (artist, director, author) draws as the quadrant tile with the person's square photo at the bottom left.</summary>
    public bool RenderAsPersonQuadrants =>
        Person is not null && RenderAsGroupTile && string.IsNullOrWhiteSpace(CustomCoverUrl) && GroupCovers.Count > 0;

    /// <summary>A person with no works to show, and a cast member, draw as a square photo with the name on the art.</summary>
    public bool RenderAsPersonPhoto => Person is not null && !RenderAsPersonQuadrants;

    /// <summary>
    /// Stable render identity for a display card. Journey rows may share a work while
    /// targeting different assets, so the work-level Id alone is not sufficient.
    /// </summary>
    public MediaTileRenderKey RenderKey => new(Id, WorkId, AssetId, CollectionId, MediaKind);

}

public readonly record struct MediaTileRenderKey(
    Guid Id,
    Guid? WorkId,
    Guid? AssetId,
    Guid? CollectionId,
    string MediaKind);

public sealed class MediaTileShelfViewModel
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public IReadOnlyList<MediaTileViewModel> Items { get; init; } = [];
    public string? SeeAllRoute { get; init; }

    public MediaTileShelfKind Kind => MediaTileShelfKeys.Classify(Key);
}

public enum MediaTileGroupKind
{
    Collection,
    Series,
    Universe,
    List,
    Set,
}

public enum MediaTileShelfKind
{
    Standard,
    Continue,
    Collections,
    ReadSeries,
    /// <summary>A collection shown as a lane (Collections and lists spec D9); the key carries the collection id.</summary>
    Lane,
}

public static class MediaTileShelfKeys
{
    public const string Continue = "continue";
    public const string ContinueReading = "continue-reading";
    public const string ContinueWatching = "continue-watching";
    public const string ContinueListening = "continue-listening";
    public const string ReadSeries = "series-and-reading-lists";
    public const string Collections = "collections";
    public const string HomeCollections = "home-collections";
    public const string ListenCollections = "listen-collections";

    /// <summary>Prefix of a collection lane's shelf key: "lane-" and the collection id without dashes.</summary>
    public const string LanePrefix = "lane-";

    /// <summary>Reads the collection id out of a lane's shelf key; false for any other shelf.</summary>
    public static bool TryGetLaneCollectionId(string? key, out Guid collectionId)
    {
        collectionId = Guid.Empty;
        return key is not null
            && key.StartsWith(LanePrefix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(key[LanePrefix.Length..], "N", out collectionId);
    }

    public static MediaTileShelfKind Classify(string? key)
    {
        if (TryGetLaneCollectionId(key, out _))
        {
            return MediaTileShelfKind.Lane;
        }

        if (string.Equals(key, Continue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, ContinueReading, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, ContinueWatching, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, ContinueListening, StringComparison.OrdinalIgnoreCase))
        {
            return MediaTileShelfKind.Continue;
        }

        if (string.Equals(key, Collections, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, HomeCollections, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, ListenCollections, StringComparison.OrdinalIgnoreCase))
        {
            return MediaTileShelfKind.Collections;
        }

        return string.Equals(key, ReadSeries, StringComparison.OrdinalIgnoreCase)
            ? MediaTileShelfKind.ReadSeries
            : MediaTileShelfKind.Standard;
    }
}
