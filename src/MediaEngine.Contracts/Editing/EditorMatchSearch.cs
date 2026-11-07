using System.Text.Json.Serialization;
using MediaEngine.Contracts.Matching;

namespace MediaEngine.Contracts.Editing;

/// <summary>How well a search result (or the current match) fits the file, as advice. Apply match is available whatever the verdict.</summary>
public static class EditorMatchVerdicts
{
    /// <summary>Every compared fact agrees and enough of them were checked.</summary>
    public const string Strong = "strong";

    /// <summary>What says which record this is agrees, but something else differs or too little could be checked.</summary>
    public const string Possible = "possible";

    /// <summary>A fact that says which record this is (the title, the number, an identifier) differs.</summary>
    public const string Check = "check";
}

/// <summary>What a provider in the Match tab's dropdown is ready to do.</summary>
public static class EditorMatchProviderStates
{
    public const string Ready = "ready";
    public const string NeedsKey = "needs_key";
    public const string Off = "off";
}

/// <summary>The identifiers a file's name, tags or sidecar files carry, as <see cref="EditorMatchIdentifierDto.Kind"/>.</summary>
public static class EditorMatchIdentifierKinds
{
    public const string YouTube = "youtube";
    public const string Isbn = "isbn";
    public const string Isrc = "isrc";
    public const string Imdb = "imdb";
    public const string Tmdb = "tmdb";
    public const string MusicBrainz = "musicbrainz";
    public const string EmbeddedTitle = "embedded_title";
}

/// <summary>One source the Match tab can search for an item, with what it is ready to do.</summary>
public sealed class EditorMatchProviderDto
{
    /// <summary>The provider's name in the Engine ("tmdb", "openlibrary").</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The provider as shown ("TMDB", "Open Library").</summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>One of <see cref="EditorMatchProviderStates"/>. A provider that is not Ready is listed but cannot be searched.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = EditorMatchProviderStates.Ready;

    /// <summary>True for the provider the kind uses first; it is also the first entry of the list.</summary>
    [JsonPropertyName("is_default")]
    public bool IsDefault { get; init; }
}

/// <summary>One box of the Match search, in the order the kind lists them, with the value the Engine pre-filled.</summary>
public sealed class EditorMatchFieldDto
{
    /// <summary>The field: <c>title</c>, <c>year</c>, <c>author</c>, <c>isbn</c>, <c>series</c>, <c>issue</c>, <c>show</c>, <c>artist</c>, <c>album</c>, <c>song</c>, <c>provider_id</c> ...</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>The label shown above the box.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>The cleaned value from the file name, the embedded tags or the folders; empty when none of them said.</summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    /// <summary>Where the pre-filled value came from: <c>tags</c>, <c>file_name</c> or <c>folder</c>; null when the box is empty.</summary>
    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; init; }
}

