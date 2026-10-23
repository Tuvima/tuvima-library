using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Backups;

/// <summary>
/// A profile in a user file as the file states it. Limits are carried as exported and cannot be checked, so the administrator sees them
/// before importing and can change them afterwards on the profile. Roles are never carried: every imported profile arrives as an ordinary
/// member (Kids stays Kids), except the one chosen to look after a household the import creates.
/// </summary>
/// <param name="Name">The profile's name.</param>
/// <param name="Role">The stored role: <c>Administrator</c>, <c>StandardUser</c> or <c>RestrictedProfile</c>. Only <c>RestrictedProfile</c> (Kids) is kept; anything else arrives as <c>StandardUser</c>.</param>
/// <param name="ContentLimit">The highest rating the profile may see (<c>G</c>, <c>PG</c>, <c>PG-13</c> or <c>R</c>); null when there is none.</param>
/// <param name="ContentLimitAllowUnrated">True when items with no rating stay visible although a content limit is set.</param>
/// <param name="Id">The profile's id in the file; the import choices name it.</param>
/// <param name="SignInId">The id (in the file) of the sign-in that opens this profile; null when none does.</param>
/// <param name="Access">What the profile could use in the file (<c>read</c>, <c>watch</c>, <c>listen</c>, <c>view</c>); it arrives only when the administrator keeps it.</param>
public sealed record UserImportProfileDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content_limit")] string? ContentLimit,
    [property: JsonPropertyName("content_limit_allow_unrated")] bool ContentLimitAllowUnrated,
    [property: JsonPropertyName("id")] Guid Id = default,
    [property: JsonPropertyName("sign_in_id")] Guid? SignInId = null,
    [property: JsonPropertyName("access")] IReadOnlyList<string>? Access = null);

/// <summary>A sign-in (user) in a user file, with what the Engine already knows about it here.</summary>
/// <param name="Id">The sign-in's id in the file.</param>
/// <param name="Email">The email the file has for it.</param>
/// <param name="ProfileId">The file's profile it opens.</param>
/// <param name="EmailInUse">True when someone here already signs in with that email, so the administrator gives a different one (or imports the profile with no sign-in).</param>
/// <param name="Problem">One plain sentence when this sign-in cannot be imported whatever its email (it can still arrive as a profile with no sign-in); null otherwise.</param>
public sealed record UserImportSignInDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("email_in_use")] bool EmailInUse,
    [property: JsonPropertyName("problem")] string? Problem);

/// <summary>A household that already exists in this Library, as a place to put the imported profiles.</summary>
/// <param name="Id">The household.</param>
/// <param name="Name">Its name.</param>
/// <param name="Profiles">How many profiles it holds now (at most eight fit).</param>
public sealed record UserImportHouseholdDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("profiles")] int Profiles);

/// <summary>
/// What an administrator sees before importing a user file (backup and restore spec D19, users spec D9). Nothing has been written to the
/// data store. The file is kept for a short while under <see cref="OperationId"/> so the next step imports exactly what was shown.
/// </summary>
/// <param name="OperationId">Names the checked file for the import step.</param>
/// <param name="Scope"><c>profile</c> when the file holds one profile with its sign-in, <c>household</c> when it holds a whole household.</param>
/// <param name="HouseholdName">The household the profiles came from, as the file names it (a suggestion for a new household); null when it names none.</param>
/// <param name="Profiles">The profiles, with the role and content limit the file states.</param>
/// <param name="SignIns">The sign-ins that open them.</param>
/// <param name="Households">The households that exist here to choose from.</param>
/// <param name="CanAddHousehold">False while a server administrator has no email and password yet, so a second household cannot be made (<see cref="AddHouseholdBlockedReason"/> says why).</param>
/// <param name="AddHouseholdBlockedReason">One plain sentence when <see cref="CanAddHousehold"/> is false; null otherwise.</param>
/// <param name="MaxProfilesPerHousehold">How many profiles one household holds at most.</param>
/// <param name="PersonalItems">How many items of progress, bookmarks, favourites and collection items they carry.</param>
/// <param name="BlockedReason">One plain sentence when the file cannot be imported at all; null when it can.</param>
/// <param name="TwoStepToSetUp">True when the file holds no two-step secret for a sign-in because someone else made it, so those people set two-step sign-in up again after they sign in.</param>
public sealed record UserImportPreviewDto(
    [property: JsonPropertyName("operation_id")] Guid OperationId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("household_name")] string? HouseholdName,
    [property: JsonPropertyName("profiles")] IReadOnlyList<UserImportProfileDto> Profiles,
    [property: JsonPropertyName("sign_ins")] IReadOnlyList<UserImportSignInDto> SignIns,
    [property: JsonPropertyName("households")] IReadOnlyList<UserImportHouseholdDto> Households,
    [property: JsonPropertyName("can_add_household")] bool CanAddHousehold,
    [property: JsonPropertyName("add_household_blocked_reason")] string? AddHouseholdBlockedReason,
    [property: JsonPropertyName("max_profiles_per_household")] int MaxProfilesPerHousehold,
    [property: JsonPropertyName("personal_items")] int PersonalItems,
    [property: JsonPropertyName("blocked_reason")] string? BlockedReason,
    [property: JsonPropertyName("two_step_to_set_up")] bool TwoStepToSetUp = false);

