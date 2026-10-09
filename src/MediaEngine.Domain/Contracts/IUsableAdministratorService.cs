using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Contracts;

/// <summary>What remote access needs to know about administrator sign-in.</summary>
public sealed record UsableAdministratorStatus(
    bool HasUsableAdministrator,
    bool HasRecoveryCodes,
    bool LocalhostBypassDisabled);

/// <summary>Answers "can an administrator actually sign in right now?".</summary>
public interface IUsableAdministratorService
{
    /// <summary>
    /// True when at least one enabled administrator has a working sign-in method under <paramref name="policy"/>.
    /// Used by Settings to stop changes that would lock every administrator out.
    /// </summary>
    Task<bool> HasUsableAdministratorSignInAsync(AuthSettings policy, CancellationToken ct);

    /// <summary>
    /// Remote-access view: an administrator must also have saved recovery codes.
    /// </summary>
    Task<UsableAdministratorStatus> EvaluateForRemoteAsync(CancellationToken ct);
}