/// <summary>An identifier found in the file's name, tags or sidecar files.</summary>
public sealed class EditorMatchIdentifierDto
{
    /// <summary>One of <see cref="EditorMatchIdentifierKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}

/// <summary>What the Engine knows about the file itself, shown beside the verdict (universal editor v5, 3c "Local evidence").</summary>
public sealed class EditorMatchLocalEvidenceDto
{
    /// <summary>The title read from the file name (cleaned); null when it says none.</summary>
    [JsonPropertyName("file_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileTitle { get; init; }

    /// <summary>The year read from the file name or tags.</summary>
    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Year { get; init; }

    /// <summary>The number the file name carries: an episode, an issue or a track.</summary>
    [JsonPropertyName("number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Number { get; init; }

    /// <summary>The creator the tags name: the author, the artist or the director.</summary>
    [JsonPropertyName("creator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Creator { get; init; }

    /// <summary>The length measured from the media file, in seconds; null when the file has not been measured.</summary>
    [JsonPropertyName("measured_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MeasuredSeconds { get; init; }

    /// <summary>The measured length as shown ("1:47:52"); null when not measured.</summary>
    [JsonPropertyName("measured_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MeasuredText { get; init; }

    [JsonPropertyName("identifiers")]
    public IReadOnlyList<EditorMatchIdentifierDto> Identifiers { get; init; } = [];
}

/// <summary>
/// What the Match tab needs to open its search for one item (universal editor v5, 3c): the providers for the item's kind with their
/// states, the boxes of the kind with their pre-filled values, the original file name, and the local evidence. A read; it makes no
/// search and no request to any provider.
/// </summary>
public sealed class EditorMatchSetupDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    /// <summary>The editor's kind (<c>movie</c>, <c>book</c>, <c>album</c> ...).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>The original file name, shown as muted text under the heading; null when the item has no single file.</summary>
    [JsonPropertyName("file_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; init; }

    /// <summary>True when no provider matches this kind of item: the tab says so and offers no search.</summary>
    [JsonPropertyName("no_provider")]
    public bool NoProvider { get; init; }

    /// <summary>The sentence shown when <see cref="NoProvider"/> is true.</summary>
    [JsonPropertyName("no_provider_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NoProviderMessage { get; init; }

    /// <summary>The providers for this kind, the default first. Empty when <see cref="NoProvider"/> is true.</summary>
    [JsonPropertyName("providers")]
    public IReadOnlyList<EditorMatchProviderDto> Providers { get; init; } = [];

    /// <summary>The boxes of the search, in order, with their pre-filled values. Empty when <see cref="NoProvider"/> is true.</summary>
    [JsonPropertyName("fields")]
    public IReadOnlyList<EditorMatchFieldDto> Fields { get; init; } = [];

    [JsonPropertyName("evidence")]
    public EditorMatchLocalEvidenceDto Evidence { get; init; } = new();
}

/// <summary>A Match search: the provider to ask and the boxes as the person left them.</summary>
public sealed class EditorMatchSearchRequestDto
{
    /// <summary>The provider to ask (its <c>name</c>); empty asks the kind's default provider.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>The boxes, by <see cref="EditorMatchFieldDto.Key"/>.</summary>
    [JsonPropertyName("fields")]
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One fact of a verdict: a tick, a cross, or "not checked".</summary>
public sealed class EditorMatchReasonDto
{
    /// <summary>The fact: <c>title</c>, <c>year</c>, <c>number</c>, <c>creator</c>, <c>runtime</c>, <c>track_length</c> or <c>identifier</c>.</summary>
    [JsonPropertyName("fact")]
    public string Fact { get; init; } = string.Empty;

    /// <summary>One of <see cref="EditorMatchEvidenceStates"/>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = EditorMatchEvidenceStates.NotChecked;

    /// <summary>The reason in plain words ("Title matches", "Length differs by 2 s").</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>A note under an approximate fact ("1:47:52 measured · 108 min (rounded)"); null otherwise.</summary>
    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

/// <summary>An edition or release of a work: the year, the publisher, the format and the language.</summary>
public sealed class EditorMatchEditionDto
{
    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Year { get; init; }

    [JsonPropertyName("publisher")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Publisher { get; init; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }
}

/// <summary>One search result: what the list row and the preview card show, the verdict with its reasons, and the candidate that Apply stages.</summary>
public sealed class EditorMatchResultDto
{
    /// <summary>The record exactly as the match is applied from; the Dashboard stages it unchanged.</summary>
    [JsonPropertyName("candidate")]
    public ItemCanonicalRetailCandidateDto Candidate { get; init; } = new();

    [JsonPropertyName("provider_name")]
    public string ProviderName { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Year { get; init; }

    [JsonPropertyName("creator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Creator { get; init; }

    /// <summary>The record's length as shown ("108 min", "4:14"); null when the provider gave none.</summary>
    [JsonPropertyName("length_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LengthText { get; init; }

    /// <summary>The picture for the row and the preview, through the Engine's own preview connection point; null when there is none.</summary>
    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>The record's page on the provider's own site ("Open source"); null when no address can be built.</summary>
    [JsonPropertyName("source_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; init; }

    /// <summary>What the record is, in plain words ("Film", "Book", "Album").</summary>
    [JsonPropertyName("kind_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KindText { get; init; }

    /// <summary>One of <see cref="EditorMatchVerdicts"/>.</summary>
    [JsonPropertyName("verdict")]
    public string Verdict { get; init; } = EditorMatchVerdicts.Possible;

    [JsonPropertyName("reasons")]
    public IReadOnlyList<EditorMatchReasonDto> Reasons { get; init; } = [];

    /// <summary>
    /// For books, comics and audiobooks: this work's editions or releases when the provider has more than one, the closest to the file
    /// first. Empty for a single record (a film, a song) or a work the provider has one edition of.
    /// </summary>
    [JsonPropertyName("editions")]
    public IReadOnlyList<EditorMatchResultDto> Editions { get; init; } = [];

    /// <summary>When this result is one edition of a work, the edition's own year, publisher, format and language.</summary>
    [JsonPropertyName("edition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorMatchEditionDto? Edition { get; init; }

    /// <summary>True for the edition the Engine suggests as closest to the file's facts.</summary>
    [JsonPropertyName("is_suggested")]
    public bool IsSuggested { get; init; }
}

/// <summary>A provider that did not answer a search, named so the person can try another.</summary>
public sealed class EditorMatchFailedProviderDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>The answer to a Match search.</summary>
public sealed class EditorMatchSearchResultDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    /// <summary>The provider that was asked.</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    /// <summary>The words that were searched, after cleaning.</summary>
    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;

    /// <summary>True when the first try found nothing and the results came from a second try with the punctuation taken out.</summary>
    [JsonPropertyName("retried_without_punctuation")]
    public bool RetriedWithoutPunctuation { get; init; }

    /// <summary>One entry per work, best first. For a book, comic or audiobook with several editions, <see cref="EditorMatchResultDto.Editions"/> lists them.</summary>
    [JsonPropertyName("results")]
    public IReadOnlyList<EditorMatchResultDto> Results { get; init; } = [];

    /// <summary>The providers that did not answer, by name. Empty when every provider asked answered.</summary>
    [JsonPropertyName("failed_providers")]
    public IReadOnlyList<EditorMatchFailedProviderDto> FailedProviders { get; init; } = [];

    /// <summary>The plain sentence shown when there are no results ("Nothing matches that." or which provider did not answer); null when there are results.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    /// <summary>The local evidence the verdicts were judged against.</summary>
    [JsonPropertyName("evidence")]
    public EditorMatchLocalEvidenceDto Evidence { get; init; } = new();
}

/// <summary>The result a person is about to apply, sent to ask where the match would put the item.</summary>
public sealed class EditorMatchPlacementRequestDto
{
    [JsonPropertyName("candidate")]
    public ItemCanonicalRetailCandidateDto Candidate { get; init; } = new();
}

/// <summary>
/// Where applying a match would put the item and its file (universal editor v5, D8). A read: nothing is moved. The Dashboard stages a
/// Relationship change when <see cref="DestinationId"/> is set, and a file move when <see cref="Managed"/> is true, so Review and the
/// footer say what Save changes will do.
/// </summary>
public sealed class EditorMatchPlacementDto
{
    /// <summary>The album, season or series the match names, when it is not where the item already is; null when the parent stays.</summary>
    [JsonPropertyName("destination_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DestinationId { get; init; }

    /// <summary>The place it will sit in, as shown ("Season 2 (Other Show)"); null when the parent stays.</summary>
    [JsonPropertyName("parent_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentLabel { get; init; }

    /// <summary>The place it sits in now, as shown; null when it has none.</summary>
    [JsonPropertyName("current_parent_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentParentLabel { get; init; }

    /// <summary>True when the item's file is in a Managed folder, so applying the match renames or moves it by itself. False for a Read-only or As-is folder.</summary>
    [JsonPropertyName("managed")]
    public bool Managed { get; init; }

    [JsonPropertyName("from_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromPath { get; init; }

    [JsonPropertyName("to_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToPath { get; init; }

    /// <summary>Why the item or its file will stay where it is (no such place in the Library yet, a Read-only folder); null when nothing is blocked. The match still applies.</summary>
    [JsonPropertyName("blocked_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlockedReason { get; init; }
}
