using System.Text.Json.Serialization;
using MediaEngine.Contracts.Library;

namespace MediaEngine.Contracts.Review;

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
    /// <c>keep_provider_match</c> (the provider's match stands, Wikidata found several entities), <c>move_to_tv</c> (the film is really this TV show),
    /// <c>confirm_match</c> (the Engine's match is linked but it was not sure enough to accept it by itself) or <c>use_retail_match</c> (the retail
    /// stage was unsure but found only this one candidate; "Use this" applies it through <c>POST /review/{id}/choose</c> with
    /// <see cref="ChoiceId"/>).
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
    public string? ImageUrl { get; set; }

    /// <summary>For <c>use_retail_match</c> only: the choice id to send to <c>POST /review/{id}/choose</c>; null for every other action.</summary>
    [JsonPropertyName("choice_id")]
    public string? ChoiceId { get; init; }
}

/// <summary>What a Needs Review choice would do when picked (<see cref="NeedsReviewChoiceDto.Kind"/>).</summary>
public static class NeedsReviewChoiceKinds
{
    /// <summary>One of the retail matches Library found; picking it matches the item to it.</summary>
    public const string Retail = "retail";

    /// <summary>One of the Series a book is listed in; picking it places the book on that Series.</summary>
    public const string Series = "series";

    /// <summary>One of the library types a file could be; picking it files the item under that type.</summary>
    public const string MediaType = "media_type";
}

/// <summary>
/// One of up to three answers a row offers when Library found a few possible ones and will not guess (Needs Review redesign D11). Picked
/// with <c>POST /review/{id}/choose</c> and the <see cref="Id"/>.
/// </summary>
public sealed class NeedsReviewChoiceDto
{
    /// <summary>Opaque; stable for as long as the row's candidates do not change. Send it back unchanged.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>One of <see cref="NeedsReviewChoiceKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>For a film "Dune (2021)"; a Series name; or a library type such as "Movies".</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The director, author or artist when the match has one; null otherwise.</summary>
    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    /// <summary>A picture the browser can load (never a provider's own link); null when there is none.</summary>
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }
}

/// <summary>One row of the Needs Review list: exactly one of <see cref="Review"/> and <see cref="Unmatched"/> is set.</summary>
public sealed class NeedsReviewRowDto
{
    /// <summary><c>review:{id}</c> or <c>file:{assetId}</c>; stable, so a ticked row stays ticked while the list reloads.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("library_id")]
    public string? LibraryId { get; init; }

    [JsonPropertyName("library_name")]
    public string? LibraryName { get; init; }

    /// <summary>When the Engine last tried to identify it (for a review item, when it was raised). The list is newest first by this.</summary>
    [JsonPropertyName("last_tried_at")]
    public DateTimeOffset LastTriedAt { get; init; }

    [JsonPropertyName("best_guess")]
    public NeedsReviewBestGuessDto? BestGuess { get; init; }

    /// <summary>Two or three answers to pick from in the row (group <c>choose</c>); empty when the row has none.</summary>
    [JsonPropertyName("choices")]
    public List<NeedsReviewChoiceDto> Choices { get; init; } = [];

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

/// <summary>
/// One page of the Needs Review list (review items and the files the Engine could not match, read together), with the totals for its groups
/// and for the kept items.
/// </summary>
public sealed class NeedsReviewListDto
{
    [JsonPropertyName("items")]
    public List<NeedsReviewRowDto> Items { get; init; } = [];

    /// <summary>Rows that match the library filter.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>How many rows each <see cref="NeedsReviewGroups"/> group holds in the list, before paging. All four keys are present.</summary>
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
