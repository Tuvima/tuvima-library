using MediaEngine.Contracts.Review;
using MediaEngine.Domain.Constants;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// The one rule that puts a Needs Review row in a group (Needs Review redesign D1): what the person must do with it. File problems are the
/// reasons the file itself could not be read, written to, named or changed; Choose is a question only a person can answer; Ready is any
/// other item that has exactly one suggestion (an ambiguous retail match with only one candidate worth offering counts: it has its one
/// answer to confirm instead of choices); everything else (including every unmatched file and any reason not listed here) is
/// No match found.
/// </summary>
public static class NeedsReviewGrouping
{
    private static readonly HashSet<string> FileProblemTriggers = new(StringComparer.Ordinal)
    {
        ReviewTrigger.MediaInspectionFailed,
        ReviewTrigger.WritebackFailed,
        ReviewTrigger.NameCollision,
        ReviewTrigger.BatchEditFailed,
    };

    private static readonly HashSet<string> ChooseTriggers = new(StringComparer.Ordinal)
    {
        ReviewTrigger.MovieMatchedAsTv,
        ReviewTrigger.RetailMatchAmbiguous,
        ReviewTrigger.AmbiguousSeriesMembership,
        ReviewTrigger.AmbiguousMediaType,
        ReviewTrigger.RootWatchFolder,
        ReviewTrigger.MetadataConflict,
    };

    // Items matched to a provider that only lack their Wikidata link (Needs Review redesign D10): the Engine keeps retrying quietly, so the
    // list neither shows nor counts them. They stay stored and pending.
    private static readonly HashSet<string> WikidataOnlyTriggers = new(StringComparer.Ordinal)
    {
        ReviewTrigger.MissingQid,
        ReviewTrigger.WikidataBridgeFailed,
    };

    // Books and audiobooks are the exception (spec needs-review-canonical-filter D1, D2): their missing Wikidata link is shown, in its own group.
    private static readonly string[] CanonicalShownMediaTypes = ["Books", "Audiobooks"];

    /// <summary>The triggers the list hides for every media type except books and audiobooks, as an array for queries.</summary>
    public static IReadOnlyList<string> HiddenTriggers { get; } = [.. WikidataOnlyTriggers];

    /// <summary>The media types whose Wikidata-only items the list shows, as an array for queries.</summary>
    public static IReadOnlyList<string> CanonicalShownTypes => CanonicalShownMediaTypes;

    /// <summary>
    /// The SQL condition (for a <c>review_queue</c> row aliased <paramref name="alias"/>) that keeps a row out of the list: a Wikidata-only
    /// trigger on anything but a book or audiobook. Needs the query parameters <c>@hidden</c> and <c>@canonicalTypes</c>.
    /// </summary>
    public static string NotHiddenSql(string alias) => $"""
        ({alias}.trigger NOT IN @hidden OR EXISTS (
            SELECT 1 FROM works wcm -- readiness: media type lookup for a review row, not a display query
            WHERE wcm.media_type IN @canonicalTypes
              AND (wcm.id = {alias}.entity_id
                   OR wcm.id IN (SELECT e.work_id FROM media_assets mac JOIN editions e ON e.id = mac.edition_id WHERE mac.id = {alias}.entity_id))))
        """;

    /// <summary>True for a trigger that means "matched, only the Wikidata link is missing".</summary>
    public static bool IsWikidataOnly(string? trigger) => trigger is not null && WikidataOnlyTriggers.Contains(trigger);

    /// <summary>True for a review item the list never shows or counts (matched, only its Wikidata link is missing, and not a book or audiobook).</summary>
    public static bool IsHidden(string? trigger, string? mediaType) =>
        IsWikidataOnly(trigger) && !ShowsCanonicalMissing(mediaType);

    /// <summary>True for a media type whose missing Wikidata link the list shows.</summary>
    public static bool ShowsCanonicalMissing(string? mediaType) =>
        mediaType is not null && CanonicalShownMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);

    /// <summary>The group of a review item; <paramref name="hasBestGuess"/> is whether the Engine has one suggestion to offer for it.</summary>
    public static string GroupFor(string? trigger, bool hasBestGuess, string? mediaType = null)
    {
        if (IsWikidataOnly(trigger) && ShowsCanonicalMissing(mediaType))
        {
            return NeedsReviewGroups.CanonicalMissing;
        }

        if (trigger is not null && FileProblemTriggers.Contains(trigger))
        {
            return NeedsReviewGroups.FileProblem;
        }

        if (hasBestGuess && string.Equals(trigger, ReviewTrigger.RetailMatchAmbiguous, StringComparison.Ordinal))
        {
            return NeedsReviewGroups.Ready;
        }

        if (trigger is not null && ChooseTriggers.Contains(trigger))
        {
            return NeedsReviewGroups.Choose;
        }

        return hasBestGuess ? NeedsReviewGroups.Ready : NeedsReviewGroups.NoMatch;
    }

    /// <summary>Where a group sits in the page: Ready first, then Choose, No match, File problems. An unknown group sorts last.</summary>
    public static int OrderOf(string group) => group switch
    {
        NeedsReviewGroups.Ready => 0,
        NeedsReviewGroups.Choose => 1,
        NeedsReviewGroups.NoMatch => 2,
        NeedsReviewGroups.CanonicalMissing => 3,
        NeedsReviewGroups.FileProblem => 4,
        _ => 5,
    };
}
