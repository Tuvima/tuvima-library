using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Universe;

/// <summary>
/// Where else one thing from a Universe shows up in the Library: the same Wikidata item (never the same name) in other
/// Works the profile may see, grouped by format. The thing is a character, place or organisation (matched by its
/// Wikidata QID) or a person (matched by their library credits). Only Works the requesting profile may see are listed
/// or counted.
/// </summary>
public sealed class EntityAppearancesDto
{
    [JsonPropertyName("universe_qid")]
    public string UniverseQid { get; init; } = string.Empty;

    /// <summary><c>character</c>, <c>location</c>, <c>organization</c>, <c>person</c> or <c>entity</c> (any other kind of fictional entity).</summary>
    [JsonPropertyName("subject_kind")]
    public string SubjectKind { get; init; } = EntityAppearanceSubjectKinds.Entity;

    /// <summary>The name to show: the entity's label or the person's name.</summary>
    [JsonPropertyName("subject_label")]
    public string SubjectLabel { get; init; } = string.Empty;

    /// <summary>The entity's Wikidata QID; null for a person.</summary>
    [JsonPropertyName("subject_qid")]
    public string? SubjectQid { get; init; }

    /// <summary>The person's id; null for an entity.</summary>
    [JsonPropertyName("person_id")]
    public Guid? PersonId { get; init; }

    /// <summary>Format groups in the order TV, Movies, Books, Audiobooks, Comics, Music; empty groups are left out.</summary>
    [JsonPropertyName("groups")]
    public IReadOnlyList<EntityAppearanceGroupDto> Groups { get; init; } = [];

    /// <summary>Works listed across all groups; a TV show counts once however many episodes are owned.</summary>
    [JsonPropertyName("total_works")]
    public int TotalWorks { get; init; }

    /// <summary>
    /// Entities only: Works in this Universe whose character details were looked up but came back with none, so they may
    /// be missing from the list. Always 0 for a person.
    /// </summary>
    [JsonPropertyName("works_without_character_data")]
    public int WorksWithoutCharacterData { get; init; }

    /// <summary>Entities only: Works in this Universe whose character details have not been looked up yet. Always 0 for a person.</summary>
    [JsonPropertyName("works_not_yet_checked")]
    public int WorksNotYetChecked { get; init; }
}

/// <summary>The values of <see cref="EntityAppearancesDto.SubjectKind"/>.</summary>
public static class EntityAppearanceSubjectKinds
{
    public const string Character = "character";
    public const string Location = "location";
    public const string Organization = "organization";
    public const string Person = "person";
    public const string Entity = "entity";
}

/// <summary>One format's Works in an <see cref="EntityAppearancesDto"/>.</summary>
public sealed class EntityAppearanceGroupDto
{
    /// <summary>The display kind: <c>TV</c>, <c>Movie</c>, <c>Book</c>, <c>Audiobook</c>, <c>Comic</c> or <c>Music</c>.</summary>
    [JsonPropertyName("media_kind")]
    public string MediaKind { get; init; } = string.Empty;

    /// <summary>The user-facing plural heading: TV, Movies, Books, Audiobooks, Comics or Music.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>Works in this format, by year then title.</summary>
    [JsonPropertyName("works")]
    public IReadOnlyList<EntityAppearanceWorkDto> Works { get; init; } = [];
}

/// <summary>One Work (or TV show) in an <see cref="EntityAppearanceGroupDto"/>.</summary>
public sealed class EntityAppearanceWorkDto
{
    /// <summary>The Work's id; for a TV show, the show's id.</summary>
    [JsonPropertyName("work_id")]
    public Guid WorkId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    /// <summary>The Dashboard path of the Work's (or show's) item page, from the same builders display cards use.</summary>
    [JsonPropertyName("route")]
    public string Route { get; init; } = string.Empty;

    /// <summary>The character's role in this Work, or the person's credit role; null when none is recorded.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }
}
