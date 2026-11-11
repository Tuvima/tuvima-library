using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Editing;

public enum MediaEditorIdentityIntent
{
    None,
    EditLocalDetails,
    FixRetailMatch,
    ConfirmRetailMatch,
    FixWikidataMatch,
    ConfirmWikidataMatch,
    MarkWikidataMissing,
    ReviewClassification,
    ResolveWriteback,
}

public sealed record ReviewEditorTarget(
    string InitialTab,
    string CanonicalTargetGroup,
    string FocusField,
    string Summary,
    MediaEditorIdentityIntent Intent,
    string PrimaryActionLabel);

public static class ReviewTargetResolver
{
    public static ReviewEditorTarget Resolve(string? mediaType, string? reviewTrigger)
    {
        var target = ResolveForMediaType(mediaType, reviewTrigger);
        return string.Equals((reviewTrigger ?? string.Empty).Trim(), "AmbiguousSeriesMembership", StringComparison.Ordinal)
            ? target with
            {
                // Two Series claim this item equally: the answer is the Series block in Matching, on the same tab as the match.
                InitialTab = "links",
                FocusField = "series",
                Summary = "Choose the Series this item belongs to.",
                PrimaryActionLabel = "Choose Series",
            }
            : target;
    }

    private static ReviewEditorTarget ResolveForMediaType(string? mediaType, string? reviewTrigger)
    {
        var normalizedType = NormalizeMediaType(mediaType);
        var normalizedTrigger = (reviewTrigger ?? string.Empty).Trim();
        var intent = ResolveIntent(normalizedTrigger);
        var initialTab = ResolveInitialTab(intent);
        var actionLabel = ResolvePrimaryActionLabel(intent);

        if (normalizedType == "Music")
        {
            return new(initialTab, "track", "title", BuildSummary(intent, "track and album"), intent, actionLabel);
        }

        if (normalizedType == "Audiobooks")
        {
            return normalizedTrigger switch
            {
                "QidNoMatch" or "MissingQid" or "WikidataBridgeFailed" or "MultipleQidMatches"
                    => new(initialTab, "narrator", "narrator", BuildSummary(intent, "narrator"), intent, actionLabel),
                _ => new(initialTab, "audiobook_identity", "title", BuildSummary(intent, "audiobook"), intent, actionLabel),
            };
        }

        if (normalizedType == "TV")
        {
            return new(initialTab, "show_episode", "show_name", BuildSummary(intent, "show and episode"), intent, actionLabel);
        }

        if (normalizedType == "Comics")
        {
            return new(initialTab, "issue", "series", BuildSummary(intent, "series and issue"), intent, actionLabel);
        }

        if (normalizedType == "Movies")
        {
            return new(initialTab, "movie_identity", "title", BuildSummary(intent, "movie"), intent, actionLabel);
        }

        return new(initialTab, "book_identity", "title", BuildSummary(intent, "item"), intent, actionLabel);
    }

    public static string NormalizeMediaType(string? mediaType) =>
        (mediaType ?? string.Empty).Trim() switch
        {
            "Book" => "Books",
            "Comic" => "Comics",
            "" => "Books",
            var value => value,
        };

    private static MediaEditorIdentityIntent ResolveIntent(string trigger) =>
        trigger switch
        {
            "RetailMatchAmbiguous" => MediaEditorIdentityIntent.ConfirmRetailMatch,
            "RetailMatchFailed" or "UserFixMatch"
                => MediaEditorIdentityIntent.FixRetailMatch,
            "WikidataBridgeFailed" or "MultipleQidMatches" or "QidNoMatch" or "MissingQid"
                => MediaEditorIdentityIntent.FixWikidataMatch,
            "AmbiguousMediaType" or "RootWatchFolder" => MediaEditorIdentityIntent.ReviewClassification,
            "WritebackFailed" => MediaEditorIdentityIntent.ResolveWriteback,
            "PlaceholderTitle" or "StagedUnidentifiable" or "MetadataConflict" or "LowConfidence"
                => MediaEditorIdentityIntent.FixRetailMatch,
            _ => MediaEditorIdentityIntent.FixRetailMatch,
        };

    private static string ResolveInitialTab(MediaEditorIdentityIntent intent) =>
        intent switch
        {
            MediaEditorIdentityIntent.FixRetailMatch or
            MediaEditorIdentityIntent.ConfirmRetailMatch or
            MediaEditorIdentityIntent.FixWikidataMatch or
            MediaEditorIdentityIntent.ConfirmWikidataMatch or
            MediaEditorIdentityIntent.MarkWikidataMissing => "links",
            MediaEditorIdentityIntent.ResolveWriteback => "file",
            _ => "details",
        };

    private static string ResolvePrimaryActionLabel(MediaEditorIdentityIntent intent) =>
        intent switch
        {
            MediaEditorIdentityIntent.FixRetailMatch => "Find match",
            MediaEditorIdentityIntent.ConfirmRetailMatch => "Confirm Retail Match",
            MediaEditorIdentityIntent.FixWikidataMatch => "Search Wikidata",
            MediaEditorIdentityIntent.ConfirmWikidataMatch => "Choose Wikidata Match",
            MediaEditorIdentityIntent.MarkWikidataMissing => "Mark Provider-Only",
            MediaEditorIdentityIntent.ReviewClassification => "Review classification",
            MediaEditorIdentityIntent.ResolveWriteback => "Retry Writeback",
            _ => "Review Metadata",
        };

    private static string BuildSummary(MediaEditorIdentityIntent intent, string subject) =>
        intent switch
        {
            MediaEditorIdentityIntent.FixRetailMatch => $"Find the correct retail match for this {subject}.",
            MediaEditorIdentityIntent.ConfirmRetailMatch => $"Confirm the retail match for this {subject}.",
            MediaEditorIdentityIntent.FixWikidataMatch => $"Fix the Wikidata identity for this {subject}.",
            MediaEditorIdentityIntent.MarkWikidataMissing => $"Keep the retail match and mark this {subject} as provider-only.",
            MediaEditorIdentityIntent.ReviewClassification => "Review the classification and metadata; this editor does not change the media type.",
            MediaEditorIdentityIntent.ResolveWriteback => "Review the file write-back failure and retry or skip it.",
            _ => $"Review the {subject} metadata.",
        };
}