/// <summary>
/// What the administrator chose for an import. Nothing here can grant a role or library access: roles are never carried, and library
/// access never is. Give exactly one of <see cref="TargetHouseholdId"/> and <see cref="NewHouseholdName"/>.
/// </summary>
/// <param name="TargetHouseholdId">An existing household to put the profiles in.</param>
/// <param name="NewHouseholdName">The name of a new household to make for them.</param>
/// <param name="NewHouseholdAdministratorProfileId">For a new household: the file's profile (one with a sign-in, not a Kids profile) that looks after it. Not given for an existing household.</param>
/// <param name="KeepAccess">True to keep Read, Watch, Listen and View as the file had them; false (the default) so the profiles arrive with no access.</param>
/// <param name="Emails">Per sign-in in the file (by its id): the email to use instead of the file's.</param>
/// <param name="WithoutSignIn">Sign-ins in the file (by id) to leave out: their profile arrives with no sign-in.</param>
public sealed record UserImportRequestDto(
    [property: JsonPropertyName("target_household_id")] Guid? TargetHouseholdId = null,
    [property: JsonPropertyName("new_household_name")] string? NewHouseholdName = null,
    [property: JsonPropertyName("new_household_administrator_profile_id")] Guid? NewHouseholdAdministratorProfileId = null,
    [property: JsonPropertyName("keep_access")] bool KeepAccess = false,
    [property: JsonPropertyName("emails")] IReadOnlyDictionary<Guid, string>? Emails = null,
    [property: JsonPropertyName("without_sign_in")] IReadOnlyList<Guid>? WithoutSignIn = null);

/// <summary>A profile an import made.</summary>
/// <param name="Id">The new profile.</param>
/// <param name="Name">Its name.</param>
/// <param name="SignInEmail">The email of the sign-in that opens it; null when it arrived with no sign-in.</param>
/// <param name="IsHouseholdAdministrator">True for the one chosen to look after a household the import made.</param>
public sealed record UserImportedProfileDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sign_in_email")] string? SignInEmail,
    [property: JsonPropertyName("is_household_administrator")] bool IsHouseholdAdministrator);

/// <summary>What a user import did. Imported people can sign in (when they kept a sign-in) but see only what the administrator kept for them.</summary>
/// <param name="HouseholdId">The household the profiles are in.</param>
/// <param name="HouseholdName">Its name.</param>
/// <param name="CreatedHousehold">True when the import made the household.</param>
/// <param name="Profiles">The profiles that came.</param>
/// <param name="WaitingItems">Items that wait for their titles to be found, and attach when they are.</param>
public sealed record UserImportResultDto(
    [property: JsonPropertyName("household_id")] Guid HouseholdId,
    [property: JsonPropertyName("household_name")] string HouseholdName,
    [property: JsonPropertyName("created_household")] bool CreatedHousehold,
    [property: JsonPropertyName("profiles")] IReadOnlyList<UserImportedProfileDto> Profiles,
    [property: JsonPropertyName("waiting_items")] int WaitingItems);
