using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Identity.Contracts;

public sealed record SessionIssueResult(
    AuthSession Session,
    Account Account,
    Profile Profile,
    Profile ActiveProfile,
    string PlaintextToken,
    IReadOnlyList<string> RecoveryCodes,
    bool ChooseProfile = false);

public sealed record SessionValidationResult(AuthSession Session, Account Account, Profile Profile, Profile ActiveProfile);

public sealed record AuthenticationAttemptResult(bool Succeeded, bool LockedOut, string? Error, SessionIssueResult? IssuedSession);

public interface IFirstPartyIdentityService
{
    Task<bool> IsAdministratorConfiguredAsync(CancellationToken ct = default);
    Task<SessionIssueResult> BootstrapAdministratorAsync(string email, string password, string displayName, string deviceId, string deviceName, string client, CancellationToken ct = default, string? pin = null, string ingress = ClientIngress.Remote);
    /// <summary>Creates the first administrator with a name and email but no password; it works only on this computer until secured.</summary>
    Task<SessionIssueResult> BootstrapThisComputerAdministratorAsync(string email, string displayName, string deviceId, string deviceName, string client, CancellationToken ct = default, string? pin = null);
    /// <summary>Signs in the this-computer-only account without a password; <c>null</c> when there is no such account.</summary>
    Task<SessionIssueResult?> SignInThisComputerAccountAsync(string deviceId, string deviceName, string client, CancellationToken ct = default);
    /// <summary>The display name of the this-computer-only account, or <c>null</c> when there is none.</summary>
    Task<string?> GetThisComputerAccountNameAsync(CancellationToken ct = default);
    Task<AuthenticationAttemptResult> AuthenticatePasswordAsync(string email, string password, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote);
    Task<SessionIssueResult> CreateExternalSessionAsync(Guid accountId, string provider, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote);
    Task<SessionIssueResult> CreatePasskeySessionAsync(Guid accountId, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote);
    Task<SessionIssueResult> AcceptInvitationAsync(string token, string password, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote);
    /// <param name="currentIngress">Where the request came from (<see cref="ClientIngress"/>). A home session is refused from outside. <c>null</c> means an internal caller whose request was already admitted, so no origin check is made.</param>
    Task<SessionValidationResult?> ValidateSessionAsync(string plaintextToken, bool touch = true, CancellationToken ct = default, string? currentIngress = null);
    Task<IReadOnlyList<AuthSession>> GetSessionsAsync(Guid accountId, CancellationToken ct = default);
    Task<bool> RevokeSessionAsync(Guid sessionId, string reason, CancellationToken ct = default);
    Task<int> RevokeOtherSessionsAsync(Guid accountId, Guid currentSessionId, string reason, CancellationToken ct = default);
    /// <summary>Changes the password. The caller has already checked that the person signed in or confirmed recently.</summary>
    Task ChangePasswordAsync(Guid accountId, string newPassword, Guid? currentSessionId = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResetPasswordWithRecoveryCodeAsync(string email, string recoveryCode, string newPassword, CancellationToken ct = default);
    Task<string?> BeginPasswordResetAsync(string email, CancellationToken ct = default);
    Task ResetPasswordWithTokenAsync(string token, string newPassword, CancellationToken ct = default);
    /// <summary>Replaces the recovery codes. The caller has already checked that the person signed in or confirmed recently.</summary>
    Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid accountId, CancellationToken ct = default);
    /// <summary>
    /// Turns a this-computer-only account into a normal one: sets a password and/or relies on a passkey that was just
    /// registered, clears the this-computer-only limit, ends every old session and issues a new one from this computer.
    /// Recovery codes come back only when a password was set.
    /// </summary>
    /// <exception cref="InvalidOperationException">The account is not a this-computer-only account, or no sign-in method was given.</exception>
    /// <summary>Throws (<see cref="InvalidOperationException"/>, <see cref="ArgumentException"/>) when securing with this password would be refused; changes nothing.</summary>
    Task ValidateSecureThisComputerAccountAsync(Guid accountId, string? password, CancellationToken ct = default);

    Task<SessionIssueResult> SecureThisComputerAccountAsync(Guid accountId, string? password, bool hasPasskey, string deviceId, string deviceName, string client, CancellationToken ct = default);
    /// <summary>
    /// True when the session is active and the person signed in or confirmed within <see cref="RecentSignIn.Window"/>.
    /// A this-computer session counts as recent: it has no password to ask for and works only on this computer.
    /// </summary>
    Task<bool> IsRecentlyAuthenticatedAsync(Guid sessionId, CancellationToken ct = default);
    /// <summary>Confirms it is them with their password. False when the password is wrong or the account is temporarily locked.</summary>
    Task<bool> ConfirmWithPasswordAsync(Guid accountId, Guid sessionId, string password, CancellationToken ct = default);
    /// <summary>Records a confirmation that was already proven another way (a passkey).</summary>
    Task<bool> ConfirmSessionAsync(Guid accountId, Guid sessionId, string method, CancellationToken ct = default);
    /// <summary>The profile this device always opens as for the signed-in account, or <c>null</c>. Scoped to the caller's own session.</summary>
    Task<Guid?> GetDeviceProfilePreferenceAsync(Guid accountId, Guid sessionId, CancellationToken ct = default);
    /// <exception cref="UnauthorizedAccessException">The session is not the caller's, or the account has no access to that profile.</exception>
    Task SetDeviceProfilePreferenceAsync(Guid accountId, Guid sessionId, Guid profileId, CancellationToken ct = default);
    Task<bool> ClearDeviceProfilePreferenceAsync(Guid accountId, Guid sessionId, CancellationToken ct = default);
    /// <summary>The subset of the given profiles that have a PIN (the picker shows a lock on them).</summary>
    Task<IReadOnlySet<Guid>> GetProfileIdsWithPinAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken ct = default);
    Task SetProfilePinAsync(Guid profileId, string? pin, CancellationToken ct = default);
    Task<SessionValidationResult> SwitchActiveProfileAsync(string sessionToken, Guid targetProfileId, string? pin, CancellationToken ct = default);
    Task<bool> ValidateServiceCredentialAsync(string plaintextToken, CancellationToken ct = default);
}
