using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Pure static utility that maps a list of <see cref="CollectionViewModel"/>s
/// (the raw HTTP API representation) into a <see cref="UniverseViewModel"/> —
/// the flat, cross-type view consumed by the Universe grid, stats bar,
/// and hero tiles.
///
/// Design notes:
/// <list type="bullet">
///   <item>No async I/O; call site decides when to refresh from the API.</item>
///   <item>All type-to-bucket classification lives here; components are colour-blind.</item>
///   <item>The dominant colour is the brand colour of the most-represented media bucket.</item>
/// </list>
/// </summary>
public static class UniverseMapper
{
    // ── Colour palette ────────────────────────────────────────────────────────
    // Sourced from PaletteProvider so config/ui/palette.json drives these values.
    // Bucket → palette mapping:
    //   Book    → palette.media_type.book
    //   Video   → palette.media_type.movie
    //   Comic   → palette.media_type.comic
    //   Audio   → palette.media_type.audiobook
    //   Unknown → palette.media_type.unknown (rgba fallback)
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<MediaTypeBucket, string> BucketColours =>
        new Dictionary<MediaTypeBucket, string>
        {
            [MediaTypeBucket.Book] = PaletteProvider.Current.MediaType.Book,
            [MediaTypeBucket.Video] = PaletteProvider.Current.MediaType.Movie,
            [MediaTypeBucket.Comic] = PaletteProvider.Current.MediaType.Comic,
            [MediaTypeBucket.Audio] = PaletteProvider.Current.MediaType.Audiobook,
            [MediaTypeBucket.Unknown] = PaletteProvider.Current.MediaType.Unknown,
        };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Flattens all Works from every Collection into a single <see cref="UniverseViewModel"/>.
    /// Empty collection list returns an empty library view (no exception).
    /// </summary>
    public static UniverseViewModel MapFromCollections(IReadOnlyList<CollectionViewModel> collections)
    {
        var items = collections
            .SelectMany(collection => collection.Works.Select(MapItem))
            .ToList();

        return new UniverseViewModel
        {
            Title = DeriveTitle(collections),
            DominantHexColor = DominantColour(items),
            Items = items,
        };
    }

    /// <summary>
    /// Maps a single <see cref="WorkViewModel"/> to a <see cref="MediaItemViewModel"/>.
    /// Exposed publicly so callers can incrementally update a cached universe
    /// when a <c>MediaAdded</c> event arrives without a full collection refresh.
    /// </summary>
    public static MediaItemViewModel MapItem(WorkViewModel work)
    {
        var bucket = ClassifyBucket(work.MediaType);
        return new MediaItemViewModel
        {
            Id = work.Id,
            CollectionId = work.CollectionId,
            MediaType = work.MediaType,
            Title = work.Title,
            Author = work.Author,
            Year = work.Year,
            DominantHexColor = BucketColours[bucket],
            MediaTypeBucket = bucket,
        };
    }

    /// <summary>Returns the brand hex colour for a given <see cref="MediaTypeBucket"/>.</summary>
    public static string ColourFor(MediaTypeBucket bucket) => BucketColours[bucket];

    /// <summary>
    /// Public entry-point for bucket classification — used by components that need
    /// to map a raw media-type string to an icon or colour (e.g. CommandPalette).
    /// </summary>
    public static MediaTypeBucket ClassifyBucketPublic(string mediaType) =>
        ClassifyBucket(mediaType);

    /// <summary>
    /// Returns the brand hex colour that best represents the dominant media type
    /// across a Collection's work list.  Used by <see cref="CollectionViewModel.DominantHexColor"/>
    /// to colour the Collection's bento tile without a full universe map pass.
    /// </summary>
    public static string ColourForCollection(IEnumerable<WorkViewModel> works)
    {
        var buckets = works.Select(w => ClassifyBucket(w.MediaType)).ToList();

        if (buckets.Count == 0)
        {
            return BucketColours[MediaTypeBucket.Unknown];
        }

        var top = buckets
            .GroupBy(b => b)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;

        return BucketColours[top];
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Classifies the raw domain media-type string (e.g. "Epub", "Video", "Cbz")
    /// into a broad UI bucket.  Delegates to <see cref="MediaTypeClassifier"/>
    /// for the canonical type mapping.
    /// </summary>
    private static MediaTypeBucket ClassifyBucket(string? mediaType)
    {
        var type = MediaTypeClassifier.Classify(mediaType);
        return type switch
        {
            MediaType.Books => MediaTypeBucket.Book,
            MediaType.Audiobooks => MediaTypeBucket.Audio,
            MediaType.Movies or MediaType.TV => MediaTypeBucket.Video,
            MediaType.Comics => MediaTypeBucket.Comic,
            MediaType.Music => MediaTypeBucket.Audio,
            _ => MediaTypeBucket.Unknown,
        };
    }

    /// <summary>
    /// Returns <see langword="true"/> when the raw media-type string maps to the
    /// <see cref="MediaTypeBucket.Audio"/> bucket.  Used to
    /// filter the grid to audio-only content.
    /// </summary>
    public static bool IsAudio(string mediaType) =>
        ClassifyBucket(mediaType) == MediaTypeBucket.Audio;

    /// <summary>
    /// Derives a display title for the universe from its collection composition.
    /// Single-collection libraries use the collection's name; multi-collection libraries use "My Library".
    /// </summary>
    private static string DeriveTitle(IReadOnlyList<CollectionViewModel> collections) =>
        collections.Count switch
        {
            0 => "Empty Library",
            1 => collections[0].DisplayName,
            _ => "My Library",
        };

    /// <summary>
    /// Returns the colour of the most-represented media bucket,
    /// defaulting to primary violet for an empty library.
    /// </summary>
    private static string DominantColour(List<MediaItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return BucketColours[MediaTypeBucket.Unknown];
        }

        var topBucket = items
            .GroupBy(i => i.MediaTypeBucket)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;

        return BucketColours[topBucket];
    }
}
