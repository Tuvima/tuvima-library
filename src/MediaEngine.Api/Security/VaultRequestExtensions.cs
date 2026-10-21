using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>Small helpers for routes outside the Vault area that must still answer differently once Vault mode is on.</summary>
public static class VaultRequestExtensions
{
    /// <summary>True only when this request is a person's own Dashboard session with Vault mode switched on (Videos spec D17, D36).</summary>
    public static async ValueTask<bool> IsVaultUnlockedAsync(this HttpContext http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var services = http.RequestServices;
        var authority = await services.GetRequiredService<IRequestAuthorityResolver>().ResolveAsync(http, ct).ConfigureAwait(false);
        return await services.GetRequiredService<VaultAccessResolver>().IsUnlockedAsync(authority, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The Vault's source folders this request must not see: all of them while Vault mode is off, none while it is on. A folder picker or
    /// folder check treats these folders, and everything under them, as folders that do not exist.
    /// </summary>
    public static async ValueTask<IReadOnlyCollection<string>> HiddenVaultFolderRootsAsync(this HttpContext http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var services = http.RequestServices;
        var ids = await services.GetRequiredService<IVaultLibraryRepository>().GetIdsAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0 || await http.IsVaultUnlockedAsync(ct).ConfigureAwait(false))
        {
            return [];
        }

        return services.GetRequiredService<IConfigurationLoader>().LoadLibraries().Libraries
            .Where(library => ids.Contains(library.Id))
            .SelectMany(library => library.Sources)
            .Select(source => source.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
    }
}
