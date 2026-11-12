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

    /// <summary>The triggers the list hides, as an array for queries.</summary>
    public static IReadOnlyList<string> HiddenTriggers { get; } = [.. WikidataOnlyTriggers];

    /// <summary>True for a review item the list never shows or counts (matched, only its Wikidata link is missing).</summary>
    public static bool IsHidden(string? trigger) => trigger is not null && WikidataOnlyTriggers.Contains(trigger);

    /// <summary>The group of a review item; <paramref name="hasBestGuess"/> is whether the Engine has one suggestion to offer for it.</summary>
    public static string GroupFor(string? trigger, bool hasBestGuess)
    {
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
        NeedsReviewGroups.FileProblem => 3,
        _ => 4,
    };
}
