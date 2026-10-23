using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Contracts;

/// <summary>What remote access needs to know about administrator sign-in.</summary>
public sealed record UsableAdministratorStatus(
    bool HasUsableAdministrator,
    bool HasRecoveryCodes,
    bool LocalhostBypassDisabled);

/// <summary>
/// What a change would take away from administrator sign-in, so it can be checked before it is made: a whole user
/// (disabled or deleted), or the server administrator role of one profile (switched off, or the profile is deleted).
/// </summary>
public readonly record struct AdministratorSignInLoss(Guid? AccountId, Guid? ProfileId)
{
    public static AdministratorSignInLoss OfAccount(Guid accountId) => new(accountId, null);

    public static AdministratorSignInLoss OfProfile(Guid profileId) => new(null, profileId);
}

/// <summary>Answers "can an administrator actually sign in right now?".</summary>
public interface IUsableAdministratorService
{
    /// <summary>
    /// True when at least one enabled administrator has a working sign-in method under <paramref name="policy"/>.
    /// Used by Settings to stop changes that would lock every administrator out.
    /// </summary>
    Task<bool> HasUsableAdministratorSignInAsync(AuthSettings policy, CancellationToken ct);

    /// <summary>
    /// True when at least one enabled administrator would still have a working sign-in method, under the current
    /// sign-in policy, once <paramref name="loss"/> has happened. Account changes use it to refuse leaving the server
    /// with no administrator who can sign in.
    /// </summary>
    Task<bool> HasUsableAdministratorSignInAfterAsync(AdministratorSignInLoss loss, CancellationToken ct);

    /// <summary>
    /// Remote-access view: an administrator must also have saved recovery codes.
    /// </summary>
    Task<UsableAdministratorStatus> EvaluateForRemoteAsync(CancellationToken ct);

    /// <summary>
    /// True while at least one enabled administrator has no password or passkey yet (it works only on this computer)
    /// and no other enabled administrator exists. Actions that would let others in wait until this is false.
    /// </summary>
    Task<bool> OnlyThisComputerAdministratorsAsync(CancellationToken ct);
}
