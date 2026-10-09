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
    IReadOnlyList<string> RecoveryCodes);

public sealed record SessionValidationResult(AuthSession Session, Account Account, Profile Profile, Profile ActiveProfile);

/// <summary>Whose sign-in an invitation code would create, and until when the code works.</summary>
public sealed record InvitationPreview(string Email, DateTimeOffset ExpiresAt);

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
    /// <summary>Looks at an invitation code without using it up; <c>null</c> when it is wrong, used or expired.</summary>
    Task<InvitationPreview?> PreviewInvitationAsync(string code, CancellationToken ct = default);
    /// <summary>
    /// Gives the account an administrator-chosen password that works only to choose a password of its own, until
    /// <paramref name="expiresAt"/>. Every existing session of the account ends.
    /// </summary>
    Task SetTemporaryPasswordAsync(Guid accountId, string password, DateTimeOffset expiresAt, CancellationToken ct = default);
    /// <summary>
    /// Replaces a temporary password with the person's own, ends all of the account's sessions and returns a new
    /// normal one.
    /// </summary>
    Task<SessionIssueResult> ChangeTemporaryPasswordAsync(Guid accountId, string currentPassword, string newPassword, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote);
    Task<SessionIssueResult> AcceptInvitationAsync(string code, string password, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote);
    /// <param name="currentIngress">Where the request came from (<see cref="ClientIngress"/>). A home session is refused from outside. <c>null</c> means an internal caller whose request was already admitted, so no origin check is made.</param>
    Task<SessionValidationResult?> ValidateSessionAsync(string plaintextToken, bool touch = true, CancellationToken ct = default, string? currentIngress = null);
    Task<IReadOnlyList<AuthSession>> GetSessionsAsync(Guid accountId, CancellationToken ct = default);
    Task<bool> RevokeSessionAsync(Guid sessionId, string reason, CancellationToken ct = default);
    Task<int> RevokeOtherSessionsAsync(Guid accountId, Guid currentSessionId, string reason, CancellationToken ct = default);
    Task ChangePasswordAsync(Guid accountId, string currentPassword, string newPassword, Guid? currentSessionId = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResetPasswordWithRecoveryCodeAsync(string email, string recoveryCode, string newPassword, CancellationToken ct = default);
    Task<string?> BeginPasswordResetAsync(string email, CancellationToken ct = default);
    Task ResetPasswordWithTokenAsync(string token, string newPassword, CancellationToken ct = default);
    Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid accountId, string currentPassword, CancellationToken ct = default);
    Task SetProfilePinAsync(Guid profileId, string? pin, CancellationToken ct = default);
    Task<SessionValidationResult> SwitchActiveProfileAsync(string sessionToken, Guid targetProfileId, string? pin, CancellationToken ct = default);
    Task<bool> ValidateServiceCredentialAsync(string plaintextToken, CancellationToken ct = default);
}
