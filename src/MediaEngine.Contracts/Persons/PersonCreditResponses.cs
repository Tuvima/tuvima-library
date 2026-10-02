using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Persons;

public sealed class CharacterPortrayalDto
{
    [JsonPropertyName("fictional_entity_id")]
    public Guid FictionalEntityId { get; init; }

    [JsonPropertyName("character_name")]
    public string? CharacterName { get; init; }

    [JsonPropertyName("character_qid")]
    public string? CharacterQid { get; init; }

    [JsonPropertyName("portrait_url")]
    public string? PortraitUrl { get; init; }
}

public sealed class CastCreditDto
{
    [JsonPropertyName("person_id")]
    public Guid? PersonId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("wikidata_qid")]
    public string? WikidataQid { get; init; }

    [JsonPropertyName("headshot_url")]
    public string? HeadshotUrl { get; init; }

    [JsonPropertyName("characters")]
    public List<CharacterPortrayalDto> Characters { get; init; } = [];

    [JsonPropertyName("actor_person_id")]
    public Guid? ActorPersonId => PersonId;

    [JsonPropertyName("actor_name")]
    public string? ActorName => Name;

    [JsonPropertyName("actor_headshot_url")]
    public string? ActorHeadshotUrl => HeadshotUrl;

    [JsonPropertyName("character_name")]
    public string? CharacterName => Characters.FirstOrDefault()?.CharacterName;

    [JsonPropertyName("character_qid")]
    public string? CharacterQid => Characters.FirstOrDefault()?.CharacterQid;

    [JsonPropertyName("character_image_url")]
    public string? CharacterImageUrl => Characters.FirstOrDefault()?.PortraitUrl;
}

public sealed class PersonLibraryCreditDto
{
    // Server-side provenance used to authorize normalized album credits against
    // the exact credited tracks. This is intentionally omitted from the API.
    [JsonIgnore]
    public IReadOnlyList<Guid> SourceWorkIds { get; init; } = [];

    [JsonPropertyName("work_id")]
    public Guid WorkId { get; init; }

    [JsonPropertyName("collection_id")]
    public Guid? CollectionId { get; init; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("association_type")]
    public string AssociationType { get; init; } = "Direct";

    [JsonPropertyName("via_group_id")]
    public Guid? ViaGroupId { get; init; }

    [JsonPropertyName("via_group_name")]
    public string? ViaGroupName { get; init; }

    [JsonPropertyName("association_is_inferred")]
    public bool AssociationIsInferred { get; init; }

    [JsonPropertyName("characters")]
    public List<CharacterPortrayalDto> Characters { get; init; } = [];
}

public sealed class PersonGroupMemberDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("date_range")]
    public string? DateRange { get; init; }

    [JsonPropertyName("headshot_url")]
    public string? HeadshotUrl { get; init; }
}

public sealed class PersonCharacterRoleDto
{
    [JsonPropertyName("fictional_entity_id")]
    public Guid FictionalEntityId { get; init; }

    [JsonPropertyName("character_name")]
    public string? CharacterName { get; init; }

    [JsonPropertyName("portrait_url")]
    public string? PortraitUrl { get; init; }

    [JsonPropertyName("work_id")]
    public Guid? WorkId { get; init; }

    [JsonPropertyName("work_qid")]
    public string? WorkQid { get; init; }

    [JsonPropertyName("work_title")]
    public string? WorkTitle { get; init; }

    [JsonPropertyName("collection_id")]
    public Guid? CollectionId { get; init; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; init; }

    [JsonPropertyName("universe_qid")]
    public string? UniverseQid { get; init; }

    [JsonPropertyName("universe_label")]
    public string? UniverseLabel { get; init; }
}
