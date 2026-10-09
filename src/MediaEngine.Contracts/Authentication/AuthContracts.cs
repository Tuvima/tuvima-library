using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

public sealed record AuthBootstrapStatusResponse(
    [property: JsonPropertyName("administrator_configured")] bool AdministratorConfigured);

/// <summary>
/// What a visitor can use to sign in from where they are right now. Deliberately holds no account, email or
/// profile information: it is answered before anyone has signed in.
/// </summary>
public sealed record SignInMethodsResponse(
    [property: JsonPropertyName("password")] bool Password,
    [property: JsonPropertyName("passkey")] bool Passkey,
    [property: JsonPropertyName("external_providers")] IReadOnlyList<AccountExternalProviderResponse> ExternalProviders,
    [property: JsonPropertyName("invitation_code")] bool InvitationCode);

public sealed class BootstrapAdministratorRequest
{
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    [JsonPropertyName("password")] public string Password { get; init; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = "Administrator";
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("client")] public string Client { get; init; } = "Dashboard";
}

/// <summary>Asks the Engine to sign in the account that works only on this computer (no password).</summary>
public sealed class ThisComputerSignInRequest
{
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("client")] public string Client { get; init; } = "Dashboard";
}

/// <summary>Whose account would be signed in on this computer. Only ever answered to a visitor on this computer.</summary>
public sealed record ThisComputerAccountResponse(
    [property: JsonPropertyName("display_name")] string DisplayName);

public sealed class LocalLoginRequest
{
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("password")] public string? Password { get; init; }
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("client")] public string Client { get; init; } = "Dashboard";
    [JsonPropertyName("original_client_ingress")] public string OriginalClientIngress { get; init; } = ClientIngressValues.Remote;
    [JsonPropertyName("original_client_is_https")] public bool OriginalClientIsHttps { get; init; }
}

public sealed class ExternalSessionRequest
{
    [JsonPropertyName("transaction_ticket")] public string TransactionTicket { get; init; } = string.Empty;
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("client")] public string Client { get; init; } = "Dashboard";
    [JsonPropertyName("original_client_ingress")] public string OriginalClientIngress { get; init; } = ClientIngressValues.Remote;
    [JsonPropertyName("original_client_is_https")] public bool OriginalClientIsHttps { get; init; }
}

public static class ExternalIdentityTransactionPurposes
{
    public const string SignIn = "sign_in";
    public const string Link = "link";
}

public sealed class BeginExternalIdentityTransactionRequest
{
    [JsonPropertyName("purpose")] public string Purpose { get; init; } = string.Empty;
    [JsonPropertyName("provider")] public string Provider { get; init; } = string.Empty;
    [JsonPropertyName("issuer")] public string Issuer { get; init; } = string.Empty;
    [JsonPropertyName("subject")] public string Subject { get; init; } = string.Empty;
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
}

public sealed record ExternalIdentityTransactionResponse(
    [property: JsonPropertyName("ticket")] string Ticket,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed class AuthSessionResponse
{
    [JsonPropertyName("session_id")] public Guid SessionId { get; init; }
    [JsonPropertyName("session_token")] public string SessionToken { get; init; } = string.Empty;
    [JsonPropertyName("account_id")] public Guid AccountId { get; init; }
    [JsonPropertyName("active_profile_id")] public Guid ActiveProfileId { get; init; }
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("authority")] public required DashboardAuthorityResponse Authority { get; init; }
    [JsonPropertyName("authentication_method")] public string AuthenticationMethod { get; init; } = string.Empty;
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; init; }
    [JsonPropertyName("recovery_codes")] public IReadOnlyList<string> RecoveryCodes { get; init; } = [];
}

public sealed class SessionValidationResponse
{
    [JsonPropertyName("session_id")] public Guid SessionId { get; init; }
    [JsonPropertyName("account_id")] public Guid AccountId { get; init; }
    [JsonPropertyName("active_profile_id")] public Guid ActiveProfileId { get; init; }
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("authority")] public required DashboardAuthorityResponse Authority { get; init; }
    [JsonPropertyName("authentication_method")] public string AuthenticationMethod { get; init; } = string.Empty;
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class DeviceSessionResponse
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("account_id")] public Guid AccountId { get; init; }
    [JsonPropertyName("active_profile_id")] public Guid ActiveProfileId { get; init; }
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("client")] public string Client { get; init; } = string.Empty;
    [JsonPropertyName("authentication_method")] public string AuthenticationMethod { get; init; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("last_seen_at")] public DateTimeOffset LastSeenAt { get; init; }
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; init; }
    [JsonPropertyName("revoked_at")] public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class ChangePasswordRequest
{
    [JsonPropertyName("new_password")] public string NewPassword { get; init; } = string.Empty;
}

