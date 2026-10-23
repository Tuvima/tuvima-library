using MediaEngine.Api.Security;
using MediaEngine.Application.Services;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.BatchEdit;

/// <summary>
/// Decides, item by item, which of a batch's selected items the caller may not edit. The route already requires an effective
/// administrator; this adds the same resource check a single edit gets (the libraries hidden from the caller, their content limit, and
/// the "still being ingested" guard), so an item the caller cannot edit is refused by name instead of being skipped quietly.
/// </summary>
internal sealed class LibraryBatchEditAccessService(
    CatalogueResourceAuthorizationService resources,
    ILibraryCurationReadService curationReadService,
    IDatabaseConnection database)
{
    /// <summary>The selected items the caller may not edit, each with the reason shown to the person.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> FindRefusalsAsync(
        HttpContext http,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken ct = default)
    {
        var refusals = new Dictionary<Guid, string>();
        var notFound = new List<Guid>();
        foreach (var id in entityIds.Where(id => id != Guid.Empty).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var access = await resources.EvaluateAnyEntityAsync(
                http, id, ApplicationPermissionIds.MetadataWrite, ct).ConfigureAwait(false);
            if (access == CatalogueResourceAccess.Denied)
            {
                refusals[id] = LibraryBatchEditReasons.NotAllowed;
            }
            else if (access == CatalogueResourceAccess.NotFound)
            {
                notFound.Add(id);
            }
            else if (await IngestionAvailability.IsUpdatingAsync(database, id, ct).ConfigureAwait(false))
            {
                refusals[id] = LibraryBatchEditReasons.Updating;
            }
        }

        if (notFound.Count > 0)
        {
            var notTitles = await curationReadService.FindNonTitleIdsAsync(notFound, ct).ConfigureAwait(false);
            foreach (var id in notFound)
            {
                refusals[id] = notTitles.Contains(id) ? LibraryBatchEditReasons.NotATitle : LibraryBatchEditReasons.Gone;
            }
        }

        return refusals;
    }
}
