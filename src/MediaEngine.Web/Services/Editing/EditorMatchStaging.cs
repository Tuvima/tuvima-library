using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Stages what a chosen match brings besides the match itself (universal editor v5, 3c): the move to the record's album or season, and the
/// file move that goes with it. One place for the Match tab's single Apply and the group Match, so the two can never stage differently.
/// Nothing is written here; Save changes writes the pending list.
/// </summary>
public static class EditorMatchStaging
{
    /// <summary>
    /// Replaces the item's staged move with the one <paramref name="placement"/> describes: a Relationship change to the destination, and a
    /// FileMove only in a Managed folder (a read-only folder keeps its files and the match still applies). A null placement stages nothing.
    /// </summary>
    public static void StageMove(PendingEditList pending, Guid entityId, EditorMatchPlacementDto? placement)
    {
        Unstage(pending, EditSessionChangeKinds.Relationship, entityId);
        Unstage(pending, EditSessionChangeKinds.FileMove, entityId);
        if (placement is null)
        {
            return;
        }

        if (placement.DestinationId is { } destination && !string.IsNullOrWhiteSpace(placement.ParentLabel))
        {
            pending.SetTarget(
                EditSessionChangeKinds.Relationship,
                entityId,
                destination.ToString("D"),
                label: placement.ParentLabel,
                before: string.IsNullOrWhiteSpace(placement.CurrentParentLabel) ? EditReviewRows.AsItIsNow : placement.CurrentParentLabel);
        }

        if (placement.Managed && !string.IsNullOrWhiteSpace(placement.FromPath) && !string.IsNullOrWhiteSpace(placement.ToPath))
        {
            pending.SetTarget(EditSessionChangeKinds.FileMove, entityId, null, label: placement.ToPath, before: placement.FromPath);
        }
    }

    /// <summary>Removes the item's staged change of this kind, if there is one.</summary>
    public static void Unstage(PendingEditList pending, string kind, Guid entityId)
    {
        var existing = pending.Changes.FirstOrDefault(change => change.Kind == kind && change.EntityId == entityId);
        if (existing is not null)
        {
            pending.Remove(existing.Id);
        }
    }
}
