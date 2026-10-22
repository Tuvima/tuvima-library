using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Vault;

/// <summary>
/// Where the Vault area and the Vault group in Search get their items (Videos spec D39). <see cref="EngineVaultCatalogue"/> asks the
/// Engine's Vault routes, which answer only a session with Vault mode on; <see cref="PendingVaultCatalogue"/> is the stand-in that
/// makes no request.
/// </summary>
public interface IVaultCatalogue
{
    /// <summary>The items of one Vault view (<see cref="VaultViews"/>), optionally narrowed by <paramref name="filter"/>.</summary>
    Task<VaultCatalogueResult> ListAsync(string viewKey, VaultViewPreferences preferences, VaultListFilter? filter = null, CancellationToken ct = default);

    /// <summary>The Vault search group: what is actually stored for Vault items matching the text (never mixed into normal results).</summary>
    Task<VaultCatalogueResult> SearchAsync(string query, CancellationToken ct = default);

    /// <summary>One scene's own page, or null when it is not there for this session (a locked session gets exactly what a missing scene gets).</summary>
    Task<VaultSceneDetail?> GetSceneAsync(Guid assetId, CancellationToken ct = default);
}

/// <summary>The stand-in that answers "not available" without making any request.</summary>
public sealed class PendingVaultCatalogue : IVaultCatalogue
{
    public Task<VaultCatalogueResult> ListAsync(string viewKey, VaultViewPreferences preferences, VaultListFilter? filter = null, CancellationToken ct = default) =>
        Task.FromResult(VaultCatalogueResult.NotYetAvailable);

    public Task<VaultCatalogueResult> SearchAsync(string query, CancellationToken ct = default) =>
        Task.FromResult(VaultCatalogueResult.NotYetAvailable);

    public Task<VaultSceneDetail?> GetSceneAsync(Guid assetId, CancellationToken ct = default) =>
        Task.FromResult<VaultSceneDetail?>(null);
}
