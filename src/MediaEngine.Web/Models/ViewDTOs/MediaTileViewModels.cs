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
    ArtistPhotoSquare,
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

public enum MediaTileHoverMode
{
    None,
    GlowOnly,
    Preview,
    Expanded,
    Overlay,
}

public sealed record MediaTileMediaCountViewModel(string Icon, string Label, int Count);

public sealed record MediaTilePersonViewModel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
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
    public string? SourceBadgeLabel { get; init; }
    public string? SourceLogoUrl { get; init; }
    public IReadOnlyList<MediaTileMediaCountViewModel> MediaCounts { get; init; } = [];
    public MediaTileGroupSummaryViewModel? GroupSummary { get; init; }
    public IReadOnlyList<string> ContextLines { get; init; } = [];
    public IReadOnlyList<string> HoverFacts { get; init; } = [];
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
    public MediaTileHoverLayout HoverLayout { get; init; } = MediaTileHoverLayout.ArtOnlyPopover;
    public MediaTileHoverMode HoverMode { get; init; } = MediaTileHoverMode.Expanded;
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
    public bool UseLandscapeGroupTile { get; init; }

    public bool RenderAsLandscapeGroupTile =>
        UseLandscapeGroupTile
        || (IsCollection
            && Presentation is not (MediaTilePresentation.TvSeries
                or MediaTilePresentation.Album
                or MediaTilePresentation.Artist));

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

public enum MediaTileShelfKind
{
    Standard,
    Continue,
    Collections,
    ReadSeries,
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

    public static MediaTileShelfKind Classify(string? key)
    {
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
