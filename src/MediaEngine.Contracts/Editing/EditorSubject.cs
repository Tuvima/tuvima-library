using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// The kinds of thing besides a library title that the universal editor edits (spec D17, D17a). Each has its own small configuration:
/// which Details fields it shows, which pictures it can have, and whether it can be matched.
/// </summary>
public static class EditorSubjectKinds
{
    /// <summary>A person or group: display name, biography, sort name and library tags; the photo; no provider record to re-match yet.</summary>
    public const string Person = "person";

    /// <summary>A Series (or other provider-built grouping): its name, description and order are locked; its cover, background, logo, tags and sort title are the owner's own (v8 D5).</summary>
    public const string Series = "series";

    /// <summary>A curated collection someone built: its own name and description, and a cover.</summary>
    public const string Collection = "collection";

    /// <summary>A Universe: a local name and description override (the canonical ones are never changed), a cover and a background.</summary>
    public const string Universe = "universe";

    /// <summary>A character, place or organisation of a Universe (the Chronicle explorer): a name and description kept locally, and a portrait and background.</summary>
    public const string FictionalEntity = "fictional-entity";

    public static readonly IReadOnlyList<string> All = [Person, Series, Collection, Universe, FictionalEntity];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>How a Details field is typed into.</summary>
public static class EditorSubjectInputKinds
{
    public const string Text = "text";
    public const string TextArea = "textarea";

    /// <summary>A list of short labels, separated by semicolons (library tags).</summary>
    public const string Chips = "chips";

    /// <summary>One value picked from the field's <see cref="EditorSubjectFieldDto.Options"/> (a Universe thing's "belongs to").</summary>
    public const string Choice = "choice";
}

/// <summary>
/// What the editor needs to open on a person, Series, curated collection or Universe: who it is, which fields it shows with their
/// current and canonical values, and which pictures it can have. The Engine builds it from the same data the save writes, so the
/// panel and the save always agree on what a field means.
/// </summary>
public sealed class EditorSubjectDto
{
    /// <summary>The id the edit session names this subject by (a Universe uses the id of its own record).</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>One of <see cref="EditorSubjectKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = EditorSubjectKinds.Person;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The kind of picture owner the artwork tool asks about (<c>Person</c>, <c>Collection</c>).</summary>
    [JsonPropertyName("artwork_entity_kind")]
    public string ArtworkEntityKind { get; init; } = "Collection";

    /// <summary>The picture slots the subject can have, such as <c>CoverArt</c>, <c>Background</c> or <c>Headshot</c>. Empty means no Artwork tool.</summary>
    [JsonPropertyName("artwork_slots")]
    public List<string> ArtworkSlots { get; init; } = [];

    [JsonPropertyName("fields")]
    public List<EditorSubjectFieldDto> Fields { get; init; } = [];

    /// <summary>Facts from providers, shown read-only beside the fields (a person's occupation, birth date, nationality).</summary>
    [JsonPropertyName("facts")]
    public List<EditorSubjectFactDto> Facts { get; init; } = [];

    /// <summary>For a Series: its members in order, so the editor can list them (locked) and open any one of them. Empty for every other kind.</summary>
    [JsonPropertyName("members")]
    public List<EditorSubjectMemberDto> Members { get; init; } = [];

