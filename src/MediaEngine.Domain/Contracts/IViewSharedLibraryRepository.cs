using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Domain.Contracts;

public interface IViewSharedLibraryRepository
{
    /// <summary>The server's own Shared library: the one of the server administrator's household (else the oldest household).</summary>
    Task<ViewSharedLibrary> GetAsync(CancellationToken ct = default);
    /// <summary>A household's Shared library, or <see langword="null"/> when it has not needed one yet.</summary>
    Task<ViewSharedLibrary?> FindForHouseholdAsync(Guid householdId, CancellationToken ct = default);
    /// <summary>A household's Shared library, created now when it does not exist yet.</summary>
    Task<ViewSharedLibrary> EnsureForHouseholdAsync(Guid householdId, CancellationToken ct = default);
    Task<IReadOnlyList<ViewSharedSource>> GetSourcesAsync(CancellationToken ct = default);
    Task<ViewSharedSource> UpsertSourceAsync(ViewSharedSource source, CancellationToken ct = default);
    Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken ct = default);
}
