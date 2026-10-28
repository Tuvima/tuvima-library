namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Which Vault sections (Vault libraries) each profile may open once its Vault mode is on (Vault sections D28, D39). Nothing is granted by
/// default: a profile opens no section until an administrator ticks it. Server administrators open every section without a row. A profile
/// with a content limit or a Kids profile never has one. These are not account library grants; a Vault library is never one.
/// </summary>
public interface IVaultSectionGrantRepository
{
    /// <summary>The Vault libraries this profile has been granted (hyphenated GUID text, compared case-insensitively).</summary>
    Task<IReadOnlySet<string>> GetGrantedAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the sections one profile may open. Refuses a library that is not a Vault library and a profile that does not exist or is a
    /// Kids or content-limited profile. Grants are read live on every request, so no version needs raising.
    /// </summary>
    Task ReplaceGrantedAsync(
        Guid profileId, IReadOnlyCollection<string> libraryIds, DateTimeOffset changedAt, Guid? changedByProfileId, CancellationToken ct = default);
}
