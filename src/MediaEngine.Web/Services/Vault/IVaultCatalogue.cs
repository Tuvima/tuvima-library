using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Vault;

/// <summary>
/// Where the Vault area and the Vault group in Search get their items. SEAM (Videos spec WP11/WP12/WP17): the Engine has no Vault
/// listing or search route yet (only NSFW mode, the Vault library records and the Vault artwork route), so
/// <see cref="PendingVaultCatalogue"/> answers "not yet available" without making any request. When the Engine gains those routes,
/// only the implementation changes; every screen already draws what this returns.
/// </summary>
public interface IVaultCatalogue
{
    /// <summary>The items of one Vault view (<see cref="VaultViews"/>).</summary>
    Task<VaultCatalogueResult> ListAsync(string viewKey, VaultViewPreferences preferences, CancellationToken ct = default);

    /// <summary>The Vault search group: what is actually stored for Vault items matching the text (never mixed into normal results).</summary>
    Task<VaultCatalogueResult> SearchAsync(string query, CancellationToken ct = default);
}

/// <summary>The stand-in until the Engine has Vault list and search routes. Makes no request.</summary>
public sealed class PendingVaultCatalogue : IVaultCatalogue
{
    public Task<VaultCatalogueResult> ListAsync(string viewKey, VaultViewPreferences preferences, CancellationToken ct = default) =>
        Task.FromResult(VaultCatalogueResult.NotYetAvailable);

    public Task<VaultCatalogueResult> SearchAsync(string query, CancellationToken ct = default) =>
        Task.FromResult(VaultCatalogueResult.NotYetAvailable);
}
