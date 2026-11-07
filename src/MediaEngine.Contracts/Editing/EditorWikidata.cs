using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// How a Wikidata item stands to the item being edited (universal editor v5, 3c "Wikidata"). The Engine decides it from the editor's kind of
/// item and the item's class on Wikidata; the class lists live in <c>config/wikidata_match.json</c>.
/// </summary>
public static class EditorWikidataRelations
{
    /// <summary>The item itself: an episode for an episode, a film for a film. It can be applied.</summary>
    public const string Direct = "direct";

    /// <summary>The thing the item belongs to: the show for an episode, the album for a track. Shown for context; it can never be applied.</summary>
    public const string Parent = "parent";

    /// <summary>Anything else (a novel found while looking for an episode). It can be applied; the alignment evidence tells the person what to think.</summary>
    public const string Other = "other";
}

/// <summary>How one fact of the alignment evidence stands: both sides agree, they differ, or one side does not say.</summary>
public static class EditorWikidataEvidenceStates
{
    public const string Agrees = "agrees";
    public const string Differs = "differs";
    public const string NotChecked = "not_checked";
}

/// <summary>The facts the alignment evidence compares, as <see cref="EditorWikidataEvidenceDto.Fact"/>.</summary>
public static class EditorWikidataEvidenceFacts
{
    /// <summary>An identifier both sides can hold (IMDb, TheTVDB, TMDB, ISBN, MusicBrainz ...); <see cref="EditorWikidataEvidenceDto.Label"/> names it.</summary>
    public const string Identifier = "identifier";
    public const string Title = "title";
    public const string Year = "year";
    public const string Creator = "creator";
}

/// <summary>The reasons the Wikidata reads give, as codes. The Engine never sends a sentence; the Dashboard words every code.</summary>
public static class EditorWikidataMessageCodes
{
    /// <summary>Nothing was typed to search for and nothing was pasted.</summary>
    public const string EnterWords = "enter_words";

    /// <summary>What was pasted is not a Wikidata link or item number.</summary>
    public const string NotAnItem = "not_an_item";

    /// <summary>The pasted item is not on Wikidata.</summary>
    public const string ItemNotFound = "item_not_found";

    /// <summary>Wikidata did not answer.</summary>
    public const string ProviderSilent = "provider_silent";

    /// <summary>Wikidata answered with nothing.</summary>
    public const string NothingFound = "nothing_found";

    /// <summary>The item is the parent (the show, the album, the series) of the one being edited, so it cannot be its identity.</summary>
    public const string ParentNotAllowed = "parent_not_allowed";

    /// <summary>The item cannot be read for editing.</summary>
    public const string CouldNotRead = "could_not_read";

    /// <summary>The item's library never looks anything up, so Wikidata is not asked.</summary>
    public const string LookupsOff = "lookups_off";
}

/// <summary>One thing the Match tab's Wikidata search is asked: words to search for, or a pasted link or item number.</summary>
public sealed class EditorWikidataSearchRequestDto
{
    /// <summary>The words typed in "Search Wikidata"; empty when the person pasted instead.</summary>
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    /// <summary>What was pasted in "Paste a Wikidata link or QID": <c>Q123</c>, <c>https://www.wikidata.org/wiki/Q123</c>, a mobile or an entity address.</summary>
    [JsonPropertyName("paste")]
    public string? Paste { get; set; }
}

/// <summary>One fact of the alignment evidence: what the item says, what Wikidata says, and whether they agree.</summary>
public sealed class EditorWikidataEvidenceDto
{
    /// <summary>One of <see cref="EditorWikidataEvidenceFacts"/>.</summary>
    [JsonPropertyName("fact")]
    public string Fact { get; init; } = string.Empty;

    /// <summary>For an identifier, its name ("TheTVDB", "IMDb"); empty for the other facts.</summary>
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    /// <summary>One of <see cref="EditorWikidataEvidenceStates"/>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = EditorWikidataEvidenceStates.NotChecked;

