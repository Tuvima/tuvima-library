namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Which libraries are "Selected profiles" libraries (Library pages D9). The mode of each library is kept in the libraries file, so the one
/// visibility rule reads it through this small seam and keeps no copy. A Vault library is never in the set: the Vault has its own rules.
/// </summary>
public interface ILibraryAccessCatalog
{
    /// <summary>The ids of the libraries whose access is Selected profiles.</summary>
    ValueTask<IReadOnlySet<Guid>> GetSelectedLibraryIdsAsync(CancellationToken ct = default);
}
