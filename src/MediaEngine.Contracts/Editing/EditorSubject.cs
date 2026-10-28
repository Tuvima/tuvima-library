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

    /// <summary>A Series (or other provider-built grouping): title and description as local overrides, and a cover.</summary>
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
