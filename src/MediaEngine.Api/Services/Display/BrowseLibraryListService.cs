using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Display;

/// <summary>
/// The libraries the signed-in profile may browse, one per browse tab (Library setup spec D54). The catalogue read decides, with the same
/// rule it applies to works: library grants (an administrator holds every library), the lane's feature (Watch, Listen or Read) and the
/// Vault. A content limit does not hide a library: its items are filtered one by one (a folder marked "Suitable for limited profiles" lets
/// its unrated items through). The View area is never listed.
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
        var access = await display.LoadLibraryAccessAsync(ct).ConfigureAwait(false);
        var vaultIds = await vault.GetIdsAsync(ct).ConfigureAwait(false);
        var kinds = (kindSource?.GetKinds() ?? configuration.LoadLibraryKinds().Kinds)
            .GroupBy(kind => kind.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return configuration.LoadLibraries().Libraries
            .Where(library => library.Kind == LibraryKinds.Catalogued
                && Array.IndexOf(Areas, library.Area) >= 0
                && !vaultIds.Contains(library.Id)
                && Guid.TryParse(library.Id, out _))
            .Select(library =>
            {
                kinds.TryGetValue(library.KindKey, out var kind);
                return (Library: library, Kind: kind, MediaType: kind?.Format ?? library.MediaTypes.FirstOrDefault() ?? string.Empty);
            })
            .Where(entry => access.Allows(entry.Library.Id, entry.MediaType))
            .OrderBy(entry => Array.IndexOf(Areas, entry.Library.Area))
            .ThenBy(entry => entry.Kind?.Order ?? int.MaxValue)
            .ThenBy(entry => entry.Library.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new BrowseLibraryDto(
                Guid.Parse(entry.Library.Id),
                entry.Library.Name,
                entry.Library.KindKey,
                entry.Library.Area,
                entry.MediaType,
                entry.Library.ScannableSources.Any()))
            .ToList();
    }
}
