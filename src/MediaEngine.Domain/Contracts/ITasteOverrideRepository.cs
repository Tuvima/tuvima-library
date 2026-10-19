using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The changes people made to their own taste. The learned taste profile is never modified; these rows are applied when it is read.
/// </summary>
public interface ITasteOverrideRepository
{
    /// <summary>Every override for the given profiles. <see langword="null"/> means every profile.</summary>
    Task<IReadOnlyList<TasteOverride>> ListAsync(IReadOnlyCollection<Guid>? profileIds, CancellationToken ct = default);

    /// <summary>Sets the person's change for one value, replacing an earlier change to the same value.</summary>
    Task SetAsync(Guid profileId, string group, string value, string action, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>The most common genres across the library, for the Add control's suggestions.</summary>
    Task<IReadOnlyList<string>> ListKnownGenresAsync(int limit, CancellationToken ct = default);
}
