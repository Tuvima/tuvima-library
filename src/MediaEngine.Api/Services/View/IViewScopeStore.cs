namespace MediaEngine.Api.Services.View;

/// <summary>
/// Narrow persistence seam for View scope policy. Implementations must return
/// only current state; callers never receive physical libraries until after the
/// resolver applies policy.
/// </summary>
public interface IViewScopeStore
{
    Task<ViewScopeStoreEntry?> FindProfileAsync(Guid profileId, CancellationToken ct = default);

    Task<IReadOnlyList<ViewScopeStoreEntry>> GetProfilesAsync(CancellationToken ct = default);
    Task<Guid?> GetSharedLibraryIdAsync(CancellationToken ct = default) => Task.FromResult<Guid?>(null);

    /// <summary>
    /// The Shared library this person sees: their own household's. With no person (an application acting for the
    /// server) it is the server's own Shared library. Stores that only know one library keep answering with it.
    /// </summary>
    Task<Guid?> GetSharedLibraryIdAsync(Guid? profileId, CancellationToken ct = default) => GetSharedLibraryIdAsync(ct);
}
