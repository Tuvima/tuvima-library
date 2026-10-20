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
        "RetailMatchFailed" => $"Library searched the configured providers but couldn't confirm a reliable match for this {media}, so it wasn't linked automatically.",
        "RetailMatchAmbiguous" => $"Library found more than one possible match for this {media} and couldn't safely choose between them.",
        "MovieMatchedAsTv" => "No movie matched this file, but TMDB lists a TV series with the same title and year. It stays in Movies with its file details until you decide.",
        "FromMerge" => $"This {media} came from a folder merge, but Library could not name it yet. It was put in the From merge folder and is waiting for you to confirm what it is.",
        "NameCollision" => $"While Library organised this folder, another file already had the standard name for this {media}. Neither was overwritten; this one kept a numbered name.",
        "PlaceholderTitle" => $"Library could not find enough identifying information in this {media} to search for it reliably.",
        "StagedUnidentifiable" => $"Library could not determine a reliable identity for this {media}, so it stopped before organizing it.",
        "MissingQid" or "WikidataBridgeFailed" => $"The retail edition was identified, but Library couldn't determine the shared canonical identity for this {media}.",
        "MultipleQidMatches" => $"Library found several possible canonical identities for this {media} and needs you to choose the correct one.",
        "MetadataConflict" => $"Library found conflicting metadata for this {media} and could not safely choose which value to keep.",
        "AmbiguousMediaType" or "RootWatchFolder" => "Library could not confidently decide which part of the library this file belongs in.",
        "WritebackFailed" => "Library identified this item, but could not save the updated metadata to its physical file.",
        "MediaInspectionFailed" => $"This {media} couldn't be read. It may be damaged or incomplete.",
        "AmbiguousSeriesMembership" => $"Wikidata lists this {media} in the main sequence of more than one Series and nothing separates them, so Library did not guess. It is not on any Series for now.",
        _ => fallback,
    };

    private static string QueueExplanation(ReviewItemViewModel item) => item.Trigger switch
    {
        "RetailMatchFailed" => "No reliable match was found in the configured providers",
        "RetailMatchAmbiguous" => "Multiple possible retail matches need comparison",
        "MovieMatchedAsTv" => "TMDB lists this film as a TV title",
        "FromMerge" => "From merge: not named yet",
        "NameCollision" => "Two different files wanted the same name",
        "PlaceholderTitle" => "Missing title, creator, or identifying information",
        "StagedUnidentifiable" => "Not enough reliable information to identify the item",
        "MissingQid" or "WikidataBridgeFailed" => "Retail match found, but canonical identity is missing",
        "MultipleQidMatches" => "Multiple canonical identities need comparison",
        "MetadataConflict" => "Local and provider metadata disagree",
        "AmbiguousMediaType" or "RootWatchFolder" => "The correct media type could not be determined",
        "WritebackFailed" => "Updated metadata could not be written to the file",
        "MediaInspectionFailed" => "The file couldn't be read",
        "AmbiguousSeriesMembership" => "More than one Series claims this",
        _ => ReviewIssueClassifier.Classify(item.Trigger).Explanation,
    };

    private static IReadOnlyList<string> FailureReasons(ReviewItemViewModel item, string media)
    {
        var reasons = item.Trigger switch
        {
            "RetailMatchFailed" => new[] { "None of the configured providers returned a match reliable enough to use automatically." },
            "RetailMatchAmbiguous" => new[] { "Several provider results were plausible, but no single result was safe to select automatically." },
            "MovieMatchedAsTv" => new[] { "The movie search found nothing, but a short TV series with the same title and year exists on TMDB." },
            "FromMerge" => new[] { "The file was not identified, was still waiting for a person, or had no confirmed title, so it has no standard name yet." },
            "NameCollision" => new[] { "Two files with different contents were both identified as the same title, so they would have had the same file name." },
            "PlaceholderTitle" => new[] { "A usable title or identifier was not detected in the local file metadata." },
            "StagedUnidentifiable" => new[] { "The available local evidence scored below the safe identification threshold." },
            "MissingQid" or "WikidataBridgeFailed" => new[] { "The retained retail identifiers did not resolve to a confirmed canonical identity." },
            "MultipleQidMatches" => new[] { "More than one canonical identity was compatible with the retained retail record." },
            "MetadataConflict" => new[] { "Two or more metadata sources supplied similarly credible but conflicting values." },
            "AmbiguousMediaType" or "RootWatchFolder" => new[] { "The file type and folder context were compatible with more than one media type." },
            "WritebackFailed" => new[] { "The metadata write operation failed after the configured retry attempts." },
            "MediaInspectionFailed" => new[] { "The media tool is working, but it could not read this file's details." },
            "LanguageMismatch" => new[] { "The embedded language conflicts with the configured library language." },
            "AmbiguousSeriesMembership" => new[] { "Two Series of the same kind of media both list it in their main sequence, and neither is more specific." },
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
        "FromMerge" => "Confirm what this is (search for the right match, or add the missing details). Once it is confirmed, Library names it and files it in the target folder.",
        "NameCollision" => "Check whether these are two different versions (for example a theatrical and an extended cut) or one is a wrong match, then rename or fix the match.",
        "PlaceholderTitle" or "StagedUnidentifiable" => $"Add the missing {IdentityHints(item.MediaType)} so Library can search again.",
        "MissingQid" or "WikidataBridgeFailed" or "MultipleQidMatches" => "Search for the shared work or person identity while keeping the confirmed retail edition unchanged.",
        "MetadataConflict" => $"Compare the conflicting values using {IdentityHints(item.MediaType)} and choose which information Library should keep.",
        "AmbiguousMediaType" or "RootWatchFolder" => "Choose the correct media type so Library can continue with the appropriate matching workflow.",
        "WritebackFailed" => "Check that the file is available and writable, then retry saving its metadata.",
        "MediaInspectionFailed" => "Check that the file plays on your computer. If it is damaged, replace it with a good copy.",
        "AmbiguousSeriesMembership" => $"Open this {media} and use Matching to choose the Series it belongs to. Until then it is not on either Series.",
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
        // A detected value that is not a real length of time (NaN, infinity, or more than a TimeSpan can hold) is shown as
        // detected; TimeSpan.FromSeconds throws on those and one bad fact must not stop the whole Needs Review page.
        if (seconds <= 0 || !double.IsFinite(seconds) || seconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            return value;
        }

        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}
