using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

/// <summary>A profile a user delete would take with it, so the confirmation can name it (see <see cref="UserDeletionPreviewResponse"/>).</summary>
public sealed record AccountDeletionProfileDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("display_name")] string DisplayName);

/// <summary>
/// One library an administrator can hide from a person. <c>area</c> is <c>read</c>, <c>watch</c>, <c>listen</c> or <c>view</c>.
/// <c>hidden_from_you</c> is true only in a household administrator's view, when the library is hidden from the administrator's own
/// profile (they can hide it from others but never show it to anyone). A Vault library is never listed.
/// </summary>
public sealed record AccessLibraryOptionDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("area")] string Area,
    [property: JsonPropertyName("hidden_from_you")] bool HiddenFromYou = false);

public sealed record ManagedProfileResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string AvatarColor,
    [property: JsonPropertyName("avatar_path")] string? AvatarPath,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("household_id")] Guid? HouseholdId = null,
    [property: JsonPropertyName("is_restricted")] bool IsRestricted = false,
    [property: JsonPropertyName("avatar_icon")] string? AvatarIcon = null,
    [property: JsonPropertyName("content_limit")] string? ContentLimit = null,
    [property: JsonPropertyName("content_limit_allow_unrated")] bool ContentLimitAllowUnrated = false,
    [property: JsonPropertyName("is_server_admin")] bool IsServerAdmin = false,
    [property: JsonPropertyName("is_household_admin")] bool IsHouseholdAdmin = false,
    [property: JsonPropertyName("feature_ids")] IReadOnlyList<string>? FeatureIds = null,
    [property: JsonPropertyName("hidden_library_ids")] IReadOnlyList<Guid>? HiddenLibraryIds = null,
    [property: JsonPropertyName("user")] ManagedProfileUserDto? User = null);

/// <summary>
/// Adds a person to a household. A child person is a restricted profile; the PIN is optional.
/// <c>content_limit</c> is Everything (<c>""</c>), <c>G</c>, <c>PG</c>, <c>PG-13</c> or <c>R</c>; left out, a child person starts on
/// <c>PG</c> and anyone else on Everything.
/// </summary>
public sealed record AddHouseholdPersonRequest(
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string? AvatarColor,
    [property: JsonPropertyName("is_child")] bool IsChild,
    [property: JsonPropertyName("pin")] string? Pin,
    [property: JsonPropertyName("avatar_icon")] string? AvatarIcon = null,
    [property: JsonPropertyName("content_limit")] string? ContentLimit = null,
    [property: JsonPropertyName("content_limit_allow_unrated")] bool ContentLimitAllowUnrated = false);

/// <summary>
/// Changes a person's name and look. <c>content_limit</c> left out keeps the current limit; <c>""</c> sets Everything; otherwise
/// <c>G</c>, <c>PG</c>, <c>PG-13</c> or <c>R</c>. <c>content_limit_allow_unrated</c> left out keeps the current choice.
/// </summary>
public sealed record UpdateManagedProfileRequest(
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string? AvatarColor,
    [property: JsonPropertyName("avatar_icon")] string? AvatarIcon = null,
    [property: JsonPropertyName("content_limit")] string? ContentLimit = null,
    [property: JsonPropertyName("content_limit_allow_unrated")] bool? ContentLimitAllowUnrated = null);

public sealed record AccountProfileGrantDto(
    [property: JsonPropertyName("account_id")] Guid AccountId,
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_path")] string? AvatarPath,
    [property: JsonPropertyName("is_default")] bool IsDefault,
    [property: JsonPropertyName("is_enabled")] bool IsEnabled,
    [property: JsonPropertyName("admin_enabled")] bool AdminEnabled,
    [property: JsonPropertyName("admin_protection")] ProfileAdminProtectionResponse AdminProtection,
    [property: JsonPropertyName("authorization_version")] long AuthorizationVersion,
    [property: JsonPropertyName("granted_at")] DateTimeOffset GrantedAt,
    [property: JsonPropertyName("is_restricted")] bool IsRestricted = false);

/// <summary>
/// "Ask for my PIN on admin pages" for one profile (<c>GET /access/profiles/{id}/admin-protection</c>): whether it is on, how long an
/// answer lasts (<c>unlock_mode</c> is FixedDuration, UntilProfileSwitch or LockOnLeave; <c>unlock_minutes</c> only for FixedDuration)
/// and the version that changes whenever the setting does. The PIN asked for is the profile's own; it is never sent.
/// </summary>
public sealed record ProfileAdminProtectionResponse(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("unlock_mode")] string UnlockMode,
    [property: JsonPropertyName("unlock_minutes")] int? UnlockMinutes,
    [property: JsonPropertyName("protection_version")] long ProtectionVersion);