    /// <summary>
    /// For a Series (universal editor v8 D1): the header's Retail and Canonical badge, worked out from the Series' own Wikidata link and its
    /// members' matches. Null for every other kind.
    /// </summary>
    [JsonPropertyName("match_badge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorSubjectMatchBadgeDto? MatchBadge { get; init; }

    /// <summary>
    /// For a Series (v8 D6): the kinds of title it holds (Books, Movies, TV and so on), only those it has, in the library's usual order. They are
    /// the choices of the header's first dropdown; the second lists the titles of one kind (<see cref="EditorSeriesTitlesDto"/>). Empty for every other kind.
    /// </summary>
    [JsonPropertyName("media_types")]
    public List<EditorSeriesMediaTypeDto> MediaTypes { get; init; } = [];
}

/// <summary>The two halves of the editor header's match badge for a Series (v8 D1): a retail provider match through its members, and a Wikidata link of its own.</summary>
public sealed class EditorSubjectMatchBadgeDto
{
    /// <summary>True when the Series has owned members and every one of them is matched to a retail provider.</summary>
    [JsonPropertyName("retail_matched")]
    public bool RetailMatched { get; init; }

    /// <summary>The provider's name for the tooltip when the members are matched through one provider; null otherwise.</summary>
    [JsonPropertyName("provider_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderName { get; init; }

    /// <summary>True when the Series itself is linked to a Wikidata item.</summary>
    [JsonPropertyName("canonical_matched")]
    public bool CanonicalMatched { get; init; }
}

/// <summary>One kind of title a Series holds: a choice of the header's first dropdown (v8 D6).</summary>
public sealed class EditorSeriesMediaTypeDto
{
    /// <summary>The media type's name, such as <c>Books</c>, <c>Movies</c> or <c>TV</c>; the key the titles read is asked with.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>The words shown for it: "Books", "Audiobooks", "Movies", "TV", "Music", "Comics".</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>How many titles of this kind the Series' owner has.</summary>
    [JsonPropertyName("owned_count")]
    public int OwnedCount { get; init; }
}

/// <summary>One page of the titles of one kind in a Series: the header's second dropdown (v8 D6).</summary>
public sealed class EditorSeriesTitlesDto
{
    [JsonPropertyName("media_type")]
    public string MediaType { get; init; } = string.Empty;

    /// <summary>How many titles match the search, owned and (when the profile shows them) not owned.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    /// <summary>True when the profile's own "Show missing" tick for this Series is on, so titles not owned are in the list.</summary>
    [JsonPropertyName("showing_missing")]
    public bool ShowingMissing { get; init; }

    [JsonPropertyName("items")]
    public List<EditorSeriesTitleDto> Items { get; init; } = [];
}

/// <summary>One title in the Series' second dropdown. A title the person does not own has no <see cref="Id"/> and cannot be opened.</summary>
public sealed class EditorSeriesTitleDto
{
    /// <summary>The Work to open; null for a title that is not owned.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The title's place in the Series ("1", "2.5"); null when the Series gives it none.</summary>
    [JsonPropertyName("number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Number { get; init; }

    [JsonPropertyName("owned")]
    public bool Owned { get; init; } = true;
}

/// <summary>One member of a Series: the book, film or comic the editor opens when it is chosen.</summary>
public sealed class EditorSubjectMemberDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The member's place in the Series; null when the Series gives it none.</summary>
    [JsonPropertyName("number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Number { get; init; }

    /// <summary>The media type of the member (<c>Books</c>, <c>Movies</c> and so on), for the detail line of the order list; null when unknown.</summary>
    [JsonPropertyName("media_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaType { get; init; }

    /// <summary>The member's publication date or year as the Engine holds it, for the detail line; null when it has none.</summary>
    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Year { get; init; }

    /// <summary>
    /// The rule of the Series' order that placed this member (one of <see cref="EditorSeriesOrderRuleKeys"/>), worked out by the Engine where it sorts
    /// the members. Null when the Engine did not say, in which case the editor lists the member without naming a rule.
    /// </summary>
    [JsonPropertyName("placed_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlacedBy { get; init; }
}

/// <summary>
/// The rules a Series' order is worked out by, in the order they are tried (<see cref="EditorSubjectMemberDto.PlacedBy"/>): the number the
/// Series gives a member, then its publication date, then its title (A to Z).
/// </summary>
public static class EditorSeriesOrderRuleKeys
{
    public const string Number = "number";
    public const string Date = "date";
    public const string Title = "title";

    /// <summary>The rules in the order they are tried.</summary>
    public static readonly IReadOnlyList<string> InOrder = [Number, Date, Title];
}

/// <summary>One Details field of an editor subject.</summary>
public sealed class EditorSubjectFieldDto
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>One of <see cref="EditorSubjectInputKinds"/>.</summary>
    [JsonPropertyName("input_kind")]
    public string InputKind { get; init; } = EditorSubjectInputKinds.Text;

    /// <summary>What the library shows now (the local value when there is one, otherwise the canonical one).</summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    /// <summary>The canonical value the field goes back to when the local one is removed. Empty when the field has no canonical value to go back to.</summary>
    [JsonPropertyName("canonical_value")]
    public string CanonicalValue { get; init; } = string.Empty;

    /// <summary>True when a local value is stored over the canonical one, so a "Reset to canonical value" link makes sense.</summary>
    [JsonPropertyName("is_overridden")]
    public bool IsOverridden { get; init; }

    /// <summary>The name of the source the canonical value comes from, shown beside the reset link.</summary>
    [JsonPropertyName("source_label")]
    public string? SourceLabel { get; init; }

    /// <summary>For <see cref="EditorSubjectInputKinds.Choice"/>: the values the person can pick from, in the order shown.</summary>
    [JsonPropertyName("options")]
    public List<EditorSubjectOptionDto> Options { get; init; } = [];
}

/// <summary>One pickable value of a choice field.</summary>
public sealed class EditorSubjectOptionDto
{
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;
}

/// <summary>A read-only fact with its label.</summary>
public sealed class EditorSubjectFactDto
{
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("source_label")]
    public string? SourceLabel { get; init; }
}
