using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One staged change the Engine did not accept, with the plain reason it gave.</summary>
public sealed record EditSessionFailure(string ChangeId, Guid EntityId, string Kind, string Reason);

/// <summary>
/// How a save ended. <see cref="Saved"/> is true when the data changes are in the library; <see cref="Failures"/> lists the changes that
/// did not go through (empty after a clean save), <see cref="AppliedIds"/> names the changes that are now in the library, and
/// <see cref="Message"/> is the plain sentence for a save that could not be tried at all.
/// </summary>
public sealed record EditSessionSaveOutcome(bool Saved, IReadOnlyList<EditSessionFailure> Failures, string? Message = null, IReadOnlyList<string>? AppliedIds = null)
{
    public static EditSessionSaveOutcome Success { get; } = new(true, []);

    public static EditSessionSaveOutcome Unsaved(string message) => new(false, [], message);
}

/// <summary>
/// Saves the pending list the way the universal editor requires (spec D13): check everything first, then apply only if every change
/// passed. Kept apart from any screen so the editing bar now and the Review changes dialog later share one save.
/// </summary>
public sealed class EditSessionSaver
{
    private const string NothingToSave = "There are no changes to save.";
    private const string Unreachable = "The Engine could not be reached. Nothing was saved.";

    private readonly IEngineApiClient _api;

    public EditSessionSaver(IEngineApiClient api)
    {
        _api = api;
    }

    /// <summary>Asks the Engine to check the pending list without saving anything.</summary>
    public async Task<EditSessionSaveOutcome> PreviewAsync(PendingEditList pending, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.IsEmpty)
        {
            return EditSessionSaveOutcome.Unsaved(NothingToSave);
        }

        var checkedList = await _api.RunEditSessionAsync(pending.ToRequest(previewOnly: true), ct);
        if (!checkedList.IsOk)
        {
            return EditSessionSaveOutcome.Unsaved(checkedList.Message ?? Unreachable);
        }

        var failures = FailuresOf(checkedList.Value!);
        return failures.Count == 0 && checkedList.Value!.Valid
            ? EditSessionSaveOutcome.Success
            : new EditSessionSaveOutcome(false, failures);
    }

    /// <summary>
    /// Checks, then saves. A change the check refuses stops everything and nothing is written. The pending list is left as it is: the
    /// caller removes <see cref="EditSessionSaveOutcome.AppliedIds"/> once the page shows the saved values.
    /// </summary>
    public async Task<EditSessionSaveOutcome> SaveAsync(PendingEditList pending, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var preview = await PreviewAsync(pending, ct);
        if (!preview.Saved)
        {
            return preview;
        }

        var saveCall = await _api.RunEditSessionAsync(pending.ToRequest(previewOnly: false), ct);
        if (!saveCall.IsOk)
        {
            return EditSessionSaveOutcome.Unsaved(saveCall.Message ?? Unreachable);
        }

        var result = saveCall.Value!;
        var failures = FailuresOf(result);
        if (!result.DataSaved)
        {
            return new EditSessionSaveOutcome(false, failures, failures.Count == 0 ? "Nothing was saved." : null);
        }

        var applied = result.Changes.Where(change => change.Status == EditSessionChangeStatuses.Ok).Select(change => change.Id).ToList();
        return new EditSessionSaveOutcome(true, failures, AppliedIds: applied);
    }

    // A change that was only held back because another one failed is not the cause, so it is left out when the cause is listed.
    private static List<EditSessionFailure> FailuresOf(EditSessionResult result)
    {
        var notOk = result.Changes.Where(change => change.Status != EditSessionChangeStatuses.Ok).ToList();
        var causes = notOk.Where(change => change.Status != EditSessionChangeStatuses.NotApplied).ToList();
        return (causes.Count > 0 ? causes : notOk)
            .Select(change => new EditSessionFailure(change.Id, change.EntityId, change.Kind, change.Reason ?? "This change could not be saved."))
            .ToList();
    }
}
