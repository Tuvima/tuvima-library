using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

/// <summary>
/// One user (a sign-in) as the server administrator's Users and Sign-in list shows it: the email, the profile it opens and that
/// profile's household. Never carries a password, a code or a token.
/// </summary>
public sealed record UserSummaryResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("profile_id")] Guid? ProfileId,
    [property: JsonPropertyName("profile_name")] string? ProfileName,
    [property: JsonPropertyName("household_id")] Guid? HouseholdId,
    [property: JsonPropertyName("household_name")] string? HouseholdName,
    [property: JsonPropertyName("has_password")] bool HasPassword,
    [property: JsonPropertyName("has_two_step")] bool HasTwoStep,
    [property: JsonPropertyName("is_enabled")] bool IsEnabled,
    [property: JsonPropertyName("this_computer_only")] bool ThisComputerOnly);

/// <summary>
/// A new profile made together with its user. Give either <c>household_id</c> (an existing household) or <c>new_household_name</c>
/// (the profile starts a new household and looks after it). <c>is_kids</c> (off by default) makes a Kids profile.
/// </summary>
public sealed record NewUserProfileRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("household_id")] Guid? HouseholdId = null,
    [property: JsonPropertyName("new_household_name")] string? NewHouseholdName = null,
    [property: JsonPropertyName("is_kids")] bool IsKids = false);

/// <summary>
/// Makes a new user (server administrator only): attach a sign-in to an existing profile that has none (<c>profile_id</c>) or make it
/// with a new profile (<c>new_profile</c>). <c>method</c> is <c>invitation</c> (a link and QR code for the person to choose their own
/// password) or <c>temporary_password</c> (send <c>temporary_password</c> too). Roles and access are set on the profile, not here.
/// </summary>
public sealed record CreateUserRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("profile_id")] Guid? ProfileId = null,
    [property: JsonPropertyName("new_profile")] NewUserProfileRequest? NewProfile = null,
    [property: JsonPropertyName("temporary_password")] string? TemporaryPassword = null);

/// <summary>The user just made, with its invitation when that was the chosen sign-in method.</summary>
public sealed record CreateUserResponse(
    [property: JsonPropertyName("user")] UserSummaryResponse User,
    [property: JsonPropertyName("invitation")] AccountInvitationResponse? Invitation,
    [property: JsonPropertyName("temporary_password_expires_at")] DateTimeOffset? TemporaryPasswordExpiresAt);

/// <summary>
/// What deleting a user does, for the confirmation (<c>GET /access/users/{id}/deletion-preview</c>; changes nothing). Normally only the
/// sign-in goes and the profile stays. When <c>removes_household</c> is set, the user is the last that can look after a household, so
/// the household goes with every profile in <c>household_profiles</c> (their ids are what <c>DELETE /access/users/{id}?profiles=</c>
/// confirms). <c>refused_reason</c> is one plain sentence when the delete would be refused, otherwise null.
/// <c>has_personal_photos</c> says a household removal meets photos or videos in its profiles' own libraries and <c>has_shared_photos</c>
/// that the household's Shared Library holds some. They are deleted with the household and never moved, so the confirmation says so
/// and tells the two apart; there is no choice to make (D18).
/// </summary>
public sealed record UserDeletionPreviewResponse(
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("profile_name")] string? ProfileName,
    [property: JsonPropertyName("removes_household")] bool RemovesHousehold,
    [property: JsonPropertyName("household_name")] string? HouseholdName,
    [property: JsonPropertyName("household_profiles")] IReadOnlyList<AccountDeletionProfileDto> HouseholdProfiles,
    [property: JsonPropertyName("refused_reason")] string? RefusedReason,
    [property: JsonPropertyName("has_personal_photos")] bool HasPersonalPhotos = false,
    [property: JsonPropertyName("has_shared_photos")] bool HasSharedPhotos = false);

/// <summary>The user a profile opens, as the profile list shows it.</summary>
public sealed record ManagedProfileUserDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string? Email);

/// <summary>
/// Replaces what a profile may use: <c>feature_ids</c> (<c>read</c>, <c>watch</c>, <c>listen</c>, <c>view</c>) and
/// <c>hidden_library_ids</c>, the libraries hidden from the profile (every other library in its areas is visible). A household
/// administrator can hide any library for their household's profiles but cannot show one that is hidden from their own profile, and
/// hands out only the features their own profile holds. <c>expected_access_version</c> is the profile's <c>access_version</c> the
/// caller loaded; the Dashboard always sends it, and when it is no longer current the Engine refuses with 409 and changes nothing
/// (left out, an application caller keeps the plain replace).
/// </summary>
public sealed record ReplaceProfileAccessRequest(
    [property: JsonPropertyName("feature_ids")] IReadOnlyList<string> FeatureIds,
    [property: JsonPropertyName("hidden_library_ids")] IReadOnlyList<Guid> HiddenLibraryIds,
    [property: JsonPropertyName("expected_access_version")] long? ExpectedAccessVersion = null);

/// <summary>
/// Sets a profile's roles. Both values are the wanted state. A household administrator may change <c>is_household_admin</c>
/// for profiles of their own household; only a server administrator changes <c>is_server_admin</c>.
/// </summary>
public sealed record SetProfileRolesRequest(
    [property: JsonPropertyName("is_household_admin")] bool IsHouseholdAdmin,
    [property: JsonPropertyName("is_server_admin")] bool IsServerAdmin);

/// <summary>
/// A user after a new starting password was set for it (<c>POST /access/accounts/{id}/temporary-password</c>): the user as the list shows
/// it, and when a temporary password stops working (null for an ordinary password).
/// </summary>
public sealed record UserTemporaryPasswordResponse(
    [property: JsonPropertyName("user")] UserSummaryResponse User,
    [property: JsonPropertyName("temporary_password_expires_at")] DateTimeOffset? TemporaryPasswordExpiresAt);
