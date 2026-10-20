namespace MediaEngine.Domain.Contracts;

/// <summary>The order a person last chose on each "View all" grid, remembered per profile.</summary>
public interface IProfileGridPreferenceRepository
{
    /// <summary>The saved sort for each grid key the profile has chosen one for. A grid with no entry uses its default order.</summary>
    Task<IReadOnlyDictionary<string, string>> GetSortsAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Remembers (or replaces) the sort for one grid. Throws <see cref="KeyNotFoundException"/> when the profile does not exist.</summary>
    Task SaveSortAsync(Guid profileId, string gridKey, string sort, CancellationToken ct = default);
}
