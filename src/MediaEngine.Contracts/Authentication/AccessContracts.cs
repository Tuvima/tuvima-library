using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

public sealed record AccountAccessResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("is_enabled")] bool IsEnabled,
    [property: JsonPropertyName("is_administrator")] bool IsAdministrator,
    [property: JsonPropertyName("authorization_version")] long AuthorizationVersion,
    [property: JsonPropertyName("feature_grants")] IReadOnlyList<AccountFeatureGrantDto> FeatureGrants,
    [property: JsonPropertyName("library_grants")] IReadOnlyList<AccountLibraryGrantDto> LibraryGrants,
    [property: JsonPropertyName("profile_grants")] IReadOnlyList<AccountProfileGrantDto> ProfileGrants,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("last_active_at")] DateTimeOffset? LastActiveAt,
    [property: JsonPropertyName("household_id")] Guid? HouseholdId = null,
    [property: JsonPropertyName("must_change_password")] bool MustChangePassword = false,
    [property: JsonPropertyName("temporary_password_expires_at")] DateTimeOffset? TemporaryPasswordExpiresAt = null);

public sealed record AccountFeatureGrantDto(
    [property: JsonPropertyName("feature")] string Feature,
    [property: JsonPropertyName("allowed")] bool Allowed);

public sealed record AccountLibraryGrantDto(
    [property: JsonPropertyName("library_id")] Guid LibraryId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("allowed")] bool Allowed);

public sealed record AccessLibraryOptionDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("area")] string Area);

public sealed record ManagedProfileResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string AvatarColor,
    [property: JsonPropertyName("avatar_path")] string? AvatarPath,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("household_id")] Guid? HouseholdId = null);

public sealed record CreateManagedProfileRequest(
    [property: JsonPropertyName("account_id")] Guid AccountId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string? AvatarColor,
    [property: JsonPropertyName("is_default")] bool IsDefault);

public sealed record UpdateManagedProfileRequest(
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string? AvatarColor);

public sealed record AccountProfileGrantDto(
    [property: JsonPropertyName("account_id")] Guid AccountId,
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_path")] string? AvatarPath,
    [property: JsonPropertyName("is_default")] bool IsDefault,
    [property: JsonPropertyName("is_enabled")] bool IsEnabled,
    [property: JsonPropertyName("admin_enabled")] bool AdminEnabled,
    [property: JsonPropertyName("admin_protection")] GrantAdminProtectionDto AdminProtection,
    [property: JsonPropertyName("authorization_version")] long AuthorizationVersion,
    [property: JsonPropertyName("granted_at")] DateTimeOffset GrantedAt,
    [property: JsonPropertyName("is_restricted")] bool IsRestricted = false);

public sealed record GrantAdminProtectionDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("unlock_mode")] string UnlockMode,
    [property: JsonPropertyName("unlock_minutes")] int? UnlockMinutes,
    [property: JsonPropertyName("protection_version")] long ProtectionVersion,
    [property: JsonPropertyName("is_locked_out")] bool IsLockedOut,
    [property: JsonPropertyName("locked_until")] DateTimeOffset? LockedUntil);

public sealed record CreateManagedAccountRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("is_administrator")] bool IsAdministrator,
    [property: JsonPropertyName("profile_id")] Guid? ProfileId,
    [property: JsonPropertyName("new_profile")] NewAccountProfileRequest? NewProfile,
    [property: JsonPropertyName("feature_ids")] IReadOnlyList<string> FeatureIds,
    [property: JsonPropertyName("library_ids")] IReadOnlyList<Guid> LibraryIds,
    [property: JsonPropertyName("temporary_password")] string? TemporaryPassword = null);

/// <summary>An administrator sets a new temporary password for a person; they must choose their own at next sign-in.</summary>
public sealed record SetTemporaryPasswordRequest(
    [property: JsonPropertyName("temporary_password")] string TemporaryPassword);

public sealed record NewAccountProfileRequest(
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string? AvatarColor);

public sealed record UpdateManagedAccountRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("is_enabled")] bool IsEnabled,
    [property: JsonPropertyName("is_administrator")] bool IsAdministrator);

public sealed record ReplaceAccountAccessRequest(
    [property: JsonPropertyName("feature_ids")] IReadOnlyList<string> FeatureIds,
    [property: JsonPropertyName("library_ids")] IReadOnlyList<Guid> LibraryIds);

public sealed record SetAccountProfileGrantAccessRequest(
    [property: JsonPropertyName("is_default")] bool IsDefault,
    [property: JsonPropertyName("admin_enabled")] bool AdminEnabled);

public sealed record SetGrantAdminProtectionRequest(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("pin")] string? Pin,
    [property: JsonPropertyName("unlock_mode")] string UnlockMode,
    [property: JsonPropertyName("unlock_minutes")] int? UnlockMinutes);

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

public sealed record AccountSecurityCapabilitiesResponse(
    [property: JsonPropertyName("has_password")] bool HasPassword,
    [property: JsonPropertyName("has_passkeys")] bool HasPasskeys,
    [property: JsonPropertyName("has_external_logins")] bool HasExternalLogins,
    [property: JsonPropertyName("password_sign_in_available")] bool PasswordSignInAvailable,
    [property: JsonPropertyName("can_register_passkey")] bool CanRegisterPasskey,
    [property: JsonPropertyName("passkey_ready")] bool PasskeyReady,
    [property: JsonPropertyName("external_sign_in_available")] bool ExternalSignInAvailable,
    [property: JsonPropertyName("available_external_providers")] IReadOnlyList<AccountExternalProviderResponse> AvailableExternalProviders);

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
