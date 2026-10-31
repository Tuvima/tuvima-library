using MediaEngine.Contracts.Editing;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// <see cref="IEditSessionService"/> over the kind handlers. Order matters and is the whole point of the class:
/// <list type="number">
/// <item>Every change is checked first. If any check fails nothing is written and every other change says why it was not saved.</item>
/// <item>The data changes (details, identity, relationships, artwork) are written one after another, with identity last because it
/// cannot be undone (a move between Extras and episodes cannot be undone either, so it goes with it), and if one fails the ones already written are undone in reverse order, so the library ends up with all of them or none of them.</item>
/// <item>File moves run last, one at a time. A move on disk cannot be undone like a data change, so a failed move leaves the data
/// saved and is reported by name.</item>
/// </list>
/// Once writing starts the loop does not watch the request's cancellation token: a caller that gives up mid-save must not leave
/// the library half changed.
/// </summary>
public sealed class EditSessionService(
    IEnumerable<IEditSessionChangeHandler> handlers,
    ILogger<EditSessionService> logger,
    IEditSessionFileWrites? fileWrites = null) : IEditSessionService
{
    private readonly Dictionary<string, IEditSessionChangeHandler> _handlers =
        handlers.ToDictionary(handler => handler.Kind, StringComparer.Ordinal);

    public async Task<EditSessionResult> RunAsync(
        EditSessionRequest request,
        IReadOnlyDictionary<Guid, string> refusals,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(refusals);

        var checks = await CheckAllAsync(request.Changes, refusals, ct).ConfigureAwait(false);
        var valid = checks.All(check => check.Reason is null);
        if (request.PreviewOnly || !valid)
        {
            return new EditSessionResult
            {
                PreviewOnly = request.PreviewOnly,
                Valid = valid,
                Changes = checks
                    .Select(check => Result(
                        check.Change,
                        check.Reason is not null ? EditSessionChangeStatuses.Invalid
                            : valid || request.PreviewOnly ? EditSessionChangeStatuses.Ok : EditSessionChangeStatuses.NotApplied,
                        check.Reason ?? (valid || request.PreviewOnly ? null : EditSessionReasons.NotRunBecauseCheckFailed)))
                    .ToList(),
            };
        }

        var applied = await ApplyAsync(request.Changes, ct).ConfigureAwait(false);
        await QueueFilesAsync(request, applied).ConfigureAwait(false);
        return applied;
    }

    // The library setting "Save changes into the files" (spec D15) decides in the write list whether a file is really written. This is the
    // one place a session asks: after the data changes are in the library, and never when this save said not to update the files.
    private async Task QueueFilesAsync(EditSessionRequest request, EditSessionResult applied)
    {
        if (fileWrites is null || request.WriteIntoFiles == false || !applied.DataSaved)
        {
            return;
        }

        var saved = applied.Changes.Where(result => result.Status == EditSessionChangeStatuses.Ok).Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
        var entities = request.Changes
            .Where(change => saved.Contains(change.Id) && EditSessionFileWriteRules.ChangesFileContent(change))
            .Select(change => change.EntityId)
            .Distinct()
            .ToList();
        if (entities.Count > 0)
        {
            await fileWrites.QueueAsync(entities, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<List<(EditSessionChange Change, string? Reason)>> CheckAllAsync(
        IReadOnlyList<EditSessionChange> changes,
        IReadOnlyDictionary<Guid, string> refusals,
        CancellationToken ct)
    {
        var results = new List<(EditSessionChange, string?)>(changes.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            results.Add((change, await CheckOneAsync(change, ids, refusals, ct).ConfigureAwait(false)));
        }

        return results;
    }

    private async Task<string?> CheckOneAsync(
        EditSessionChange change,
        HashSet<string> ids,
        IReadOnlyDictionary<Guid, string> refusals,
        CancellationToken ct)
    {
        if (!ids.Add(change.Id))
        {
            return EditSessionReasons.DuplicateId;
        }

        if (!EditSessionChangeKinds.IsKnown(change.Kind))
        {
            return EditSessionReasons.UnknownKind;
        }

        if (change.EntityId == Guid.Empty)
        {
            return EditSessionReasons.NoTarget;
        }

        if (refusals.TryGetValue(change.EntityId, out var refusal))
        {
            return refusal;
        }

        if (!_handlers.TryGetValue(change.Kind, out var handler))
        {
            return EditSessionReasons.NotAvailableYet;
        }

        try
        {
            return await handler.CheckAsync(change, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Edit session check failed for a change on item {EntityId}.", change.EntityId);
            return EditSessionReasons.SaveFailed;
        }
    }

    private async Task<EditSessionResult> ApplyAsync(IReadOnlyList<EditSessionChange> changes, CancellationToken ct)
    {
        // Changes that can be put back go first and an identity change goes last: it cannot be undone (see the identity handler), so by
        // the time it runs everything before it is already saved, and if it fails those are undone. OrderBy is stable, so the
        // Dashboard's order is kept within each kind.
        var dataChanges = changes
            .Where(change => EditSessionChangeKinds.IsDataChange(change.Kind))
            .OrderBy(change => change.Kind is EditSessionChangeKinds.Identity or EditSessionChangeKinds.ExtraMove ? 1 : 0)
            .ToList();
        var moves = changes.Where(change => change.Kind == EditSessionChangeKinds.FileMove).ToList();
        var outcomes = new Dictionary<string, EditSessionChangeResult>(StringComparer.Ordinal);
        var undos = new Stack<(EditSessionChange Change, Func<CancellationToken, Task> Undo)>();

        var dataSaved = true;
        foreach (var change in dataChanges)
        {
            try
            {
                var undo = await _handlers[change.Kind].ApplyAsync(change, CancellationToken.None).ConfigureAwait(false);
                outcomes[change.Id] = Result(change, EditSessionChangeStatuses.Ok, null);
                if (undo is not null)
                {
                    undos.Push((change, undo));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Edit session could not save a change on item {EntityId}; undoing the rest of the unit.", change.EntityId);
                outcomes[change.Id] = Result(change, EditSessionChangeStatuses.Failed, ReasonFor(ex));
                dataSaved = false;
                break;
            }
        }

        if (!dataSaved)
        {
            await UndoAsync(undos, outcomes).ConfigureAwait(false);
            foreach (var change in changes.Where(change => !outcomes.ContainsKey(change.Id)))
            {
                outcomes[change.Id] = Result(
                    change,
                    EditSessionChangeStatuses.NotApplied,
                    change.Kind == EditSessionChangeKinds.FileMove
                        ? EditSessionReasons.NotRunBecauseDataNotSaved
                        : EditSessionReasons.UndoneBecauseAnotherFailed);
            }
        }
        else
        {
            foreach (var move in moves)
            {
                try
                {
                    await _handlers[move.Kind].ApplyAsync(move, CancellationToken.None).ConfigureAwait(false);
                    outcomes[move.Id] = Result(move, EditSessionChangeStatuses.Ok, null);
                }
                catch (InvalidOperationException ex)
                {
                    // The physical-files handler's reason names the files that did not move. That sentence goes back to the person in
                    // Review; the Engine log keeps ids only, so the exception (and its message) is not written there.
                    logger.LogWarning("Edit session could not move some files of item {EntityId}; the data changes stay saved.", move.EntityId);
                    outcomes[move.Id] = Result(move, EditSessionChangeStatuses.Failed, ReasonFor(ex));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Edit session could not move files for item {EntityId}; the data changes stay saved.", move.EntityId);
                    outcomes[move.Id] = Result(move, EditSessionChangeStatuses.Failed, ReasonFor(ex));
                }
            }
        }

        return new EditSessionResult
        {
            Valid = true,
            DataSaved = dataSaved,
            Changes = changes.Select(change => outcomes[change.Id]).ToList(),
        };
    }

    private async Task UndoAsync(
        Stack<(EditSessionChange Change, Func<CancellationToken, Task> Undo)> undos,
        Dictionary<string, EditSessionChangeResult> outcomes)
    {
        while (undos.TryPop(out var entry))
        {
            try
            {
                await entry.Undo(CancellationToken.None).ConfigureAwait(false);
                outcomes[entry.Change.Id] = Result(entry.Change, EditSessionChangeStatuses.NotApplied, EditSessionReasons.UndoneBecauseAnotherFailed);
            }
            catch (Exception ex)
            {
                // The undo itself failed, so this change stays saved. Say so rather than claim it was put back.
                logger.LogError(ex, "Edit session could not undo a change on item {EntityId}.", entry.Change.EntityId);
                outcomes[entry.Change.Id] = Result(entry.Change, EditSessionChangeStatuses.Failed, EditSessionReasons.SaveFailed);
            }
        }
    }

    // Handlers throw plain-English messages for the failures a person can act on (InvalidOperationException); anything else is generic.
    private static string ReasonFor(Exception ex) =>
        ex is InvalidOperationException && !string.IsNullOrWhiteSpace(ex.Message) ? ex.Message : EditSessionReasons.SaveFailed;

    private static EditSessionChangeResult Result(EditSessionChange change, string status, string? reason) => new()
    {
        Id = change.Id,
        Kind = change.Kind,
        EntityId = change.EntityId,
        Status = status,
        Reason = reason,
    };
}
