using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Resources;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The one place that turns each reason an item is in Needs Review into one plain, everyday sentence (spec
/// needs-review-redesign D3). The wording lives in <c>SharedStrings.resx</c> (<c>NeedsReviewReason_*</c>), never here.
/// A <c>{0}</c> in a sentence is the singular lower-case media word (film, episode, book, ...).
/// </summary>
public static class NeedsReviewReasonText
{
    private const string UnknownKey = "NeedsReviewReason_Unknown";
    private const string UnknownFileKey = "NeedsReviewReason_File_Unknown";

    // Literal keys, so the string-table guardrails can see every one of them is used.
    private static readonly IReadOnlyDictionary<string, string> TriggerKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ReviewTrigger.LowConfidence] = "NeedsReviewReason_LowConfidence",
        [ReviewTrigger.RetailMatchFailed] = "NeedsReviewReason_RetailMatchFailed",
        [ReviewTrigger.RetailMatchAmbiguous] = "NeedsReviewReason_RetailMatchAmbiguous",
        [ReviewTrigger.MovieMatchedAsTv] = "NeedsReviewReason_MovieMatchedAsTv",
        [ReviewTrigger.FromMerge] = "NeedsReviewReason_FromMerge",
        [ReviewTrigger.NameCollision] = "NeedsReviewReason_NameCollision",
        [ReviewTrigger.PlaceholderTitle] = "NeedsReviewReason_PlaceholderTitle",
        [ReviewTrigger.StagedUnidentifiable] = "NeedsReviewReason_StagedUnidentifiable",
        [ReviewTrigger.MultipleQidMatches] = "NeedsReviewReason_MultipleQidMatches",
        [ReviewTrigger.MissingQid] = "NeedsReviewReason_MissingQid",
        [ReviewTrigger.WikidataBridgeFailed] = "NeedsReviewReason_WikidataBridgeFailed",
        [ReviewTrigger.MetadataConflict] = "NeedsReviewReason_MetadataConflict",
        [ReviewTrigger.AmbiguousMediaType] = "NeedsReviewReason_AmbiguousMediaType",
        [ReviewTrigger.RootWatchFolder] = "NeedsReviewReason_RootWatchFolder",
        [ReviewTrigger.WritebackFailed] = "NeedsReviewReason_WritebackFailed",
        [ReviewTrigger.MediaInspectionFailed] = "NeedsReviewReason_MediaInspectionFailed",
        [ReviewTrigger.AmbiguousSeriesMembership] = "NeedsReviewReason_AmbiguousSeriesMembership",
        [ReviewTrigger.BatchEditFailed] = "NeedsReviewReason_BatchEditFailed",
        [ReviewTrigger.LanguageMismatch] = "NeedsReviewReason_LanguageMismatch",
        [ReviewTrigger.UserFixMatch] = "NeedsReviewReason_UserFixMatch",
    };

    private static readonly IReadOnlyDictionary<string, string> FileKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [UnmatchedReasons.NoMatch] = "NeedsReviewReason_File_no_match",
        [UnmatchedReasons.NoYoutubeId] = "NeedsReviewReason_File_no_youtube_id",
    };

    /// <summary>Every sentence key this class can ask for, including the fallbacks and the retry lines (for the guardrail test).</summary>
    public static IReadOnlyList<string> AllKeys { get; } = TriggerKeys.Values
        .Concat(FileKeys.Values)
        .Concat([UnknownKey, UnknownFileKey, "NeedsReviewReason_RetryToday", "NeedsReviewReason_RetryTomorrow", "NeedsReviewReason_RetryDays", "NeedsReviewReason_RetryNever"])
        .ToArray();

    /// <summary>The string-table key for a review trigger's sentence (the fallback key when the trigger is not known).</summary>
    public static string KeyForTrigger(string? trigger) =>
        trigger is not null && TriggerKeys.TryGetValue(trigger, out var key) ? key : UnknownKey;

    /// <summary>The string-table key for an unmatched file reason's sentence (the fallback key when the reason is not known).</summary>
    public static string KeyForUnmatchedReason(string? reason) =>
        reason is not null && FileKeys.TryGetValue(reason, out var key) ? key : UnknownFileKey;

    /// <summary>One plain sentence for why an item is in Needs Review. A failed batch edit shows its own detail text when it has one.</summary>
    public static string ForTrigger(IStringLocalizer<SharedStrings> L, string? trigger, string? mediaType, string? detail)
    {
        if (trigger == ReviewTrigger.BatchEditFailed && !string.IsNullOrWhiteSpace(detail))
        {
            return detail;
        }

        return L[KeyForTrigger(trigger), MediaWord(L, mediaType)].Value;
    }

    /// <summary>One plain sentence for why a file is Unmatched.</summary>
    public static string ForUnmatchedReason(IStringLocalizer<SharedStrings> L, string? reason) =>
        L[KeyForUnmatchedReason(reason)].Value;

    /// <summary>When Library will look again: today, tomorrow, in N days, or never by itself (<paramref name="nextRetryAt"/> is null).</summary>
    public static string RetryText(IStringLocalizer<SharedStrings> L, DateTimeOffset? nextRetryAt, DateTimeOffset now)
    {
        if (nextRetryAt is not { } next)
        {
            return L["NeedsReviewReason_RetryNever"].Value;
        }

        var days = (next.ToOffset(now.Offset).Date - now.Date).Days;
        return days switch
        {
            <= 0 => L["NeedsReviewReason_RetryToday"].Value,
            1 => L["NeedsReviewReason_RetryTomorrow"].Value,
            _ => L["NeedsReviewReason_RetryDays", days].Value,
        };
    }

    /// <summary>The singular lower-case word for the item's kind, from the Dashboard's language file (the editor's kind words).</summary>
    private static string MediaWord(IStringLocalizer<SharedStrings> L, string? mediaType) =>
        (mediaType?.Trim().ToLowerInvariant()) switch
        {
            "movies" or "movie" => L["EditorWikidata_ItemFilm"].Value,
            "tv" or "tv show" or "tv shows" => L["EditorWikidata_ItemEpisode"].Value,
            "books" or "book" => L["EditorWikidata_ItemBook"].Value,
            "audiobooks" or "audiobook" => L["EditorMatchPanel_KindAudiobook"].Value,
            "comics" or "comic" => L["EditorMatchPanel_KindComic"].Value,
            "music" or "song" or "songs" => L["EditorWikidata_ItemSong"].Value,
            _ => L["EditorWikidata_ItemGeneric"].Value,
        };
}
