using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>A person, or a group of faces still waiting for a name ("Who's this?"), in the profile's own photos (spec view-people-search).</summary>
public sealed record ViewFaceGroupDto(
    [property: JsonPropertyName("person_id")] Guid PersonId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("hidden")] bool Hidden,
    [property: JsonPropertyName("face_count")] int FaceCount,
    [property: JsonPropertyName("photo_count")] int PhotoCount,
    [property: JsonPropertyName("cover_face_id")] Guid? CoverFaceId,
    [property: JsonPropertyName("sample_face_ids")] IReadOnlyList<Guid> SampleFaceIds);

public sealed record ViewFaceGroupsDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ViewFaceGroupDto> Items);

/// <summary>One face in a photo: its box as fractions of the picture (0 to 1) and who it was matched to.</summary>
public sealed record ViewFaceBoxDto(
    [property: JsonPropertyName("face_id")] Guid FaceId,
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("person_id")] Guid? PersonId,
    [property: JsonPropertyName("person_name")] string? PersonName,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height);

public sealed record ViewFaceBoxesDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ViewFaceBoxDto> Items);

public sealed record ViewNamePersonRequest(
    [property: JsonPropertyName("name")] string Name);

/// <summary>The person now holding the group's faces; <c>merged</c> is true when the name already belonged to someone and the group joined them.</summary>
public sealed record ViewNamePersonResultDto(
    [property: JsonPropertyName("person_id")] Guid PersonId,
    [property: JsonPropertyName("merged")] bool Merged);

public sealed record ViewMergePeopleRequest(
    [property: JsonPropertyName("into_person_id")] Guid IntoPersonId);

/// <summary>"Yes, that is them": the accepted faces join the person; the unticked ones are marked as not them.</summary>
public sealed record ViewConfirmFacesRequest(
    [property: JsonPropertyName("accepted_face_ids")] IReadOnlyList<Guid> AcceptedFaceIds,
    [property: JsonPropertyName("rejected_face_ids")] IReadOnlyList<Guid> RejectedFaceIds);

/// <summary>"Not Sam": these faces may never be put with that person again.</summary>
public sealed record ViewRejectFacesRequest(
    [property: JsonPropertyName("face_ids")] IReadOnlyList<Guid> FaceIds);

public sealed record ViewHidePersonRequest(
    [property: JsonPropertyName("hidden")] bool Hidden);

public sealed record ViewPersonCoverRequest(
    [property: JsonPropertyName("face_id")] Guid FaceId);

/// <summary>A group that may be the same person as the one just named ("Is this Sam?").</summary>
public sealed record ViewFaceSuggestionDto(
    [property: JsonPropertyName("group")] ViewFaceGroupDto Group,
    [property: JsonPropertyName("faces")] IReadOnlyList<ViewFaceBoxDto> Faces);

public sealed record ViewFaceSuggestionsDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ViewFaceSuggestionDto> Items);

/// <summary>How the face scan is doing for this profile. <c>state</c> is "off", "getting_tools", "looking" or "done".</summary>
public sealed record ViewFaceProgressDto(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("photos_looked")] int PhotosLooked,
    [property: JsonPropertyName("photos_total")] int PhotosTotal,
    [property: JsonPropertyName("faces")] int Faces,
    [property: JsonPropertyName("people")] int People,
    [property: JsonPropertyName("unnamed_groups")] int UnnamedGroups);

public static class ViewFaceStates
{
    public const string Off = "off";
    public const string GettingTools = "getting_tools";
    public const string Looking = "looking";
    public const string Done = "done";
}

/// <summary>Whether Library may look for faces in this profile's own photos (on by default).</summary>
public sealed record ViewRecognizePeopleDto(
    [property: JsonPropertyName("recognize")] bool Recognize);
