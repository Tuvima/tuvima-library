namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Which profiles may see each library whose access is "Selected profiles" (Library pages D9). A Selected library is visible to server
/// administrators and to the profiles granted here and to nobody else; a library with no rows is visible to administrators only, and a
/// new profile has no rows. The mode itself (standard, selected, vault) is kept in the libraries file; this only holds the choice of
/// profiles. A Vault library is never accepted here.
/// </summary>
public interface ILibraryProfileGrantRepository
{
    /// <summary>The libraries this profile has been granted (Selected libraries it may see).</summary>
    Task<IReadOnlySet<Guid>> GetGrantedLibrariesAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>The profiles granted one library.</summary>
    Task<IReadOnlySet<Guid>> GetGrantedProfilesAsync(Guid libraryId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the profiles granted one library. Profiles that gain or lose the library have their authorization version raised in the
    /// same transaction. Refuses a Vault library and a profile that does not exist.
    /// </summary>
    Task ReplaceGrantedProfilesAsync(
        Guid libraryId, IReadOnlySet<Guid> profileIds, DateTimeOffset changedAt, Guid? changedByProfileId, CancellationToken ct = default);

    /// <summary>Removes every grant of one library (its access changed, or it was removed), raising the version of each profile that had it.</summary>
    Task ClearLibraryAsync(Guid libraryId, CancellationToken ct = default);
}
