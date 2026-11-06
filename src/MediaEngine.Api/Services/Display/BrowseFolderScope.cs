using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Display;

/// <summary>
/// The Folders filter of a library's browse page (Libraries v3 D18): the works that have a file beneath one folder of the library asked
/// for. It only narrows what the read service already authorized, so a folder the profile cannot see is an empty result and nothing says
/// whether it exists. Null means no folder was asked for.
/// </summary>
public sealed class BrowseFolderScope(IConfigurationLoader configuration, ILibraryFolderWorkRepository works)
{
    public async Task<IReadOnlySet<Guid>?> WorksAsync(string? folderId, Guid? libraryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            return null;
        }

        var libraries = configuration.LoadLibraries().Libraries
            .Where(library => libraryId is null || string.Equals(library.Id, libraryId.Value.ToString("D"), StringComparison.OrdinalIgnoreCase));
        var source = libraries.SelectMany(library => library.CataloguedSources)
            .FirstOrDefault(item => string.Equals(item.Id, folderId.Trim(), StringComparison.OrdinalIgnoreCase));
        return source is null
            ? new HashSet<Guid>()
            : await works.ListWorkIdsInFolderAsync(source.Path, ct).ConfigureAwait(false);
    }

    /// <summary>The visible works narrowed to the folder, when one was asked for.</summary>
    public static IReadOnlySet<Guid> Narrow(IReadOnlySet<Guid> visible, IReadOnlySet<Guid>? inFolder) =>
        inFolder is null ? visible : visible.Where(inFolder.Contains).ToHashSet();
}