/// <summary>
/// Switches the admin PIN question on or off for a profile that holds an administrator role (<c>PUT /access/profiles/{id}/admin-protection</c>).
/// A person changes their own profile's; a server administrator any; a household administrator never another administrator's.
/// </summary>
public sealed record SetProfileAdminProtectionRequest(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("unlock_mode")] string UnlockMode,
    [property: JsonPropertyName("unlock_minutes")] int? UnlockMinutes);

/// <summary>
/// An administrator sets a new password for a person. By default it is temporary and they must choose their own at next
/// sign-in; <c>must_change_password: false</c> sets an ordinary password instead.
/// </summary>
public sealed record SetTemporaryPasswordRequest(
    [property: JsonPropertyName("temporary_password")] string TemporaryPassword,
    [property: JsonPropertyName("must_change_password")] bool MustChangePassword = true);

/// <summary>An administrator changes the email a person signs in with.</summary>
public sealed record ChangeAccountEmailRequest(
    [property: JsonPropertyName("email")] string Email);

public sealed record GrantAdminUnlockRequest(
    [property: JsonPropertyName("pin")] string Pin);

public sealed record GrantAdminUnlockResponse(
    [property: JsonPropertyName("unlocked")] bool Unlocked,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("protection_version")] long ProtectionVersion);

public sealed record AccountSelfServiceResponse(
    [property: JsonPropertyName("account_id")] Guid AccountId,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("active_profile_id")] Guid ActiveProfileId,
    [property: JsonPropertyName("default_profile_id")] Guid DefaultProfileId,
    [property: JsonPropertyName("profiles")] IReadOnlyList<AccountProfileGrantDto> Profiles,
    [property: JsonPropertyName("authentication_methods")] IReadOnlyList<string> AuthenticationMethods,
    [property: JsonPropertyName("security_capabilities")] AccountSecurityCapabilitiesResponse SecurityCapabilities);

/// <summary>The one-time reminders an account can say "Not now" to. Only these keys are accepted.</summary>
public static class AccountNoticeKeys
{
    /// <summary>Reminder to add a password so the Library works away from home.</summary>
    public const string SecureAccount = "secure-account";

    public static bool IsKnown(string? key) => key == SecureAccount;
}

public sealed record AccountNoticesResponse(
    [property: JsonPropertyName("dismissed")] IReadOnlyList<string> Dismissed);

public sealed record AccountSecurityCapabilitiesResponse(
    [property: JsonPropertyName("has_password")] bool HasPassword,
    [property: JsonPropertyName("has_passkeys")] bool HasPasskeys,
    [property: JsonPropertyName("has_external_logins")] bool HasExternalLogins,
    [property: JsonPropertyName("password_sign_in_available")] bool PasswordSignInAvailable,
    [property: JsonPropertyName("can_register_passkey")] bool CanRegisterPasskey,
    [property: JsonPropertyName("passkey_ready")] bool PasskeyReady,
    [property: JsonPropertyName("external_sign_in_available")] bool ExternalSignInAvailable,
    [property: JsonPropertyName("available_external_providers")] IReadOnlyList<AccountExternalProviderResponse> AvailableExternalProviders,
    [property: JsonPropertyName("has_two_step")] bool HasTwoStep = false);

public sealed record AccountExternalProviderResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName);

public sealed record AuthorizationAuditEntryResponse(
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("actor_account_id")] Guid? ActorAccountId,
    [property: JsonPropertyName("actor_profile_id")] Guid? ActorProfileId,
    [property: JsonPropertyName("actor_application_id")] Guid? ActorApplicationId,
    [property: JsonPropertyName("subject_type")] string SubjectType,
    [property: JsonPropertyName("subject_id")] string SubjectId,
    [property: JsonPropertyName("changes")] IReadOnlyDictionary<string, string?> Changes);

/// <summary>
/// One person's signed-in sessions as the Devices page shows them to an administrator. <c>account_label</c> is the email,
/// or the person's name when the sign-in has no email; <c>profile_display_name</c> is the person the user opens.
/// </summary>
public sealed record ManagedAccountSessionsResponse(
    [property: JsonPropertyName("account_id")] Guid AccountId,
    [property: JsonPropertyName("account_label")] string AccountLabel,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("household_id")] Guid? HouseholdId,
    [property: JsonPropertyName("profile_display_name")] string? ProfileDisplayName,
    [property: JsonPropertyName("sessions")] IReadOnlyList<DeviceSessionResponse> Sessions);
