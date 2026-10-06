using System.Globalization;
using MediaEngine.Domain;
using MediaEngine.Web.Components.LibraryItems;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Library;

/// <summary>Static display helpers for media library components.</summary>
public static class LibraryHelpers
{
    /// <summary>Returns the hex color for a LibraryStatus.</summary>
    public static string GetLibraryStatusColor(LibraryStatus status)
    {
        var p = PaletteProvider.Current.Status;
        return status switch
        {
            LibraryStatus.Verified => p.Verified,
            LibraryStatus.Provisional => p.Provisional,
            LibraryStatus.NeedsReview => p.NeedsReview,
            LibraryStatus.Quarantined => p.Quarantined,
            _ => p.Default,
        };
    }

    /// <summary>Returns the display label for a LibraryStatus.</summary>
    public static string GetLibraryStatusLabel(LibraryStatus status) => status switch
    {
        LibraryStatus.Verified => "Verified",
        LibraryStatus.Provisional => "Provisional",
        LibraryStatus.NeedsReview => "Needs Review",
        LibraryStatus.Quarantined => "Quarantined",
        _ => "Unknown",
    };

    /// <summary>Returns the icon for a LibraryStatus.</summary>
    public static string GetLibraryStatusIcon(LibraryStatus status) => status switch
    {
        LibraryStatus.Verified => AppMaterialIcons.Filled.CheckCircle,
        LibraryStatus.Provisional => AppMaterialIcons.Filled.Info,
        LibraryStatus.NeedsReview => AppMaterialIcons.Filled.Warning,
        LibraryStatus.Quarantined => AppMaterialIcons.Filled.Block,
        _ => AppMaterialIcons.Filled.HelpOutline,
    };

    /// <summary>Returns the hex color for a pipeline stage state.</summary>
    public static string GetStageColor(LibraryStageState state)
    {
        var p = PaletteProvider.Current.Pipeline;
        return state switch
        {
            LibraryStageState.Completed => p.Completed,
            LibraryStageState.Warning => p.Warning,
            LibraryStageState.Failed => p.Failed,
            LibraryStageState.Pending => p.Pending,
            _ => p.Pending,
        };
    }

    /// <summary>Returns the CSS shadow glow for a pipeline stage state.</summary>
    public static string GetStageShadow(LibraryStageState state)
    {
        var p = PaletteProvider.Current.Pipeline;
        return state switch
        {
            LibraryStageState.Completed => $"0 0 8px {HexToRgba(p.Completed, 0.3)}",
            LibraryStageState.Warning => $"0 0 8px {HexToRgba(p.Warning, 0.3)}",
            LibraryStageState.Failed => $"0 0 8px {HexToRgba(p.Failed, 0.3)}",
            _ => "none",
        };
    }

    /// <summary>Delegates to LibraryItemHelpers for media type icon.</summary>
    public static string GetMediaTypeIcon(string? mediaType) =>
        LibraryItemHelpers.GetMediaTypeIcon(mediaType);

    /// <summary>Delegates to LibraryItemHelpers for media type label.</summary>
    public static string FormatMediaType(string? mediaType) =>
        LibraryItemHelpers.FormatMediaType(mediaType);

