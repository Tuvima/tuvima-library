using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Editing;

public sealed record ReviewKnownFact(string Label, string Value);

public sealed record ReviewInspectorPresentation(
    string Summary,
    string QueueExplanation,
    IReadOnlyList<string> FailureReasons,
    IReadOnlyList<ReviewKnownFact> KnownFacts,
    string Guidance);

public static class ReviewIssuePresentationBuilder
{
    private static readonly string[] PreferredFactKeys =
    [
        "title", "author", "creator", "writer", "artist", "album", "narrator", "director", "series",
        "year", "release_date", "publication_date", "show_name", "season_number", "episode_number",
        "issue_number", "track_number", "disc_number",
        "runtime", "duration", "duration_sec", "language", "file_name",
    ];

    public static ReviewInspectorPresentation Build(ReviewItemViewModel item)
    {
        var issue = ReviewIssueClassifier.Classify(item.Trigger);
        var media = MediaName(item.MediaType);
        var reasons = FailureReasons(item, media);
        var facts = KnownFacts(item);

        return new ReviewInspectorPresentation(
            Summary(item, media, issue.Explanation),
            QueueExplanation(item),
            reasons,
            facts,
            Guidance(item, media));
    }

    private static string Summary(ReviewItemViewModel item, string media, string fallback) => item.Trigger switch
    {
        "RetailMatchFailed" => $"Tuvima Library searched the configured providers but couldn't confirm a reliable match for this {media}, so it wasn't linked automatically.",
        "RetailMatchAmbiguous" => $"Tuvima Library found more than one possible match for this {media} and couldn't safely choose between them.",
        "MovieMatchedAsTv" => "No movie matched this file, but TMDB lists a TV series with the same title and year. It stays in Movies with its file details until you decide.",
        "PlaceholderTitle" => $"Tuvima Library could not find enough identifying information in this {media} to search for it reliably.",
        "StagedUnidentifiable" => $"Tuvima Library could not determine a reliable identity for this {media}, so it stopped before organizing it.",
        "MissingQid" or "WikidataBridgeFailed" => $"The retail edition was identified, but Tuvima Library couldn't determine the shared canonical identity for this {media}.",
        "MultipleQidMatches" => $"Tuvima Library found several possible canonical identities for this {media} and needs you to choose the correct one.",
        "MetadataConflict" => $"Tuvima Library found conflicting metadata for this {media} and could not safely choose which value to keep.",
        "AmbiguousMediaType" or "RootWatchFolder" => "Tuvima Library could not confidently decide which part of the library this file belongs in.",
        "WritebackFailed" => "Tuvima Library identified this item, but could not save the updated metadata to its physical file.",
        _ => fallback,
    };

    private static string QueueExplanation(ReviewItemViewModel item) => item.Trigger switch
    {
        "RetailMatchFailed" => "No reliable match was found in the configured providers",
        "RetailMatchAmbiguous" => "Multiple possible retail matches need comparison",
        "MovieMatchedAsTv" => "TMDB lists this film as a TV title",
        "PlaceholderTitle" => "Missing title, creator, or identifying information",
        "StagedUnidentifiable" => "Not enough reliable information to identify the item",
        "MissingQid" or "WikidataBridgeFailed" => "Retail match found, but canonical identity is missing",
        "MultipleQidMatches" => "Multiple canonical identities need comparison",
        "MetadataConflict" => "Local and provider metadata disagree",
        "AmbiguousMediaType" or "RootWatchFolder" => "The correct media type could not be determined",
        "WritebackFailed" => "Updated metadata could not be written to the file",
        "ArtworkUnconfirmed" => "Artwork came from a less precise search",
        _ => ReviewIssueClassifier.Classify(item.Trigger).Explanation,
    };

    private static IReadOnlyList<string> FailureReasons(ReviewItemViewModel item, string media)
    {
        var reasons = item.Trigger switch
        {
            "RetailMatchFailed" => new[] { "None of the configured providers returned a match reliable enough to use automatically." },
            "RetailMatchAmbiguous" => new[] { "Several provider results were plausible, but no single result was safe to select automatically." },
            "MovieMatchedAsTv" => new[] { "The movie search found nothing, but a short TV series with the same title and year exists on TMDB." },
            "PlaceholderTitle" => new[] { "A usable title or identifier was not detected in the local file metadata." },
            "StagedUnidentifiable" => new[] { "The available local evidence scored below the safe identification threshold." },
            "MissingQid" or "WikidataBridgeFailed" => new[] { "The retained retail identifiers did not resolve to a confirmed canonical identity." },
            "MultipleQidMatches" => new[] { "More than one canonical identity was compatible with the retained retail record." },
            "MetadataConflict" => new[] { "Two or more metadata sources supplied similarly credible but conflicting values." },
            "AmbiguousMediaType" or "RootWatchFolder" => new[] { "The file type and folder context were compatible with more than one media type." },
            "WritebackFailed" => new[] { "The metadata write operation failed after the configured retry attempts." },
            "LanguageMismatch" => new[] { "The embedded language conflicts with the configured library language." },
            "ArtworkUnconfirmed" => new[] { "The artwork was found through a broad text search and could not be confirmed from a precise identifier." },
            _ => new[] { $"The ingestion pipeline could not safely complete the decision for this {media}." },
        };

        return reasons;
    }