public sealed class RecoverPasswordRequest
{
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    [JsonPropertyName("recovery_code")] public string RecoveryCode { get; init; } = string.Empty;
    [JsonPropertyName("new_password")] public string NewPassword { get; init; } = string.Empty;
    [JsonPropertyName("original_client_ingress")] public string OriginalClientIngress { get; init; } = ClientIngressValues.Remote;
    [JsonPropertyName("original_client_is_https")] public bool OriginalClientIsHttps { get; init; }
}

public sealed class SetProfilePinRequest
{
    [JsonPropertyName("pin")] public string Pin { get; init; } = string.Empty;
}

/// <summary>
/// Turns the this-computer-only account into a normal one. A password, a freshly made passkey, or both; the Engine
/// answers with the new session (and recovery codes when a password was set).
/// </summary>
public sealed class SecureAccountRequest
{
    [JsonPropertyName("password")] public string? Password { get; init; }
    [JsonPropertyName("passkey_credential_json")] public string? PasskeyCredentialJson { get; init; }
    [JsonPropertyName("passkey_state")] public string? PasskeyState { get; init; }
    [JsonPropertyName("passkey_name")] public string? PasskeyName { get; init; }
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("client")] public string Client { get; init; } = "Dashboard";
}

/// <summary>
/// The "Confirm it's you" check: the person's password, or a passkey assertion (credential and state from
/// <c>POST /auth/confirm/passkey-options</c>). Exactly one of the two.
/// </summary>
public sealed class ConfirmItsYouRequest
{
    [JsonPropertyName("password")] public string? Password { get; init; }
    [JsonPropertyName("credential_json")] public string? CredentialJson { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
}

public sealed record RevokeOtherSessionsResponse(
    [property: JsonPropertyName("revoked_count")] int RevokedCount);

public sealed record BeginPasswordResetRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("original_client_ingress")] string OriginalClientIngress = ClientIngressValues.Remote,
    [property: JsonPropertyName("original_client_is_https")] bool OriginalClientIsHttps = false);
public sealed record BeginPasswordResetResponse([property: JsonPropertyName("token")] string? Token);
public sealed record ResetPasswordTokenRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("new_password")] string NewPassword,
    [property: JsonPropertyName("original_client_ingress")] string OriginalClientIngress = ClientIngressValues.Remote,
    [property: JsonPropertyName("original_client_is_https")] bool OriginalClientIsHttps = false);

public sealed record PasskeyOptionsResponse(
    [property: JsonPropertyName("options_json")] string OptionsJson,
    [property: JsonPropertyName("state")] string State);
public sealed record BeginPasskeyLoginRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("original_client_ingress")] string OriginalClientIngress = ClientIngressValues.Remote,
    [property: JsonPropertyName("original_client_is_https")] bool OriginalClientIsHttps = false);
public sealed record BeginPasskeyRegistrationRequest(
    [property: JsonPropertyName("original_client_ingress")] string OriginalClientIngress = ClientIngressValues.Remote,
    [property: JsonPropertyName("original_client_is_https")] bool OriginalClientIsHttps = false);
public sealed record CompletePasskeyLoginRequest(
    [property: JsonPropertyName("credential_json")] string CredentialJson,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("original_client_ingress")] string OriginalClientIngress = ClientIngressValues.Remote,
    [property: JsonPropertyName("original_client_is_https")] bool OriginalClientIsHttps = false);
public sealed record CompletePasskeyRegistrationRequest(
    [property: JsonPropertyName("credential_json")] string CredentialJson,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("original_client_ingress")] string OriginalClientIngress = ClientIngressValues.Remote,
    [property: JsonPropertyName("original_client_is_https")] bool OriginalClientIsHttps = false);
public sealed record CompletePasskeyElevationRequest(
    [property: JsonPropertyName("credential_json")] string CredentialJson,
    [property: JsonPropertyName("state")] string State);
