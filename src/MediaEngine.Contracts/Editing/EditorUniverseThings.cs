using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// What the universal editor's rail reads for a Universe (universal editor v4, D10 and WP11): one entry per kind of thing the Universe can
/// hold, with how many it has, and the things of one kind, searched and paged. Unlike the Universe page, the editor lists every thing,
/// including ones the person hid and ones no Work reaches yet, because the editor is where they are put back. The Engine decides which
/// kinds exist, so a later kind needs no editor change.
/// </summary>
public sealed class EditorUniverseThingsDto
{
    /// <summary>The Universe's own editor id (the id its fields and pictures are staged under).</summary>
    [JsonPropertyName("universe_id")]
    public Guid UniverseId { get; init; }

    [JsonPropertyName("universe_qid")]
    public string UniverseQid { get; init; } = string.Empty;

    [JsonPropertyName("universe_name")]
    public string UniverseName { get; init; } = string.Empty;

    /// <summary>Every kind in the fixed order Characters, Factions, Places, Events, Objects, Species, Concepts.</summary>
    [JsonPropertyName("kinds")]
    public IReadOnlyList<EditorUniverseKindDto> Kinds { get; init; } = [];

    /// <summary>The kind the <see cref="Things"/> belong to; null when the read asked for the kinds only.</summary>
    [JsonPropertyName("kind_key")]
    public string? KindKey { get; init; }

    /// <summary>How many things of that kind match the search.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }

    [JsonPropertyName("things")]
    public IReadOnlyList<EditorUniverseThingDto> Things { get; init; } = [];
}

/// <summary>One kind of thing in a Universe, as one dropdown of the rail.</summary>
public sealed class EditorUniverseKindDto
{
    /// <summary><c>characters</c>, <c>factions</c>, <c>places</c>, <c>events</c>, <c>objects</c>, <c>species</c> or <c>concepts</c>.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>The plural name on screen ("Factions").</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>The singular name on screen, for "Add faction" ("Faction").</summary>
    [JsonPropertyName("singular")]
    public string Singular { get; init; } = string.Empty;

    /// <summary>How many things of this kind the Universe has, hidden ones included.</summary>
    [JsonPropertyName("count")]
    public int Count { get; init; }

    /// <summary>
    /// False for a kind the Universe does not use yet (Concepts, until a Fandom Lore source is approved or one is added by hand). The rail
    /// leaves such a kind out; it appears as soon as it has a thing.
    /// </summary>
    [JsonPropertyName("available")]
    public bool Available { get; init; } = true;
}

/// <summary>One thing in a rail dropdown: a small picture, a name, and whether it was added by hand or is hidden.</summary>
public sealed class EditorUniverseThingDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("kind_key")]
    public string KindKey { get; init; } = string.Empty;

    /// <summary>The main picture (or the logo, for a kind whose tile is a logo) when the thing has one on this machine.</summary>
    [JsonPropertyName("picture_url")]
    public string? PictureUrl { get; init; }

    [JsonPropertyName("added_by_user")]
    public bool AddedByUser { get; init; }

    /// <summary>True when the person hid the thing from the Universe page.</summary>
    [JsonPropertyName("hidden")]
    public bool Hidden { get; init; }
}

/// <summary>The fields of a staged "add a thing" change (<see cref="EditSessionChangeKinds.ThingAdd"/>), by the keys they travel under.</summary>
public static class EditSessionThingAdd
{
    /// <summary>The new thing's name.</summary>
    public const string NameKey = "name";

    /// <summary>The section key of its kind (<c>factions</c>, <c>places</c> ...).</summary>
    public const string KindKey = "kind";

    /// <summary>The short description, which is optional.</summary>
    public const string DescriptionKey = "description";
}
