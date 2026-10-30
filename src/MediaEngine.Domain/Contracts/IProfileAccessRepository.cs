using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// What a profile may see and do: administrator roles, the areas it reaches, the admin PIN question and its
/// unlocks, and which household is the default. A user (sign-in) is attached to one profile. Every change to a profile's access
/// raises <c>profiles.authorization_version</c> so open screens and sessions re-check.
/// </summary>
public interface IProfileAccessRepository
{
    /// <summary>
    /// The areas (Read, Watch, Listen, View) the profile can reach. Worked out, never stored: there are no per-profile area switches
    /// (library access v4). Read, Watch and Listen are open to every profile that exists; View, the Library's shared photos and videos,
    /// is open only to profiles of the default household. Empty when the profile does not exist, so an unknown profile sees nothing.
    /// Which libraries inside an area a profile sees is the household's choice alone (<c>IHouseholdLibraryAccessRepository</c>).
    /// </summary>
    Task<IReadOnlySet<AccountFeatureId>> GetAreasAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>The profile's administrator roles, or <see langword="null"/> when the profile does not exist.</summary>
    Task<ProfileAdminFlags?> GetAdminFlagsAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Sets the profile's server and household administrator roles and raises its authorization version. Returns
    /// <see langword="false"/> when the profile does not exist. This is storage only; who may be an administrator is decided by the caller.
    /// </summary>
    Task<bool> SetAdminFlagsAsync(Guid profileId, bool isServerAdministrator, bool isHouseholdAdministrator, DateTimeOffset changedAt, CancellationToken ct = default);

    /// <summary>
    /// Makes the profile a server administrator, or takes that away, and raises its authorization version. Returns
    /// <see langword="false"/> when the profile does not exist. Making one also marks the profile's household as the default household
    /// when none is marked yet. Taking it away is refused (<see cref="InvalidOperationException"/>) when no other server administrator
    /// profile would be left with an enabled user who can sign in. This is storage only; whether the profile may be an administrator
    /// (default household, not a child profile) is decided by the caller.
    /// </summary>
    Task<bool> SetServerAdministratorAsync(Guid profileId, bool isServerAdministrator, DateTimeOffset changedAt, CancellationToken ct = default);

    /// <summary>
    /// Makes the profile a household administrator, or takes that away, and raises its authorization version. Returns
    /// <see langword="false"/> when the profile does not exist. A household administrator always has a user: making one is refused
    /// (<see cref="InvalidOperationException"/>) when no user is attached to the profile.
    /// </summary>
    Task<bool> SetHouseholdAdministratorAsync(Guid profileId, bool isHouseholdAdministrator, DateTimeOffset changedAt, CancellationToken ct = default);

    /// <summary>
    /// True when another profile in the same household is a household administrator with an enabled user. The one meaning of "a
    /// household administrator who counts" (D7): a household administrator profile whose user is disabled does not count.
    /// </summary>
    Task<bool> HasAnotherHouseholdAdministratorAsync(Guid profileId, CancellationToken ct = default);

    Task<ProfileAdminProtection?> GetAdminProtectionAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Saves the admin PIN question for the profile. The protection version goes up on every save, earlier unlocks are cleared and
    /// the profile's authorization version is raised.
    /// </summary>
    Task SetAdminProtectionAsync(ProfileAdminProtection protection, CancellationToken ct = default);

    /// <summary>The unlock for this session and profile when it has not run out at <paramref name="now"/>.</summary>
    Task<ProfileAdminUnlock?> GetAdminUnlockAsync(Guid sessionId, Guid profileId, DateTimeOffset now, CancellationToken ct = default);

    Task SetAdminUnlockAsync(ProfileAdminUnlock unlock, CancellationToken ct = default);

    Task ClearAdminUnlockAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>The user (sign-in) attached to the profile, or <see langword="null"/> when it has none.</summary>
    Task<Guid?> FindUserIdOfProfileAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>The default household (the server administrator's original one), or <see langword="null"/> when none is marked yet.</summary>
    Task<Guid?> GetDefaultHouseholdIdAsync(CancellationToken ct = default);

    /// <summary>Makes one household the default and clears the mark on any other. Returns <see langword="false"/> when it does not exist.</summary>
    Task<bool> SetDefaultHouseholdAsync(Guid householdId, CancellationToken ct = default);
}
