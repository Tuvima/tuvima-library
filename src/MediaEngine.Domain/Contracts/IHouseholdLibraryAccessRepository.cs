namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The libraries hidden for a whole household (library access D2): the household ceiling. A library is visible to a profile only when it
/// is not hidden here for the profile's household: this is the only library switch (library access v4; there are no per-profile library
/// rows). Nothing is hidden by default, so a new library and a new household need no rows. The Vault is never listed (the repository refuses a Vault id):
/// its access is per profile. The rows are configuration, so they are part of a backup.
/// </summary>
public interface IHouseholdLibraryAccessRepository
{
    /// <summary>The libraries hidden for one household; empty when none or when the household does not exist.</summary>
    Task<IReadOnlySet<Guid>> GetHiddenLibrariesAsync(Guid householdId, CancellationToken ct = default);

    /// <summary>The libraries hidden for the household the profile belongs to; empty when none or when the profile does not exist.</summary>
    Task<IReadOnlySet<Guid>> GetHiddenLibrariesForProfileAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the household's hidden libraries in one step. Every profile in the household has its authorization version raised in the
    /// same transaction when the list really changed, so open screens and paired apps check again. Returns <see langword="false"/> when
    /// the household does not exist. Throws <see cref="ArgumentException"/> when a Vault library is named.
    /// <paramref name="changedByProfileId"/> is the administrator's profile that saved it, kept on each row.
    /// </summary>
    Task<bool> ReplaceHiddenLibrariesAsync(
        Guid householdId, IReadOnlySet<Guid> hiddenLibraries, DateTimeOffset changedAt, Guid? changedByProfileId, CancellationToken ct = default);
}
