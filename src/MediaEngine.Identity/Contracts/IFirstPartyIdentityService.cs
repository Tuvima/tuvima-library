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

public sealed record AuthenticationAttemptResult(bool Succeeded, bool LockedOut, string? Error, SessionIssueResult? IssuedSession);

public interface IFirstPartyIdentityService
{
    Task<bool> IsAdministratorConfiguredAsync(CancellationToken ct = default);
    Task<SessionIssueResult> BootstrapAdministratorAsync(string email, string password, string displayName, string deviceId, string deviceName, string client, CancellationToken ct = default, string? pin = null, string ingress = ClientIngress.HomeNetwork);
    Task<AuthenticationAttemptResult> AuthenticatePasswordAsync(string email, string password, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork);
    Task<AuthenticationAttemptResult> AuthenticatePinAsync(Guid profileId, string pin, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork);
    Task<SessionIssueResult> CreateExternalSessionAsync(Guid accountId, string provider, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork);
    Task<SessionIssueResult> CreatePasskeySessionAsync(Guid accountId, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork);
    Task<SessionIssueResult> AcceptInvitationAsync(string token, string password, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork);
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
