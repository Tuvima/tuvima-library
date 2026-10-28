using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves a parent change chosen in the Match tool (kind <c>relationship</c>): which album, season or series the item belongs to.
/// <see cref="EditSessionChange.TargetId"/> is the place to move it into. It runs the library move the Details overlay uses
/// (<see cref="ILibraryMoveService"/>: preview, then commit) and asks for no file to be renamed or moved, because files move only
/// through their own physical-files change (spec D5). Each entry is taken the way the move's Match step pre-selects it, or added
/// anyway when nothing fits; the Match tool does not number tracks or episodes by hand.
/// <para>
/// Undo moves the item back to the place it came from, the same way, so a later failure in the unit does not leave it half moved.
/// </para>
/// </summary>
public sealed class RelationshipEditSessionChangeHandler(
    ILibraryMoveService moves,
    ILibraryMoveRepository moveItems,
    EditSessionCallerAccessor callers) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.Relationship;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (callers.Current is not { } caller)
        {
            return EditSessionReasons.SessionUnavailable;
        }

        if (!TryDestination(change, out var destination))
        {
            return EditSessionReasons.NoDestination;
        }

        var preview = await moves.PreviewAsync(PreviewRequest(change.EntityId, destination), caller.Actor, caller.CanEdit, ct).ConfigureAwait(false);
        if (!preview.IsSuccess)
        {
            return preview.Refusal!.Message;
        }

        var row = preview.Value!.Rows.FirstOrDefault(r => r.ItemId == change.EntityId);
        return row is null ? EditSessionReasons.Gone : row.BlockedReason;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var caller = callers.Current ?? throw new InvalidOperationException(EditSessionReasons.SessionUnavailable);
        if (!TryDestination(change, out var destination))
        {
            throw new InvalidOperationException(EditSessionReasons.NoDestination);
        }

        var before = (await moveItems.GetItemsAsync([change.EntityId], ct).ConfigureAwait(false)).FirstOrDefault()
            ?? throw new InvalidOperationException(EditSessionReasons.Gone);
        var previousPlace = before.IsEdition ? before.WorkId : before.ParentWorkId;

        await MoveAsync(change.EntityId, destination, caller, ct).ConfigureAwait(false);

        return previousPlace is { } back && back != Guid.Empty && back != destination
            ? undoCt => MoveAsync(change.EntityId, back, caller, undoCt)
            : null;
    }

    private async Task MoveAsync(Guid itemId, Guid destination, EditSessionCaller caller, CancellationToken ct)
    {
        var preview = await moves.PreviewAsync(PreviewRequest(itemId, destination), caller.Actor, caller.CanEdit, ct).ConfigureAwait(false);
        if (!preview.IsSuccess)
        {
            throw new InvalidOperationException(preview.Refusal!.Message);
        }

        var review = preview.Value!;
        var row = review.Rows.FirstOrDefault(r => r.ItemId == itemId) ?? throw new InvalidOperationException(EditSessionReasons.Gone);
        if (!string.IsNullOrWhiteSpace(row.BlockedReason))
        {
            throw new InvalidOperationException(row.BlockedReason);
        }

        var hasList = review.ListKind != LibraryMoveListKinds.None;
        var take = hasList && !string.IsNullOrEmpty(row.SuggestedEntryKey) && !review.AddAnywayOnly;
        var commit = new LibraryMoveCommitRequest
        {
            PreviewToken = review.PreviewToken,
            Rows =
            [
                new LibraryMoveCommitRow
                {
                    ItemId = itemId,
                    Mode = review.AddAnywayOnly || (hasList && !take) ? LibraryMoveModes.AddAnyway : LibraryMoveModes.Match,
                    EntryKey = take ? row.SuggestedEntryKey : null,
                },
            ],
        };

        var result = await moves.CommitWithoutFileMovesAsync(commit, caller.Actor, caller.CanEdit, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Refusal!.Message);
        }
    }

    private static LibraryMovePreviewRequest PreviewRequest(Guid itemId, Guid destination) =>
        new() { ItemIds = [itemId], DestinationId = destination };

    private static bool TryDestination(EditSessionChange change, out Guid destination) =>
        Guid.TryParse(change.TargetId, out destination) && destination != Guid.Empty;
}
