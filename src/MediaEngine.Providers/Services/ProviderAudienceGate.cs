using MediaEngine.Domain.Contracts;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Decides which providers may be asked about which items (Videos spec D21, D26). An item is Vault because its file sits in
/// a Vault library (<see cref="IVaultLibraryRepository"/>); nothing about the item can change that. Adult-only providers are
/// asked about Vault items and nothing else; every other provider, YouTube included, is never asked about a Vault item by this
/// gate. (Retail, Wikidata and plugin lookups for a Vault section follow that section's own "Look up details online" switch, which
/// <c>LibraryKindLookupPolicy</c> and <c>IVaultSectionOnlineLookup</c> read; YouTube stays blocked whatever that switch says.)
/// An unknown provider is never allowed.
/// </summary>
public sealed class ProviderAudienceGate : IProviderAudienceGate
{
    private readonly IConfigurationLoader _configuration;
    private readonly IVaultLibraryRepository _vault;

    public ProviderAudienceGate(IConfigurationLoader configuration, IVaultLibraryRepository vault)
    {
        _configuration = configuration;
        _vault = vault;
    }

    public async Task<bool> MayAskAboutItemAsync(string providerName, Guid assetOrWorkId, CancellationToken ct = default)
    {
        var adultOnly = AdultOnlyOrNull(providerName);
        if (adultOnly is null)
        {
            return false;
        }

        var inVault = await _vault.IsInVaultAsync(assetOrWorkId, ct).ConfigureAwait(false);
        return adultOnly.Value == inVault;
    }

    public async Task<bool> MayAskAboutLibraryAsync(string providerName, string? libraryId, CancellationToken ct = default)
    {
        var adultOnly = AdultOnlyOrNull(providerName);
        if (adultOnly is null)
        {
            return false;
        }

        var ids = await _vault.GetIdsAsync(ct).ConfigureAwait(false);
        var inVault = !string.IsNullOrWhiteSpace(libraryId) && ids.Contains(libraryId);
        return adultOnly.Value == inVault;
    }

    private bool? AdultOnlyOrNull(string providerName)
    {
        var provider = string.IsNullOrWhiteSpace(providerName) ? null : _configuration.LoadProvider(providerName);
        return provider is null ? null : provider.AdultOnly;
    }
}