    private static IReadOnlyList<ReviewKnownFact> KnownFacts(ReviewItemViewModel item)
    {
        var facts = new List<ReviewKnownFact>();
        Add(facts, "Title", item.EntityTitle);
        Add(facts, "Media type", MediaLabel(item.MediaType, item.EntityType));

        foreach (var key in PreferredFactKeys)
        {
            if (key == "title" || !item.DetectedFacts.TryGetValue(key, out var value))
            {
                continue;
            }

            Add(facts, Label(key), FormatValue(key, value));
        }

        foreach (var identifier in item.BridgeIdentifiers.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            Add(facts, Label(identifier.Key), identifier.Value);
        }

        Add(facts, "Confidence", item.ConfidenceScore is { } score ? $"{score:P0}" : null);
        return facts.DistinctBy(fact => (fact.Label, fact.Value)).Take(12).ToList();
    }

    private static string Guidance(ReviewItemViewModel item, string media) => item.Trigger switch
    {
        "RetailMatchFailed" => $"Search for the correct retail record. Adding {IdentityHints(item.MediaType)} may improve the results.",
        "RetailMatchAmbiguous" => $"Compare the possible matches using {IdentityHints(item.MediaType)} and choose the record that represents this {media}.",
        "MovieMatchedAsTv" => "Choose Move to TV to file it under the TV show as a special, Search again to look for a different match, or Keep as unmatched film.",
        "PlaceholderTitle" or "StagedUnidentifiable" => $"Add the missing {IdentityHints(item.MediaType)} so Tuvima Library can search again.",
        "MissingQid" or "WikidataBridgeFailed" or "MultipleQidMatches" => "Search for the shared work or person identity while keeping the confirmed retail edition unchanged.",
        "MetadataConflict" => $"Compare the conflicting values using {IdentityHints(item.MediaType)} and choose which information Tuvima Library should keep.",
        "AmbiguousMediaType" or "RootWatchFolder" => "Choose the correct media type so Tuvima Library can continue with the appropriate matching workflow.",
        "WritebackFailed" => "Check that the file is available and writable, then retry saving its metadata.",
        _ => "Open the focused review to inspect the available evidence and make the missing decision.",
    };

    private static string IdentityHints(string? mediaType) => ReviewTargetResolver.NormalizeMediaType(mediaType) switch
    {
        "Comics" => "series, issue number, creator, or publication date",
        "Audiobooks" => "title, author, narrator, ISBN, ASIN, or year",
        "Movies" => "title, release year, director, or runtime",
        "TV" => "show, season, episode, or air date",
        "Music" => "title, artist, album, or track number",
        _ => "title, author, ISBN, edition, or year",
    };

    private static string MediaName(string? mediaType) => MediaLabel(mediaType, "item").ToLowerInvariant();

    private static string MediaLabel(string? mediaType, string? fallback) =>
        string.IsNullOrWhiteSpace(mediaType) ? fallback ?? "Item" : mediaType
            .Replace("Audiobooks", "Audiobook", StringComparison.OrdinalIgnoreCase)
            .Replace("Movies", "Movie", StringComparison.OrdinalIgnoreCase)
            .Replace("Books", "Book", StringComparison.OrdinalIgnoreCase)
            .Replace("Comics", "Comic", StringComparison.OrdinalIgnoreCase);

    private static void Add(List<ReviewKnownFact> facts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            facts.Add(new ReviewKnownFact(label, value.Trim()));
        }
    }

    private static string Label(string key) => key.ToLowerInvariant() switch
    {
        "isbn_13" => "ISBN-13",
        "isbn_10" => "ISBN-10",
        "isbn" => "ISBN",
        "asin" => "ASIN",
        "tmdb_id" => "TMDB ID",
        "imdb_id" => "IMDb ID",
        "tvdb_id" => "TVDB ID",
        "wikidata_qid" => "Wikidata ID",
        "show_name" => "Show",
        "season_number" => "Season",
        "episode_number" => "Episode",
        "issue_number" => "Issue number",
        "track_number" => "Track",
        "disc_number" => "Disc",
        "release_date" => "Release date",
        "publication_date" => "Publication date",
        "file_name" => "Filename",
        "duration_sec" => "Duration",
        _ => string.Join(' ', key.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..])),
    };

    private static string FormatValue(string key, string value)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var numeric))
        {
            return value;
        }

        var seconds = key.ToLowerInvariant() switch
        {
            "duration" when numeric > 10_000 => numeric / 1000d,
            "duration_sec" => numeric,
            _ => 0,
        };
        if (seconds <= 0)
        {
            return value;
        }

        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}
