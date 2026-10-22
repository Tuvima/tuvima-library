using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Display;

/// <summary>
/// The libraries the signed-in profile may browse, one per browse tab (Library setup spec D54). The profile's own library grants decide
/// (an administrator holds every library), the same rule the catalogue read applies to works; the Vault and the View area are never listed.
/// </summary>
public sealed class BrowseLibraryListService(
    IDisplayProjectionReadService display,
    IConfigurationLoader configuration,
    IVaultLibraryRepository vault,
    ILibraryKindSource? kindSource = null)
{
    // The areas browse tabs live in, in tab order. View and Vault are their own places and have no browse tab here.
    private static readonly string[] Areas = [LibraryAreas.Watch, LibraryAreas.Listen, LibraryAreas.Read];

    public async Task<IReadOnlyList<BrowseLibraryDto>> ListAsync(CancellationToken ct)
    {
        // Scope first: a caller who may browse nothing never causes the library list to be read.
        var scope = await display.LoadLibraryScopeAsync(ct).ConfigureAwait(false);
        if (!scope.AllLibraries && scope.Libraries.Count == 0)
        {
            return [];
        }

        var vaultIds = await vault.GetIdsAsync(ct).ConfigureAwait(false);
        var kinds = (kindSource?.GetKinds() ?? configuration.LoadLibraryKinds().Kinds)
            .GroupBy(kind => kind.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return configuration.LoadLibraries().Libraries
            .Where(library => library.Kind == LibraryKinds.Catalogued
                && Array.IndexOf(Areas, library.Area) >= 0
                && !vaultIds.Contains(library.Id)
                && Guid.TryParse(library.Id, out var id)
                && (scope.AllLibraries || scope.Libraries.Contains(id)))
            .Select(library =>
            {
                kinds.TryGetValue(library.KindKey, out var kind);
                return (Library: library, Kind: kind);
            })
            .OrderBy(entry => Array.IndexOf(Areas, entry.Library.Area))
            .ThenBy(entry => entry.Kind?.Order ?? int.MaxValue)
            .ThenBy(entry => entry.Library.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new BrowseLibraryDto(
                Guid.Parse(entry.Library.Id),
                entry.Library.Name,
                entry.Library.KindKey,
                entry.Library.Area,
                entry.Kind?.Format ?? entry.Library.MediaTypes.FirstOrDefault() ?? string.Empty,
                entry.Library.ScannableSources.Any()))
            .ToList();
    }
}
