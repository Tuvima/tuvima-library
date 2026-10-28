using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves an artwork choice the way the artwork workspace does: the picture named by <see cref="EditSessionChange.TargetId"/> becomes the
/// chosen one for the slot through <see cref="ArtworkAssetService.LinkAsync"/> (the existing "preferred" write), and
/// <see cref="EditSessionArtworkTargets.None"/> leaves no picture chosen. Pictures the person uploaded, pasted or took from a provider were
/// stored earlier as unchosen pictures of that item (linked to it, not chosen), so the page does not change until this runs. A picture
/// that is not one of the item's own is refused, so a saved change can never take another item's picture. Undo puts the previously
/// chosen picture back.
/// </summary>
public sealed class ArtworkEditSessionChangeHandler(
    ArtworkAssetService artwork,
    IItemCanonicalRepository itemCanonicalRepository,
    ISystemActivityRepository activityRepository,
    EditSubjectDirectory? subjects = null) : IEditSessionChangeHandler
{
    public const string UnknownSlot = "That kind of artwork cannot be changed here.";
    public const string NoTargetPicture = "No picture was chosen.";
    public const string PictureGone = "That picture is no longer available.";
    public const string NotThisItemsPicture = "That picture belongs to a different item. Add it to this item first.";

    public string Kind => EditSessionChangeKinds.Artwork;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (ArtworkAssetService.RoleForSlot(change.Slot) is null)
        {
            return UnknownSlot;
        }

        if (string.IsNullOrWhiteSpace(change.TargetId))
        {
            return NoTargetPicture;
        }

        if (await ResolveEntityTypeAsync(change.EntityId, ct).ConfigureAwait(false) is null)
        {
            return EditSessionReasons.Gone;
        }

        if (IsNone(change.TargetId))
        {
            return null;
        }

        if (!Guid.TryParse(change.TargetId, out var assetId) || !await artwork.AssetExistsAsync(assetId, ct).ConfigureAwait(false))
        {
            return PictureGone;
        }

        // Only this item's own pictures can be chosen for it: the ones already saved for it, its unchosen ones, and uploads, pasted
        // links and provider pictures added for it in the editor (all of which are linked to it, unchosen, when they are stored).
        return await artwork.AssetBelongsToEntityAsync(assetId, change.EntityId, ct).ConfigureAwait(false) ? null : NotThisItemsPicture;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var role = ArtworkAssetService.RoleForSlot(change.Slot) ?? throw new InvalidOperationException(UnknownSlot);
        var entityType = await ResolveEntityTypeAsync(change.EntityId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(EditSessionReasons.Gone);
        var previous = await artwork.GetChosenAsync(entityType, change.EntityId, role, ct).ConfigureAwait(false);

        if (IsNone(change.TargetId))
        {
            await artwork.ClearChosenAsync(entityType, change.EntityId, role, ct).ConfigureAwait(false);
        }
        else
        {
            if (!Guid.TryParse(change.TargetId, out var assetId) || !await artwork.AssetExistsAsync(assetId, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(PictureGone);
            }

            if (!await artwork.AssetBelongsToEntityAsync(assetId, change.EntityId, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(NotThisItemsPicture);
            }

            var context = await artwork.GetLinkContextAsync(entityType, change.EntityId, role, assetId, ct).ConfigureAwait(false);
            await artwork.LinkAsync(
                entityType,
                change.EntityId,
                new ArtworkLinkRequest(assetId, role, context, Preferred: true, SourceAssetType: change.Slot?.Trim()),
                ct).ConfigureAwait(false);
        }

        await activityRepository.LogAsync(new SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.CoverArtSaved,
            EntityId = change.EntityId,
            EntityType = entityType,
            Detail = IsNone(change.TargetId)
                ? $"Edit session removed the chosen {change.Slot} artwork."
                : $"Edit session chose new {change.Slot} artwork.",
        }, ct).ConfigureAwait(false);

        return async undoCt =>
        {
            if (previous is null)
            {
                await artwork.ClearChosenAsync(entityType, change.EntityId, role, undoCt).ConfigureAwait(false);
                return;
            }

            await artwork.LinkAsync(
                entityType,
                change.EntityId,
                new ArtworkLinkRequest(previous.ArtworkAssetId, role, previous.Context, Preferred: true, SourceAssetType: previous.SourceAssetType),
                undoCt).ConfigureAwait(false);
        };
    }

    private static bool IsNone(string? targetId) =>
        string.Equals(targetId?.Trim(), EditSessionArtworkTargets.None, StringComparison.OrdinalIgnoreCase);

    // The edit session names an item by id only: an item that already has stored pictures says what kind it is; a library item with
    // none yet is a work (the kind the editor's Details and Artwork tools open on); a person, Series, curated collection or Universe
    // with none yet says what it is through its own kind (universal editor spec D17, D17a).
    private async Task<string?> ResolveEntityTypeAsync(Guid entityId, CancellationToken ct)
    {
        var linked = await artwork.FindLinkedEntityTypeAsync(entityId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(linked))
        {
            return linked;
        }

        var state = await itemCanonicalRepository.LoadDisplayOverridesAsync(entityId, ct).ConfigureAwait(false);
        if (state.WorkExists)
        {
            return "Work";
        }

        return subjects is null ? null : (await subjects.FindAsync(entityId, ct).ConfigureAwait(false))?.ArtworkEntityType;
    }
}
