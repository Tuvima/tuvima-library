using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Display;

/// <summary>
/// The libraries the signed-in profile may browse, one per browse tab (Library setup spec D54). The catalogue read decides, with the same
/// rule it applies to works: hidden libraries (a library hidden from the profile is never listed), the lane's feature (Watch, Listen or Read) and the
/// Vault. A content limit does not hide a library: its items are filtered one by one (a Kid's profile follows the household's level for the area). The View area is never listed.
/// </summary>
public sealed class BrowseLibraryListService(
    IDisplayProjectionReadService display,
    IConfigurationLoader configuration,
    IVaultLibraryRepository vault,
    ILibraryKindSource? kindSource = null)
{
    // The areas browse tabs live in, in tab order. View and Vault are their own places and have no browse tab here.
    private static readonly string[] Areas = [LibraryAreas.Watch, LibraryAreas.Listen, LibraryAreas.Read];

    // The Folders filter exists only when the library has two or more folders (Libraries v3 D18): each folder by its friendly name, else its note, else its last path part.
    private static IReadOnlyList<BrowseFolderDto>? FoldersOf(LibraryFolderConfig library)
    {
        var sources = library.CataloguedSources.ToList();
        return sources.Count < 2
            ? null
            : [.. sources.Select(source => new BrowseFolderDto(source.Id, FolderName(source)))];
    }

    private static string FolderName(LibrarySourceConfig source)
    {
        if (!string.IsNullOrWhiteSpace(source.Label))
        {
            return source.Label.Trim();
        }

        if (!string.IsNullOrWhiteSpace(source.Notes))
        {
            return source.Notes.Trim();
        }

        var leaf = Path.GetFileName(source.Path.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(leaf) ? source.Path : leaf;
    }

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
                entry.Library.CataloguedSources.Any(),
                FoldersOf(entry.Library)))
            .ToList();
    }
}
