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

/// <summary>The Engine's best guess for a row, with the one action "Use this" runs.</summary>
public sealed class NeedsReviewBestGuessDto
{
    /// <summary><c>keep_provider_match</c> (the provider's match stands, Wikidata could not confirm it) or <c>move_to_tv</c> (the film is really this TV show).</summary>
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

    [JsonPropertyName("skip")]
    public int Skip { get; init; }

    [JsonPropertyName("take")]
    public int Take { get; init; }
}
