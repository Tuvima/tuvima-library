using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// Unified data shape for the hero banner component.
/// Normalizes both <see cref="CollectionViewModel"/> and <see cref="JourneyItemViewModel"/>
/// into a single rendering contract.
/// </summary>
public sealed record HeroData
{
    // ── Identity ────────────────────────────────────────────────────────────
    public Guid? EntityId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? Author { get; init; }
    public string? Narrator { get; init; }
    public string? Description { get; init; }

    // ── Imagery ─────────────────────────────────────────────────────────────
    public string? CoverUrl { get; init; }
    public string? HeroUrl { get; init; }
    public string? DominantColor { get; init; }

    // ── Metadata badges ─────────────────────────────────────────────────────
    public string? Year { get; init; }
    public string? MediaTypeLabel { get; init; }
    public string? Genre { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> GenreQids { get; init; } = [];
    public string? Rating { get; init; }
    public string? Series { get; init; }
    public string? SeriesPosition { get; init; }
    public int? WorkCount { get; init; }

    // ── CTA ─────────────────────────────────────────────────────────────────
    public string? ActionLabel { get; init; }
    public string? ActionIcon { get; init; }
    public double? ProgressPct { get; init; }

    // ── Layout hint ─────────────────────────────────────────────────────────

    /// <summary>
    /// True for Books, Comics, Audiobooks (portrait cover art).
    /// False for Movies, TV, Music (landscape background art).
    /// </summary>
    public bool IsPortraitMedia => FormatLabel(MediaTypeLabel) switch
    {
        "Book" or "Audiobook" or "Comic" => true,
        _ => false,
    };

    // ── Factory methods ─────────────────────────────────────────────────────

    /// <summary>Create hero data from a Collection (lane pages, non-journey carousel slides).</summary>
    public static HeroData FromCollection(CollectionViewModel collection) => new()
    {
        EntityId = collection.Id,
        Title = collection.DisplayName,
        Author = collection.Author,
        Description = Truncate(collection.Description, 200),
        CoverUrl = collection.CoverUrl,
        HeroUrl = collection.HeroUrl,
        DominantColor = collection.DominantHexColor,
        Year = collection.Year,
        MediaTypeLabel = FormatLabel(collection.PrimaryMediaType),
        Genre = collection.Genre,
        Genres = collection.Genres,
        GenreQids = collection.GenreQids,
        Rating = collection.Rating,
        Series = collection.Series,
    };

    /// <summary>Create hero data from an individual Work (carousel slides, work-level display).</summary>
    public static HeroData FromWork(WorkViewModel work, CollectionViewModel? parentCollection = null) => new()
    {
        EntityId = work.Id,
        Title = work.Title,
        Author = work.Author,
        Description = Truncate(work.Description, 200),
        CoverUrl = work.CoverUrl ?? parentCollection?.CoverUrl,
        HeroUrl = work.HeroUrl ?? parentCollection?.HeroUrl,
        DominantColor = parentCollection?.DominantHexColor,
        Year = work.Year,
        MediaTypeLabel = FormatLabel(work.MediaType),
        Genre = work.Genre,
        Genres = work.Genres,
        GenreQids = work.GenreQids,
        Rating = work.Rating,
        Series = work.Series,
        SeriesPosition = work.SeriesPosition,
    };

    /// <summary>Create hero data from a journey item (carousel slides with progress).</summary>
    public static HeroData FromJourney(JourneyItemViewModel item) => new()
    {
        EntityId = item.CollectionId,
        Title = item.Title,
        Subtitle = item.CollectionDisplayName,
        Author = item.Author,
        Narrator = item.Narrator != item.Author ? item.Narrator : null,
        Description = Truncate(item.Description, 200),
        CoverUrl = item.CoverUrl,
        HeroUrl = item.HeroUrl,
        MediaTypeLabel = item.FormatMediaType,
        Series = item.Series,
        SeriesPosition = item.SeriesPosition,
        ActionLabel = item.ActionLabel,
        ActionIcon = IconForMediaType(item.MediaType),
        ProgressPct = item.ProgressPct,
    };

    /// <summary>
    /// Create hero data from a Collection with an active journey overlay
    /// (lane pages where the hero collection has progress).
    /// </summary>
    public static HeroData FromCollectionWithJourney(CollectionViewModel collection, JourneyItemViewModel journey) => new()
    {
        EntityId = collection.Id,
        Title = collection.DisplayName,
        // Subtitle is set by the caller via PhraseTemplateService
        Author = collection.Author,
        Description = Truncate(collection.Description, 200),
        CoverUrl = collection.CoverUrl,
        HeroUrl = collection.HeroUrl,
        DominantColor = collection.DominantHexColor,
        Year = collection.Year,
        MediaTypeLabel = FormatLabel(collection.PrimaryMediaType),
        Genre = collection.Genre,
        Genres = collection.Genres,
        GenreQids = collection.GenreQids,
        Rating = collection.Rating,
        Series = collection.Series,
        ActionLabel = journey.ActionLabel,
        ActionIcon = IconForMediaType(journey.MediaType),
        ProgressPct = journey.ProgressPct,
    };

    // ── Shared helpers ──────────────────────────────────────────────────────

    internal static string FormatLabel(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return "";
        }

        return MediaTypeClassifier.GetDisplayLabel(mediaType);
    }

    internal static string IconForMediaType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return AppMaterialIcons.Filled.MenuBook;
        }

        var t = mediaType.ToLowerInvariant();
        if (t.Contains("audio") || t.Contains("m4b"))
        {
            return AppMaterialIcons.Filled.Headphones;
        }

        if (t.Contains("video") || t.Contains("movie"))
        {
            return AppMaterialIcons.Filled.PlayArrow;
        }

        if (t.Contains("comic") || t.Contains("cbz"))
        {
            return AppMaterialIcons.Filled.AutoStories;
        }

        if (t.Contains("music"))
        {
            return AppMaterialIcons.Filled.MusicNote;
        }

        if (t.Contains("tv"))
        {
            return AppMaterialIcons.Filled.Tv;
        }

        return AppMaterialIcons.Filled.MenuBook;
    }

    /// <summary>Colour for media type badges/indicators. Reusable by PosterCard and hero.</summary>
    internal static string FormatBadgeColor(string? mediaType)
    {
        var label = FormatLabel(mediaType);
        return label switch
        {
            "Book" => "var(--tl-media-book)",
            "Audiobook" => "var(--tl-media-audio)",
            "Movie" => "var(--tl-media-video)",
            "Video" => "var(--tl-media-video)",
            "Comic" => "var(--tl-media-comic)",
            _ => "var(--tl-accent-primary)",
        };
    }

    internal static string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "\u2026";
    }
}