    /// <summary>Converts hex color to rgba string. Returns fallback for non-hex input.</summary>
    public static string HexToRgba(string hex, double alpha)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return $"rgba(255,255,255,{alpha})";
        }
        // Already an rgba value  -  just return it
        if (hex.StartsWith("rgba", StringComparison.OrdinalIgnoreCase))
        {
            return hex;
        }

        hex = hex.TrimStart('#');
        if (hex.Length < 6)
        {
            return $"rgba(255,255,255,{alpha})";
        }

        try
        {
            var r = Convert.ToInt32(hex[..2], 16);
            var g = Convert.ToInt32(hex[2..4], 16);
            var b = Convert.ToInt32(hex[4..6], 16);
            return $"rgba({r},{g},{b},{alpha})";
        }
        catch
        {
            return $"rgba(255,255,255,{alpha})";
        }
    }

    /// <summary>Formats file size in human-readable form.</summary>
    public static string FormatFileSize(long? bytes)
    {
        if (bytes is null or 0)
        {
            return " - ";
        }

        return bytes.Value switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes.Value / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes.Value / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes.Value / (1024.0 * 1024.0 * 1024.0):F2} GB",
        };
    }

    /// <summary>Normalizes mixed duration metadata into a single seconds value for sorting and display.</summary>
    public static long? NormalizeDurationSeconds(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (TryParseDurationSeconds(candidate, out var seconds))
            {
                return seconds;
            }
        }

        return null;
    }

    /// <summary>Formats a normalized duration into m:ss or h:mm:ss, defaulting missing values to 0:00.</summary>
    public static string FormatDuration(long? durationSeconds, string? fallback = null)
    {
        if (durationSeconds is > 0)
        {
            var totalSeconds = durationSeconds.Value;
            var hours = totalSeconds / 3600;
            var minutes = (totalSeconds % 3600) / 60;
            var seconds = totalSeconds % 60;
            return hours > 0
                ? $"{hours}:{minutes:00}:{seconds:00}"
                : $"{minutes}:{seconds:00}";
        }

        if (TryParseDurationSeconds(fallback, out var parsedFallback))
        {
            return FormatDuration(parsedFallback);
        }

        return "0:00";
    }

    /// <summary>Formats a rating into a simple star display when numeric data is available.</summary>
    public static string FormatRating(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return "-";
        }

        if (int.TryParse(rating, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wholeStars))
        {
            wholeStars = Math.Clamp(wholeStars, 0, 5);
            return wholeStars == 0 ? "-" : new string('*', wholeStars);
        }

        if (double.TryParse(rating, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            var rounded = (int)Math.Round(Math.Clamp(numeric, 0d, 5d), MidpointRounding.AwayFromZero);
            return rounded == 0 ? "-" : new string('*', rounded);
        }

        return rating;
    }

    private static bool TryParseDurationSeconds(string? value, out long seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is 2 or 3)
            {
                long total = 0;
                long multiplier = 1;
                for (var index = parts.Length - 1; index >= 0; index--)
                {
                    if (!long.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var segment))
                    {
                        return false;
                    }

                    total += segment * multiplier;
                    multiplier *= 60;
                }

                seconds = Math.Max(0, total);
                return true;
            }

            if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var parsedTimeSpan))
            {
                seconds = Math.Max(0, (long)Math.Round(parsedTimeSpan.TotalSeconds, MidpointRounding.AwayFromZero));
                return true;
            }
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) || numeric < 0)
        {
            return false;
        }

        if (numeric >= 10000 && Math.Abs(numeric % 1000d) < 0.0001d)
        {
            numeric /= 1000d;
        }

        seconds = Math.Max(0, (long)Math.Round(numeric, MidpointRounding.AwayFromZero));
        return true;
    }

    /// <summary>Returns provider lookup buttons based on media type, matching configured slot providers.</summary>
    public static string[] GetProviderButtons(string? mediaType) => mediaType?.ToUpperInvariant() switch
    {
        "MOVIE" or "MOVIES" => ["TMDB"],
        "TV" => ["TheTVDB", "TMDB"],
        "BOOK" or "BOOKS" or "EPUB" => ["Open Library"],
        "AUDIOBOOK" or "AUDIOBOOKS" => ["Apple API"],
        "MUSIC" => ["MusicBrainz"],
        "COMICS" or "COMIC" => [],
        _ => [],
    };

    /// <summary>Returns the sort parameter string for the API.</summary>
    public static string GetSortParam(string sortBy) => sortBy switch
    {
        "oldest" => "oldest",
        "title" => "title",
        "title_desc" => "-title",
        "confidence" => "-confidence",
        "confidence_asc" => "confidence",
        "presence" => "-presence",
        "presence_asc" => "presence",
        "name" => "name",
        "name_desc" => "-name",
        _ => "newest",
    };

    /// <summary>
    /// Returns a plain-language explanation of a review trigger for new users.
    /// Written from the perspective of someone who has never seen Tuvima before.
    /// </summary>
    public static string HumanizeReviewTrigger(string? trigger) => trigger switch
    {
        "RetailMatchFailed" => "No provider could find a match for this file. You can try searching manually.",
        "WikidataBridgeFailed" => "This item couldn't be linked to a known entry on Wikidata.",
        "LowConfidence" => "The match confidence is too low to confirm automatically. Please review the suggested matches.",
        "MultipleQidMatches" => "Multiple possible matches were found. Please pick the correct one.",
        "RetailMatchAmbiguous" => "A possible match was found but it's not certain. Please verify.",
        "AmbiguousMediaType" => "The file type is ambiguous  -  it could be music or an audiobook.",
        "MissingQid" => "A retail match was found but it hasn't been linked to Wikidata yet.",
        "StagedUnidentifiable" => "This file couldn't be identified. Check the filename and embedded metadata.",
        "PlaceholderTitle" => "The title appears to be a placeholder (e.g. 'Unknown'). Please provide the real title.",
        "ArtworkUnconfirmed" => "Cover art was found via text search and may not be accurate.",
        "LanguageMismatch" => "This file's language doesn't match your library's configured language.",
        "UserReport" => "You flagged this item for review.",
        "RootWatchFolder" => "This file was placed in the root watch folder  -  its media type couldn't be determined.",
        "UserFixMatch" => "You requested to fix the match for this item.",
        "WritebackFailed" => "Re-tagging this file failed. It may be locked, corrupt, or unwritable.",
        _ => "This item needs your attention.",
    };

    /// <summary>Returns a human-readable label for a review trigger code.</summary>
    public static string GetReviewTriggerLabel(string? trigger) => trigger switch
    {
        "StagedUnidentifiable" => "This file could not be identified automatically",
        "PlaceholderTitle" => "The title looks like a placeholder or temporary name",
        "WikidataBridgeFailed" => "Wikidata lookup failed after retail match",
        "RetailMatchFailed" => "No retail provider could find a match",
        "MetadataConflict" => "Multiple sources disagree on this item's metadata",
        "LowConfidence" => "The best match has low confidence",
        "LanguageMismatch" => "File language differs from your library language",
        "DuplicateDetected" => "This may be a duplicate of another item",
        "MediaTypeAmbiguous" => "Could not determine the media type",
        "MissingQid" => "No Wikidata identity found for this item",
        "MultipleQidMatches" => "Multiple possible Wikidata matches found",
        "RootWatchFolder" => "Dropped into root watch folder  -  please confirm the media type",
        "WritebackFailed" => "Re-tag failed  -  file may be locked or corrupt",
        _ => trigger ?? "This item needs review",
    };

    /// <summary>
    /// Converts a technical source/provider name or GUID to a human-readable display name.
    /// </summary>
    public static string FormatSourceName(string? source) =>
        ProviderCatalogueService.FormatSourceName(source);

    /// <summary>
    /// Converts a provider GUID to a human-readable display name.
    /// </summary>
    public static string FormatProviderName(Guid providerId) =>
        ProviderCatalogueService.FormatProviderName(providerId);

    /// <summary>
    /// Returns true if the given provider GUID represents a file/local source
    /// (local_processor or library_scanner).
    /// </summary>
    public static bool IsFileSource(Guid providerId) =>
        WellKnownProviders.IsFileSource(providerId);

    /// <summary>
    /// Returns true if the given provider GUID represents a user manual source.
    /// </summary>
    public static bool IsUserSource(Guid providerId) =>
        WellKnownProviders.IsUserSource(providerId);

    /// <summary>
    /// Builds a clickable external URL for a given bridge ID key and value.
    /// Returns null if no URL template is known for the key.
    /// </summary>
    /// <remarks>
    /// The URL switch arms here intentionally mirror the hardcoded fallback in
    /// <c>ProviderCatalogueService.GetExternalUrl</c>. LibraryHelpers is a static class
    /// and cannot inject services, so both must stay in sync if new bridge IDs are added.
    /// The primary source of truth is <c>ProviderCatalogueService.GetExternalUrl</c>,
    /// which prefers live catalogue URL templates over the hardcoded fallbacks.
    /// </remarks>
    public static (string Label, string Url)? BuildProviderUrl(string key, string value, string? mediaType = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return key.ToLowerInvariant() switch
        {
            BridgeIdKeys.TmdbId when (mediaType ?? "").Contains("TV", StringComparison.OrdinalIgnoreCase)
                => ("View on TMDB", $"https://www.themoviedb.org/tv/{value}"),
            BridgeIdKeys.TmdbId
                => ("View on TMDB", $"https://www.themoviedb.org/movie/{value}"),
            BridgeIdKeys.TvdbId when value.All(char.IsDigit)
                => ("View on TheTVDB", $"https://thetvdb.com/series/{value}"),
            BridgeIdKeys.TvdbSeasonId when value.All(char.IsDigit)
                => ("View on TheTVDB", $"https://thetvdb.com/seasons/{value}"),
            BridgeIdKeys.TvdbEpisodeId when value.All(char.IsDigit)
                => ("View on TheTVDB", $"https://thetvdb.com/episodes/{value}"),
            BridgeIdKeys.OpenLibraryId or "olid"
                => ("View on Open Library", $"https://openlibrary.org/works/{value}"),
            BridgeIdKeys.MusicBrainzId
                => ("View on MusicBrainz", $"https://musicbrainz.org/release/{value}"),
            BridgeIdKeys.WikidataQid when value.StartsWith("Q", StringComparison.OrdinalIgnoreCase)
                => ("View on Wikidata", $"https://www.wikidata.org/wiki/{value}"),
            BridgeIdKeys.ImdbId when value.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                => ("View on IMDb", $"https://www.imdb.com/title/{value}"),
            BridgeIdKeys.AppleBooksId
                => ("View on Apple Books", $"https://books.apple.com/book/id{value}"),
            _ => null,
        };
    }

    /// <summary>Formats a claim key (snake_case field name) into a human-readable label.</summary>
    public static string FormatClaimKey(string key) => key.ToLowerInvariant() switch
    {
        "title" => "Title",
        "original_title" => "Original Title",
        "author" => "Author",
        "director" => "Director",
        "artist" => "Artist",
        "narrator" => "Narrator",
        "year" => "Year",
        "genre" => "Genre",
        "series" => "Series",
        "series_position" => "Series #",
        "description" => "Description",
        "cover_url" => "Cover Art",
        "isbn" => "ISBN",
        "isbn_13" => "ISBN-13",
        "isbn_10" => "ISBN-10",
        "asin" => "ASIN",
        "tmdb_id" => "TMDB ID",
        "tvdb_id" => "TheTVDB Series ID",
        "tvdb_season_id" => "TheTVDB Season ID",
        "tvdb_episode_id" => "TheTVDB Episode ID",
        "imdb_id" => "IMDb ID",
        "musicbrainz_id" => "MusicBrainz ID",
        "open_library_id" => "Open Library ID",
        "apple_books_id" => "Apple Books ID",
        "apple_music_id" => "Apple Music ID",
        "apple_music_collection_id" => "Apple Album ID",
        "apple_artist_id" => "Apple Artist ID",
        "comic_vine_id" => "Comic Vine ID",
        "wikidata_qid" => "Wikidata QID",
        "show_name" => "Show Name",
        "episode_title" => "Episode Title",
        "season_number" => "Season",
        "episode_number" => "Episode",
        "track_number" => "Track #",
        "album" => "Album",
        "composer" => "Composer",
        "rating" => "Rating",
        "runtime" => "Runtime",
        "duration" => "Duration",
        "publisher" => "Publisher",
        "language" => "Language",
        "page_count" => "Pages",
        "barcode" => "Barcode",
        "store_date" => "Store Date",
        "explicit" => "Explicit",
        "media_type" => "Media Type",
        "illustrator" => "Illustrator",
        "screenwriter" => "Screenwriter",
        "cast_member" => "Actor",
        _ => string.Join(' ', key.Split('_').Select(w =>
                                           w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w)),
    };

    /// <summary>Returns the brand colour for a media type string, matching stats bar colours.</summary>
    public static string GetMediaTypeColor(string? mediaType)
    {
        var p = PaletteProvider.Current.MediaType;
        var t = (mediaType ?? "").ToLowerInvariant();
        if (t.Contains("movie") || t.Contains("video"))
        {
            return p.Movie;
        }

        if (t.Contains("book") && !t.Contains("audio"))
        {
            return p.Book;
        }

        if (t.Contains("audiobook"))
        {
            return p.Audiobook;
        }

        if (t == "tv")
        {
            return p.TV;
        }

        if (t.Contains("music"))
        {
            return p.Music;
        }

        if (t.Contains("comic"))
        {
            return p.Comic;
        }

        return p.Unknown;
    }
}


