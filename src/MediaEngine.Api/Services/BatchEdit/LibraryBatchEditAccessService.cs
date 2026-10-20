using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.BatchEdit;

/// <summary>
/// Decides, item by item, which of a batch's selected items the caller may edit. The route already requires an effective
/// administrator; this adds the same resource check a single edit gets (the caller's library grants and content limit, and
/// the "still being ingested" guard), so an item the caller cannot edit is refused by name instead of being skipped quietly.
/// </summary>
internal sealed class LibraryBatchEditAccessService(
    CatalogueResourceAuthorizationService resources,
    IDatabaseConnection database)
{
    public const string NotAllowedReason = "You are not allowed to edit this item.";
    public const string GoneReason = "This item is no longer in your library.";
    public const string UpdatingReason = "This item is still being updated. Try again when it finishes.";

    /// <summary>The selected items the caller may not edit, each with the reason shown to the person.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> FindRefusalsAsync(
        HttpContext http,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken ct = default)
    {
        var refusals = new Dictionary<Guid, string>();
        foreach (var id in entityIds.Where(id => id != Guid.Empty).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var access = await resources.EvaluateAnyEntityAsync(
                http, id, ApplicationPermissionIds.MetadataWrite, ct).ConfigureAwait(false);
            if (access == CatalogueResourceAccess.Denied)
            {
                refusals[id] = NotAllowedReason;
            }
            else if (access == CatalogueResourceAccess.NotFound)
            {
                refusals[id] = GoneReason;
            }
            else if (await IngestionAvailability.IsUpdatingAsync(database, id, ct).ConfigureAwait(false))
            {
                refusals[id] = UpdatingReason;
            }
        }

        return refusals;
    }
}
