using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// Drops the progress kept for files in the Recycle bin once nothing is left to come back to (delete groups D21). The bin is a folder
/// of files with no link to the data store, so this works from what the bin still holds: with an empty bin every kept row goes, and
/// otherwise rows older than the longest retention (plus a day) go, because no file kept that long can still be in the bin.
/// </summary>
public sealed class RecycleBinKeptProgress(RecycleBinService bin, ILibraryItemDeletionRepository repository, IConfigurationLoader configuration)
{
    /// <summary>One sweep. Returns how many kept rows were dropped.</summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (bin.List().Count == 0)
        {
            return await repository.DiscardKeptProgressAsync(null, ct).ConfigureAwait(false);
        }

        var retention = RecycleBinSummaryBuilder.RetentionFrom(configuration.LoadLibraries());
        var days = configuration.LoadLibraries().Libraries.Where(library => library.RecycleBin).Select(library => retention(library.Id)).ToList();
        return days.Count > 0 && days.All(value => value > 0)
            ? await repository.DiscardKeptProgressAsync(now - TimeSpan.FromDays(days.Max() + 1), ct).ConfigureAwait(false)
            : 0;
    }
}
