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
    /// True when the file move this placement brings cannot be staged because the item's file already moves with a series change (universal
    /// editor v6): the two would disagree about where it ends up, so the person is asked to save one first. Nothing is dropped silently.
    /// </summary>
    public static bool SeriesMoveInTheWay(PendingEditList pending, Guid entityId, EditorMatchPlacementDto? placement) =>
        placement is { Managed: true } && !string.IsNullOrWhiteSpace(placement.FromPath) && !string.IsNullOrWhiteSpace(placement.ToPath)
        && pending.FindVideoSeriesMove(entityId) is not null;

    /// <summary>
    /// Replaces the item's staged move with the one <paramref name="placement"/> describes: a Relationship change to the destination, and a
    /// FileMove only in a Managed folder (a read-only folder keeps its files and the match still applies). A null placement stages nothing.
    /// Returns false, and stages nothing, when the file already moves with a series change (see <see cref="SeriesMoveInTheWay"/>).
    /// </summary>
    public static bool StageMove(PendingEditList pending, Guid entityId, EditorMatchPlacementDto? placement)
    {
        if (SeriesMoveInTheWay(pending, entityId, placement))
        {
            return false;
        }

        Unstage(pending, EditSessionChangeKinds.Relationship, entityId);
        Unstage(pending, EditSessionChangeKinds.FileMove, entityId);
        if (placement is null)
        {
            return true;
        }

        if (placement.DestinationId is { } destination && !string.IsNullOrWhiteSpace(placement.ParentLabel))
        {
            // A song also takes the chosen track's number in its new album; the move and the number travel as one change.
            pending.SetTarget(
                EditSessionChangeKinds.Relationship,
                entityId,
                destination.ToString("D"),
                token: placement.Position is { } position ? EditSessionPosition.PlaceToken(position) : null,
                label: placement.ParentLabel,
                before: string.IsNullOrWhiteSpace(placement.CurrentParentLabel) ? EditReviewRows.AsItIsNow : placement.CurrentParentLabel);
        }

        if (placement.Managed && !string.IsNullOrWhiteSpace(placement.FromPath) && !string.IsNullOrWhiteSpace(placement.ToPath))
        {
            pending.SetTarget(EditSessionChangeKinds.FileMove, entityId, null, label: placement.ToPath, before: placement.FromPath);
        }

        return true;
    }

    /// <summary>Removes the item's staged change of this kind, if there is one. The file move that goes with a video's series change is not a match's, and stays.</summary>
    public static void Unstage(PendingEditList pending, string kind, Guid entityId)
    {
        var existing = pending.Changes.FirstOrDefault(change =>
            change.Kind == kind && change.EntityId == entityId
            && !(kind == EditSessionChangeKinds.FileMove && EditSessionVideo.IsMovesWithSeries(change.Token)));
        if (existing is not null)
        {
            pending.Remove(existing.Id);
        }
    }
}
