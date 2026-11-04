using MediaEngine.Contracts.Editing;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// The editor's Quick Facts totals (universal editor v4, D4). The data store gives the counts, sizes and file paths; this adds the one
/// fact it cannot know, whether any of the item's files lives in a library folder marked read-only, from the configured folders.
/// </summary>
public sealed class ItemQuickFactsService(ItemQuickFactsRepository repository, ILibraryFolderResolver folders)
{
    public async Task<ItemQuickFactsDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var read = await repository.GetAsync(id, ct).ConfigureAwait(false);
        if (read is null)
        {
            return null;
        }

        read.Facts.ReadOnlyLibrary = read.FilePaths.Any(path => folders.ResolveSourceForPath(path) is { Source.IsWritable: false });
        return read.Facts;
    }
}
