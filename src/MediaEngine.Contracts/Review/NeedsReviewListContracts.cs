using System.Text.Json.Serialization;
using MediaEngine.Contracts.Library;

namespace MediaEngine.Contracts.Review;

/// <summary>
/// Needs Review is one list (universal editor v3, WP7 and D9): the items the Engine identified but is not sure about and the files it
/// could not match at all, read together. A library's "N files not identified" and the Settings notifier count come from the same two
/// sources, so the number a person is told and the rows they find always agree.
/// </summary>
public static class NeedsReviewKinds
{
    /// <summary>The Engine found nothing to match this file to (an Unmatched file, or a review item for a failed retail match).</summary>
    public const string NotMatched = "not_matched";

    /// <summary>The Engine found a match, or several, and is not sure of it (every other review item).</summary>
    public const string NotSure = "not_sure";
}

/// <summary>
/// The four groups of the redesigned Needs Review page, in the order the page shows them: what the person must do with the item.
/// Every row carries exactly one (<see cref="NeedsReviewRowDto.Group"/>).
/// </summary>
public static class NeedsReviewGroups
{
    /// <summary>The item has exactly one suggestion to confirm.</summary>
    public const string Ready = "ready";

    /// <summary>Several close matches, or a question only a person can answer (two Series claim it, a film may be a TV show).</summary>
    public const string Choose = "choose";

    /// <summary>Nothing close enough was found, or the file says too little to look it up.</summary>
    public const string NoMatch = "no_match";

    /// <summary>The file could not be read, written to, named or changed.</summary>
    public const string FileProblem = "file_problem";
}

/// <summary>The Engine's best guess for a row, with the one action "Use this" runs.</summary>
public sealed class NeedsReviewBestGuessDto
{
    /// <summary>
    /// <c>keep_provider_match</c> (the provider's match stands, Wikidata found several entities), <c>move_to_tv</c> (the film is really this TV show)
    /// or <c>confirm_match</c> (the Engine's match is linked but it was not sure enough to accept it by itself).
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("year")]
    public string? Year { get; init; }

    /// <summary>Where the guess comes from, in plain words: "Provider match" or "TMDB TV series".</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; init; }
}

/// <summary>One row of the Needs Review list: exactly one of <see cref="Review"/> and <see cref="Unmatched"/> is set.</summary>
public sealed class NeedsReviewRowDto
{
    /// <summary><c>review:{id}</c> or <c>file:{assetId}</c>; stable, so a ticked row stays ticked while the list reloads.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary><see cref="NeedsReviewKinds.NotMatched"/> or <see cref="NeedsReviewKinds.NotSure"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = NeedsReviewKinds.NotSure;

    [JsonPropertyName("library_id")]
    public string? LibraryId { get; init; }

    [JsonPropertyName("library_name")]
    public string? LibraryName { get; init; }

    /// <summary>When the Engine last tried to identify it (for a review item, when it was raised). The list is newest first by this.</summary>
    [JsonPropertyName("last_tried_at")]
    public DateTimeOffset LastTriedAt { get; init; }

    [JsonPropertyName("best_guess")]
    public NeedsReviewBestGuessDto? BestGuess { get; init; }

    [JsonPropertyName("review")]
    public ReviewItemDto? Review { get; init; }

    [JsonPropertyName("unmatched")]
    public UnmatchedItemDto? Unmatched { get; init; }

    /// <summary>One of <see cref="NeedsReviewGroups"/>: what the person must do with this row.</summary>
    [JsonPropertyName("group")]
    public string Group { get; init; } = NeedsReviewGroups.NoMatch;

    /// <summary>
    /// The item's own picture: a review item's resolved cover, or the cover inside the file or beside it in its folder. Null when the Engine
    /// knows of none; a URL here always serves a picture.
    /// </summary>
    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    /// <summary>The top parent Work's id (<c>D</c> format) when the row's Work sits in a family (episode to season to show, track to album); rows with the same key are one family. Null otherwise.</summary>
    [JsonPropertyName("family_key")]
    public string? FamilyKey { get; init; }

    /// <summary>The family's display title (the show, album or Series); null when the row has no family.</summary>
    [JsonPropertyName("family_title")]
    public string? FamilyTitle { get; init; }

    /// <summary>The family's top Work, so the page can open it; null when the row has no family.</summary>
    [JsonPropertyName("family_work_id")]
    public Guid? FamilyWorkId { get; init; }

    /// <summary>The family's picture when the Engine knows of one; null otherwise.</summary>
    [JsonPropertyName("family_cover_url")]
    public string? FamilyCoverUrl { get; init; }

    /// <summary>For a file, when it is next looked up (null once kept as it is); always null for a review item.</summary>
    [JsonPropertyName("next_retry_at")]
    public DateTimeOffset? NextRetryAt { get; init; }

    /// <summary>True only on rows of the kept view (<c>kept=true</c>): items a person chose to Keep as is.</summary>
    [JsonPropertyName("kept")]
    public bool Kept { get; init; }
}

/// <summary>One page of the Needs Review list, with the totals the filter chips show.</summary>
public sealed class NeedsReviewListDto
{
    [JsonPropertyName("items")]
    public List<NeedsReviewRowDto> Items { get; init; } = [];

    /// <summary>Rows that match the filter and the library filter.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>Not matched rows in the chosen library (or everywhere), whatever the kind filter says.</summary>
    [JsonPropertyName("not_matched_total")]
    public int NotMatchedTotal { get; init; }

    /// <summary>Not sure rows in the chosen library (or everywhere), whatever the kind filter says.</summary>
    [JsonPropertyName("not_sure_total")]
    public int NotSureTotal { get; init; }

    /// <summary>How many rows each <see cref="NeedsReviewGroups"/> group holds in the filtered list, before paging. All four keys are present.</summary>
    [JsonPropertyName("group_totals")]
    public Dictionary<string, int> GroupTotals { get; init; } = new()
    {
        [NeedsReviewGroups.Ready] = 0,
        [NeedsReviewGroups.Choose] = 0,
        [NeedsReviewGroups.NoMatch] = 0,
        [NeedsReviewGroups.FileProblem] = 0,
    };

    /// <summary>How many kept items exist for the same library filter (the "hidden items" line), whichever view this page is.</summary>
    [JsonPropertyName("kept_total")]
    public int KeptTotal { get; init; }

    [JsonPropertyName("skip")]
    public int Skip { get; init; }

    [JsonPropertyName("take")]
    public int Take { get; init; }
}
