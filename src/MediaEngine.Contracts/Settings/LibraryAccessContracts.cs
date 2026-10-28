using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>
/// The profiles chosen for one library (<c>GET /settings/libraries/{id}/access</c>, administrators only; Library pages D9). <c>access</c> is the
/// library's access value (<c>standard</c> or <c>selected</c>; a Vault library is never returned) and <c>profile_ids</c> the profiles chosen
/// when it is <c>selected</c>. A library that is not Selected has none. <c>profiles</c> is every profile an administrator may choose, in the
/// order they were made, so the Access tab can draw the list to tick. Server administrators always see every library, so they are marked
/// (<c>is_server_admin</c>) and shown as always included. A Vault library is never returned.
/// </summary>
public sealed record LibraryAccessGrantsDto(
    [property: JsonPropertyName("library_id")] Guid LibraryId,
    [property: JsonPropertyName("access")] string Access,
    [property: JsonPropertyName("profile_ids")] IReadOnlyList<Guid> ProfileIds,
    [property: JsonPropertyName("profiles")] IReadOnlyList<LibraryAccessProfileDto> Profiles);

/// <summary>One profile in the Access tab's list: its name and picture, whether it is a Kids profile, and whether it is a server administrator.</summary>
public sealed record LibraryAccessProfileDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string AvatarColor,
    [property: JsonPropertyName("avatar_icon")] string? AvatarIcon,
    [property: JsonPropertyName("avatar_image_url")] string? AvatarImageUrl,
    [property: JsonPropertyName("is_kids")] bool IsKids,
    [property: JsonPropertyName("is_server_admin")] bool IsServerAdmin);

/// <summary>
/// Replaces the profiles chosen for a Selected-profiles library (<c>PUT /settings/libraries/{id}/access/profiles</c>, administrators only).
/// Refused when the library is not Selected, is a Vault library, or a profile does not exist.
/// </summary>
public sealed record SetLibraryProfileGrantsRequest(
    [property: JsonPropertyName("profile_ids")] IReadOnlyList<Guid> ProfileIds);
