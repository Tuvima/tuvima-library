using MediaEngine.Contracts.Items;

namespace MediaEngine.Api.Services.Libraries;

// Delete one file of an item (universal editor v4, D13): the same plan, safety gate, fingerprint and clean-up as a title delete, narrowed to one
// media file. An item with other files stays with them; the item's last file is the ordinary whole-item delete.
public sealed partial class LibraryItemDeletionService
{
    public async Task<LibraryItemDeletionResult<LibraryItemDeletionPreview>> PreviewFileAsync(
        Guid workId,
        Guid assetId,
        CancellationToken ct = default)
    {
        var planned = await BuildFilePlanAsync(workId, assetId, ct).ConfigureAwait(false);
        return planned.IsSuccess
            ? LibraryItemDeletionResult<LibraryItemDeletionPreview>.Ok(planned.Value!.Preview)
            : LibraryItemDeletionResult<LibraryItemDeletionPreview>.Refused(planned.Refusal!);
    }

    /// <summary>
    /// Deletes the one file the person confirmed. When the plan has changed since the preview, nothing is touched and the refusal
    /// carries the fresh preview.
    /// </summary>
    public async Task<LibraryItemDeletionResult<LibraryItemDeletionResultDto>> DeleteFileAsync(
        Guid workId,
        Guid assetId,
        string? fingerprint,
        LibraryItemDeletionActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await _deleteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var planned = await BuildFilePlanAsync(workId, assetId, ct).ConfigureAwait(false);
            if (!planned.IsSuccess)
            {
                return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Refused(planned.Refusal!);
            }

            var plan = planned.Value!;
            if (!string.Equals(fingerprint, plan.Preview.Fingerprint, StringComparison.Ordinal))
            {
                return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Refused(new LibraryItemDeletionRefusal(
                    LibraryItemDeletionRefusalKind.Changed, ChangedMessage, [], plan.Preview));
            }

            var result = await ExecuteAsync(plan, actor).ConfigureAwait(false);
            return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Ok(result);
        }
        finally
        {
            _deleteLock.Release();
        }
    }

    private Task<LibraryItemDeletionResult<LibraryItemDeletionPlan>> BuildFilePlanAsync(Guid workId, Guid assetId, CancellationToken ct) =>
        assetId == Guid.Empty
            ? Task.FromResult(Refused(LibraryItemDeletionRefusalKind.Validation, "Choose a file to delete.", []))
            : BuildPlanAsync([workId], ct, onlyAssetId: assetId);
}
