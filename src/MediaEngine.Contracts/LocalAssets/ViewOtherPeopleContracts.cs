using System.Text.Json.Serialization;
using MediaEngine.Contracts.Paging;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>"Other people": the households a server administrator can open, read-only. Never includes the caller's own household.</summary>
public sealed record ViewOtherPeopleDto(
    [property: JsonPropertyName("households")] IReadOnlyList<ViewOtherHouseholdDto> Households);

public sealed record ViewOtherHouseholdDto(
    [property: JsonPropertyName("household_id")] Guid HouseholdId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("people")] IReadOnlyList<ViewOtherPersonDto> People);

/// <summary>
/// One person in another household. <c>profile_id</c> is also how that household's Shared Library is requested
/// (<c>scope=othershared&amp;scopeProfileId=…</c>).
/// </summary>
public sealed record ViewOtherPersonDto(
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string? AvatarColor,
    [property: JsonPropertyName("has_personal_space")] bool HasPersonalSpace);

/// <summary>One time a server administrator opened someone's photos, shown to the household they belong to.</summary>
public sealed record ViewPhotoViewDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("viewed_at")] DateTimeOffset ViewedAt,
    [property: JsonPropertyName("viewer_name")] string ViewerName,
    [property: JsonPropertyName("opened")] string Opened,
    [property: JsonPropertyName("space")] string Space);

public sealed record ViewPhotoViewsPageDto(
    [property: JsonPropertyName("page")] PagedResponse<ViewPhotoViewDto> Page,
    [property: JsonPropertyName("household_wide")] bool HouseholdWide);
