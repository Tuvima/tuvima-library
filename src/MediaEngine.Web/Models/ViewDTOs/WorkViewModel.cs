using MediaEngine.Domain;
using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>UI representation of a Work (individual media item).</summary>
public sealed class WorkViewModel
{
    public Guid Id { get; init; }
    public Guid? CollectionId { get; init; }
    public Guid? RootWorkId { get; init; }
    public Guid? ArtistPersonId { get; init; }
    public bool IsUpdatingDetails { get; init; }
    public Guid? AssetId { get; init; }
    public string MediaType { get; init; } = string.Empty;
    public string? WorkKind { get; init; }
    public int? Ordinal { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? ResolvedCoverUrl { get; init; }
    public string? ResolvedBackgroundUrl { get; init; }
    public string? ResolvedBannerUrl { get; init; }
    public string? ResolvedHeroUrl { get; init; }
    public string? ResolvedLogoUrl { get; init; }
    public List<CanonicalValueViewModel> CanonicalValues { get; init; } = [];

    // ── Display helpers ───────────────────────────────────────────────────────

    public string Title => Canonical("title") ?? $"Untitled ({MediaType})";
    public string? OriginalTitle => Canonical("original_title");

    /// <summary>All credited authors/creators from presentation-ready canonical text.</summary>
    public IReadOnlyList<string> Authors
    {
        get
        {
            var raw = Canonical("author") ?? Canonical("creator");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            return SplitPresentationList(raw);
        }
    }

    /// <summary>
    /// Primary display author — the first value in <see cref="Authors"/>,
    /// which is always the pen name / canonical credited name when the
    /// author audit has run (pen names sort first).
    /// </summary>
    public string? Author => Authors.FirstOrDefault();
    public string? AuthorQid => Canonical("author_qid");
    public string? WikidataQid => Canonical("wikidata_qid");

    /// <summary>
    /// The best human-facing identifier for this work based on its media type.
    /// Books → ISBN, Movies/TV → IMDb, Music → MusicBrainz, fallback → QID.
    /// </summary>
    public string? DisplayIdentifier
    {
        get
        {
            var mt = (MediaType ?? string.Empty).ToLowerInvariant();
            if (mt.Contains("book") || mt.Contains("epub") || mt.Contains("audio"))
            {
                return Canonical("isbn") ?? Canonical("asin") ?? WikidataQid;
            }

            if (mt.Contains("movie") || mt.Contains("tv") || mt.Contains("video"))
            {
                return Canonical("imdb_id") ?? Canonical("tmdb_id") ?? WikidataQid;
            }

            if (mt.Contains("music"))
            {
                return Canonical("musicbrainz_id") ?? WikidataQid;
            }

            if (mt.Contains("comic"))
            {
                return Canonical(BridgeIdKeys.ComicVineId) ?? WikidataQid;
            }

            return WikidataQid;
        }
    }

    /// <summary>
    /// Label for the <see cref="DisplayIdentifier"/> (e.g. "ISBN", "IMDb", "QID").
    /// </summary>
    public string DisplayIdentifierLabel
    {
        get
        {
            var mt = (MediaType ?? string.Empty).ToLowerInvariant();
            if (mt.Contains("book") || mt.Contains("epub") || mt.Contains("audio"))
            {
                if (Canonical("isbn") is not null)
                {
                    return "ISBN";
                }

                if (Canonical("asin") is not null)
                {
                    return "ASIN";
                }

                return "QID";
            }
            if (mt.Contains("movie") || mt.Contains("tv") || mt.Contains("video"))
            {
                if (Canonical("imdb_id") is not null)
                {
                    return "IMDb";
                }

                if (Canonical("tmdb_id") is not null)
                {
                    return "TMDB";
                }

                return "QID";
            }
            if (mt.Contains("music"))
            {
                return Canonical("musicbrainz_id") is not null ? "MusicBrainz" : "QID";
            }

            if (mt.Contains("comic"))
            {
                return Canonical(BridgeIdKeys.ComicVineId) is not null ? "Comic Vine" : "QID";
            }

            return "QID";
        }
    }

    public string? Year => MediaDateSemantics.ResolveOriginalYear(MediaType, Canonical);
    public string? CoverUrl => ResolvedCoverUrl ?? Canonical("cover_url") ?? Canonical("cover");
    public string? SquareUrl => Canonical("square_url") ?? Canonical("square");
    public string? BackgroundUrl => ResolvedBackgroundUrl ?? Canonical("background_url") ?? Canonical("background");
    public string? BannerUrl => ResolvedBannerUrl ?? Canonical("banner_url") ?? Canonical("banner");
    public string? HeroUrl => ResolvedHeroUrl ?? Canonical("hero_url") ?? Canonical("hero");
    public string? LogoUrl => ResolvedLogoUrl ?? Canonical("logo_url") ?? Canonical("logo");
    public string? CoverUrlSmall => Canonical("cover_url_s");
    public string? CoverUrlMedium => Canonical("cover_url_m");
    public string? CoverUrlLarge => Canonical("cover_url_l");
    public string? SquareUrlSmall => Canonical("square_url_s");
    public string? SquareUrlMedium => Canonical("square_url_m");
    public string? SquareUrlLarge => Canonical("square_url_l");
    public string? BackgroundUrlSmall => Canonical("background_url_s");
    public string? BackgroundUrlMedium => Canonical("background_url_m");
    public string? BackgroundUrlLarge => Canonical("background_url_l");
    public string? BannerUrlSmall => Canonical("banner_url_s");
    public string? BannerUrlMedium => Canonical("banner_url_m");
    public string? BannerUrlLarge => Canonical("banner_url_l");
    public string? CoverAspectClass => Canonical("cover_aspect_class");
    public string? SquareAspectClass => Canonical("square_aspect_class");
    public string? BackgroundAspectClass => Canonical("background_aspect_class");
    public string? BannerAspectClass => Canonical("banner_aspect_class");
    public int? CoverWidthPx => CanonicalInt("cover_width_px");
    public int? CoverHeightPx => CanonicalInt("cover_height_px");
    public int? SquareWidthPx => CanonicalInt("square_width_px");
    public int? SquareHeightPx => CanonicalInt("square_height_px");
    public int? BackgroundWidthPx => CanonicalInt("background_width_px");
    public int? BackgroundHeightPx => CanonicalInt("background_height_px");
    public int? BannerWidthPx => CanonicalInt("banner_width_px");
    public int? BannerHeightPx => CanonicalInt("banner_height_px");
    public string? ArtworkPrimaryHex => Canonical(MetadataFieldConstants.ArtworkPrimaryHex);
    public string? ArtworkSecondaryHex => Canonical(MetadataFieldConstants.ArtworkSecondaryHex);
    public string? ArtworkAccentHex => Canonical(MetadataFieldConstants.ArtworkAccentHex);
    public string? Description => Canonical("description");
    public string? DescriptionSource => Canonical("description_source");
    public string? Tldr => Canonical("tldr");
    public string? Genre => Canonical("genre");
    public string? Artist => Canonical("artist");
    public string? Album => Canonical("album");
    public string? Network => Canonical("network");
    public string? ShowName => Canonical("show_name") ?? Canonical("series");
    public string? SeasonNumber => Canonical("season_number");
    public string? EpisodeNumber => Canonical("episode_number");
    public string? TrackNumber => Canonical("track_number");

    /// <summary>Genre as an array of individual presentation values.</summary>
    public IReadOnlyList<string> Genres
    {
        get
        {
            var raw = Canonical("genre");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            return SplitPresentationList(raw);
        }
    }

    /// <summary>
    /// Genre QIDs matching <see cref="Genres"/> by position.
    /// </summary>
    public IReadOnlyList<string> GenreQids
    {
        get
        {
            var raw = Canonical("genre_qid");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            return SplitPresentationList(raw);
        }
    }
    public string? Narrator => Canonical("narrator");
    public string? Director => Canonical("director");
    public string? Series => Canonical("series");
    public string? SeriesPosition => Canonical("series_position");
    public string? Rating => Canonical("rating");
    public string? Pace => Canonical("pace");
    public string? Audience => Canonical("audience");
    public string? FictionalUniverseQid => Canonical("fictional_universe_qid");
    public int? WordCount => int.TryParse(Canonical("word_count"), out var wc) ? wc : null;
    public IReadOnlyList<string> Vibes => CanonicalList("vibe");
    public IReadOnlyList<string> Moods => CanonicalList("mood");
    public IReadOnlyList<string> Themes => CanonicalList("themes");

    public string? ReadingTimeDisplay
    {
        get
        {
            if (WordCount is not { } wc || wc <= 0)
            {
                return null;
            }

            var minutes = wc / 250;
            if (minutes < 60)
            {
                return $"{wc:N0} words \u2022 {minutes} min read";
            }

            var hours = minutes / 60;
            var rem = minutes % 60;
            var timeStr = rem > 0 ? $"{hours}h {rem}m" : $"{hours}h";
            return $"{wc:N0} words \u2022 {timeStr} reading time";
        }
    }

    private string? Canonical(string key)
    {
        var raw = CanonicalValues.FirstOrDefault(cv => cv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
        return raw;
    }

    private int? CanonicalInt(string key) =>
        int.TryParse(Canonical(key), out var value) && value > 0 ? value : null;

    private IReadOnlyList<string> CanonicalList(string key) =>
        CanonicalValues
            .Where(cv => cv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .SelectMany(cv => SplitPresentationList(cv.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> SplitPresentationList(string raw) =>
        raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>A single scored metadata field on a Work or Edition.</summary>
public sealed class CanonicalValueViewModel
{
    [System.Text.Json.Serialization.JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("last_scored_at")]
    public DateTimeOffset LastScoredAt { get; init; }
}