    /// <summary>What the item says (the identifier, the title, the year); null when it says none.</summary>
    [JsonPropertyName("yours")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Yours { get; init; }

    /// <summary>What Wikidata says; null when it says none.</summary>
    [JsonPropertyName("wikidata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Wikidata { get; init; }
}

/// <summary>One Wikidata item as a text row of the results (no picture), with the evidence that shows how well it fits.</summary>
public sealed class EditorWikidataResultDto
{
    [JsonPropertyName("qid")]
    public string Qid { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>The kind of thing it is in plain words ("television episode", "film", "written work", "musical album", "human").</summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>One fact that tells similar items apart: the year, else the creator, else the series.</summary>
    [JsonPropertyName("fact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fact { get; init; }

    /// <summary>The release year, when Wikidata gives one.</summary>
    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Year { get; init; }

    /// <summary>One of <see cref="EditorWikidataRelations"/>.</summary>
    [JsonPropertyName("relation")]
    public string Relation { get; init; } = EditorWikidataRelations.Other;

    /// <summary>False for a parent: it is shown as context and Apply is refused. <see cref="Reason"/> says why.</summary>
    [JsonPropertyName("can_apply")]
    public bool CanApply { get; init; } = true;

    /// <summary>One of <see cref="EditorWikidataMessageCodes"/> when <see cref="CanApply"/> is false.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>The address of the item on Wikidata.</summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    /// <summary>The alignment evidence: identifiers both sides know, title, year and creator.</summary>
    [JsonPropertyName("evidence")]
    public IReadOnlyList<EditorWikidataEvidenceDto> Evidence { get; init; } = [];
}

/// <summary>The answer to a Wikidata search or a pasted item.</summary>
public sealed class EditorWikidataSearchResultDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    /// <summary>The results, best first, at most the number <c>config/wikidata_match.json</c> allows.</summary>
    [JsonPropertyName("results")]
    public IReadOnlyList<EditorWikidataResultDto> Results { get; init; } = [];

    /// <summary>True when a pasted link or item number was resolved directly to its item.</summary>
    [JsonPropertyName("from_paste")]
    public bool FromPaste { get; init; }

    /// <summary>One of <see cref="EditorWikidataMessageCodes"/> when there is something to say (nothing found, Wikidata silent); null otherwise.</summary>
    [JsonPropertyName("message_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageCode { get; init; }
}

/// <summary>What an item is linked to on Wikidata now (the card at the top of the Wikidata side). A read by the stored QID; it searches nothing.</summary>
public sealed class EditorWikidataIdentityDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    /// <summary>The item's own Wikidata item; null when it has none.</summary>
    [JsonPropertyName("qid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Qid { get; init; }

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Year { get; init; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    /// <summary>True when Wikidata did not answer, so only the stored QID is known.</summary>
    [JsonPropertyName("offline")]
    public bool Offline { get; init; }
}

/// <summary>The two sides of the Match tab as the person sees them now (staged changes included), sent to be compared.</summary>
public sealed class EditorWikidataConflictRequestDto
{
    /// <summary>The Wikidata item now linked (or staged to be linked).</summary>
    [JsonPropertyName("qid")]
    public string? Qid { get; set; }

    /// <summary>The provider's key in the Engine ("tmdb", "tvdb", "musicbrainz"); the identifier check compares by it, never by the display name.</summary>
    [JsonPropertyName("provider_id")]
    public string? ProviderId { get; set; }

    /// <summary>The provider as shown to the person ("TMDB", "TheTVDB"); used for wording only.</summary>
    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; set; }

    /// <summary>The provider's own id for the matched record.</summary>
    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; set; }

    [JsonPropertyName("provider_title")]
    public string? ProviderTitle { get; set; }

    [JsonPropertyName("provider_year")]
    public string? ProviderYear { get; set; }
}

/// <summary>What disagrees between the provider match and the Wikidata item, as <see cref="EditorWikidataConflictDto.Reason"/>.</summary>
public static class EditorWikidataConflictReasons
{
    /// <summary>The Wikidata item names a different record of the provider than the one matched.</summary>
    public const string Identifier = "identifier";

    /// <summary>The years differ by more than <c>config/wikidata_match.json</c> allows.</summary>
    public const string Year = "year";

    /// <summary>The titles share nothing.</summary>
    public const string Title = "title";
}

/// <summary>
/// Whether the provider match and the Wikidata identity disagree. The tab shows one amber line when they do and never changes either side
/// on its own. The words are the Dashboard's; this carries the facts they are made of.
/// </summary>
public sealed class EditorWikidataConflictDto
{
    [JsonPropertyName("conflict")]
    public bool Conflict { get; init; }

    /// <summary>One of <see cref="EditorWikidataConflictReasons"/> when <see cref="Conflict"/> is true.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("wikidata_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WikidataLabel { get; init; }

    [JsonPropertyName("wikidata_year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WikidataYear { get; init; }

    /// <summary>What the Wikidata item is in plain words ("film", "television series").</summary>
    [JsonPropertyName("wikidata_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WikidataType { get; init; }

    [JsonPropertyName("provider_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderName { get; init; }

    [JsonPropertyName("provider_year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderYear { get; init; }

    [JsonPropertyName("provider_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderTitle { get; init; }
}