public sealed record PasskeyCredentialResponse(
    [property: JsonPropertyName("credential_id")] string CredentialId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("backed_up")] bool BackedUp);

public sealed class ElevateAdministratorRequest
{
    [JsonPropertyName("secret")] public string Secret { get; init; } = string.Empty;
}

public sealed record AdministratorElevationResponse(
    [property: JsonPropertyName("elevated")] bool Elevated,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed class AccountResponse
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("is_enabled")] public bool IsEnabled { get; init; }
    [JsonPropertyName("profile_ids")] public IReadOnlyList<Guid> ProfileIds { get; init; } = [];
    [JsonPropertyName("default_profile_id")] public Guid? DefaultProfileId { get; init; }
}

public sealed class CreateAccountRequest
{
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("profile_ids")] public IReadOnlyList<Guid> ProfileIds { get; init; } = [];
    [JsonPropertyName("default_profile_id")] public Guid? DefaultProfileId { get; init; }
}
public sealed record SetAccountProfileGrantRequest(
    [property: JsonPropertyName("is_default")] bool IsDefault = false);

public sealed class CreateAccountInvitationRequest
{
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    [JsonPropertyName("profile_ids")] public IReadOnlyList<Guid> ProfileIds { get; init; } = [];
    [JsonPropertyName("default_profile_id")] public Guid? DefaultProfileId { get; init; }
    /// <summary>Invite someone outside the household: they start a household of their own with this first person.</summary>
    [JsonPropertyName("new_household_person_name")] public string? NewHouseholdPersonName { get; init; }
}
public sealed record AccountInvitationResponse(
    [property: JsonPropertyName("account_id")] Guid AccountId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
public sealed record AcceptAccountInvitationRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("original_client_ingress")] string OriginalClientIngress = ClientIngressValues.Remote,
    [property: JsonPropertyName("original_client_is_https")] bool OriginalClientIsHttps = false);

public sealed class LinkAccountExternalLoginRequest
{
    [JsonPropertyName("transaction_ticket")] public string TransactionTicket { get; init; } = string.Empty;
}

public sealed class AccountExternalLoginDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("account_id")] public Guid AccountId { get; init; }
    [JsonPropertyName("provider")] public string Provider { get; init; } = string.Empty;
    [JsonPropertyName("issuer")] public string Issuer { get; init; } = string.Empty;
    [JsonPropertyName("subject")] public string Subject { get; init; } = string.Empty;
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("linked_at")] public DateTimeOffset LinkedAt { get; init; }
    [JsonPropertyName("last_login_at")] public DateTimeOffset? LastLoginAt { get; init; }
}

public sealed class SwitchProfileRequest
{
    [JsonPropertyName("profile_id")] public Guid ProfileId { get; init; }
    [JsonPropertyName("secret")] public string? Secret { get; init; }
}

public sealed record RecoveryCodesResponse(
    [property: JsonPropertyName("recovery_codes")] IReadOnlyList<string> RecoveryCodes);

public sealed record IntercomTokenResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record DashboardServiceCredentialBundle(
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("protected_token")] string ProtectedToken,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record DashboardAuthorityResponse(
    [property: JsonPropertyName("account_id")] Guid AccountId,
    [property: JsonPropertyName("active_profile_id")] Guid ActiveProfileId,
    [property: JsonPropertyName("account_enabled")] bool AccountEnabled,
    [property: JsonPropertyName("grant_enabled")] bool GrantEnabled,
    [property: JsonPropertyName("account_authorization_version")] long AccountAuthorizationVersion,
    [property: JsonPropertyName("grant_authorization_version")] long GrantAuthorizationVersion,
    [property: JsonPropertyName("effective_administrator")] bool EffectiveAdministrator,
    [property: JsonPropertyName("administrator_surface_unlocked")] bool AdministratorSurfaceUnlocked,
    [property: JsonPropertyName("administrator_unlock_expires_at")] DateTimeOffset? AdministratorUnlockExpiresAt,
    [property: JsonPropertyName("administrator_protection_version")] long AdministratorProtectionVersion,
    [property: JsonPropertyName("profile_grants")] IReadOnlyList<AccountProfileGrantDto> ProfileGrants,
    [property: JsonPropertyName("navigation_capabilities")] IReadOnlyList<string> NavigationCapabilities,
    [property: JsonPropertyName("action_capabilities")] IReadOnlyList<string> ActionCapabilities);
