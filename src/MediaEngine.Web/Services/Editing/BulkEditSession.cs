using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The staged changes of a list that has no detail page behind it (the songs list, a library table): the same pending list and the same
/// check-then-save as the editing bar uses on a detail page (<see cref="EditSessionSaver"/>), held on the list's own host. Cancel asks
/// "Unsaved changes" first when anything is staged; Save checks everything before it writes anything and keeps a change that failed in
/// the list with its reason. The list's filters, sort and scroll are not touched here: the host only refreshes its rows after a save.
/// </summary>
public sealed class BulkEditSession : IReviewSource
{
    private readonly EditSessionSaver _saver;
    private readonly Func<Task<bool>> _confirmDiscard;

    /// <param name="confirmDiscard">Asks "Unsaved changes"; true means discard them.</param>
    public BulkEditSession(PendingEditList pending, EditSessionSaver saver, Func<Task<bool>> confirmDiscard)
    {
        Pending = pending;
        _saver = saver;
        _confirmDiscard = confirmDiscard;
        Pending.Changed += RaiseChanged;
    }

    public PendingEditList Pending { get; }

    public bool Saving { get; private set; }

    public bool HasPendingChanges => !Pending.IsEmpty;

    /// <summary>The changes the last save did not accept, with the Engine's reasons. Empty when nothing failed.</summary>
    public IReadOnlyList<EditSessionFailure> Failures { get; private set; } = [];

    /// <summary>A plain sentence when the last save could not even be tried (Engine unreachable).</summary>
    public string? Message { get; private set; }

    /// <summary>The Engine's answer for every change in the last save (Review changes shows one status per row).</summary>
    public IReadOnlyList<EditSessionChangeResult> Results { get; private set; } = [];

    /// <summary>True when the last save wrote the data changes to the library.</summary>
    public bool DataSaved { get; private set; }

    /// <summary>The editor media types of the list's rows, which say which library's file setting applies (set by the host).</summary>
    public Func<IReadOnlyList<string>>? MediaTypes { get; set; }

    IReadOnlyList<string> IReviewSource.FileWriteMediaTypes => MediaTypes?.Invoke() ?? [];

    /// <summary>This save's own answer to "Save changes into the files" (null follows the library). Reset after a save or a discard.</summary>
    public bool? WriteIntoFiles { get; set; }

    /// <summary>Names the items for Review changes (set by the host from the rows it has ticked).</summary>
    public Func<Guid, string?>? ItemName { get; set; }

    /// <summary>Review's Edit on a row: a list has one panel for every change, so there is no tool to go back to.</summary>
    public Task JumpToAsync(EditReviewRow row) => Task.CompletedTask;

    /// <summary>The pending list as the rows Review changes shows.</summary>
    public IReadOnlyList<EditReviewRow> ReviewRows() => EditReviewRows.Build(Pending, id => ItemName?.Invoke(id), fields: null);

    /// <summary>Asks the Engine to check the pending list without saving anything (Review changes opens with this).</summary>
    public Task<EditSessionSaveOutcome> PreviewAsync(CancellationToken ct = default) => _saver.PreviewAsync(Pending, ct);

    /// <summary>Takes one Review row's change back out of the pending list. A details row takes only its own field.</summary>
    public bool RemoveReviewRow(EditReviewRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.FieldKey is { } key ? Pending.ClearField(row.EntityId, key) : Pending.Remove(row.ChangeId);
    }

    /// <summary>Discard changes in Review: the same question and the same result as Cancel.</summary>
    public Task<bool> LeaveAsync() => CancelAsync();

    /// <summary>Raised after a save that reached the library, so the host can refresh its rows.</summary>
    public Func<Task>? Saved { get; set; }

    public event Action? Changed;

    /// <summary>Stops listening to the pending list (the host is going away).</summary>
    public void Detach() => Pending.Changed -= RaiseChanged;

    /// <summary>Cancel: throws the staged changes away, asking "Unsaved changes" first. False means the person kept editing.</summary>
    public async Task<bool> CancelAsync()
    {
        if (Saving)
        {
            return false;
        }

        if (HasPendingChanges && !await _confirmDiscard())
        {
            return false;
        }

        Pending.Discard();
        Failures = [];
        Message = null;
        Results = [];
        DataSaved = false;
        WriteIntoFiles = null;
        RaiseChanged();
        return true;
    }

    /// <summary>Save changes: checks every staged change, saves them, and keeps any that failed with its reason. True only when everything was saved.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Saving || !HasPendingChanges)
        {
            return false;
        }

        Saving = true;
        Failures = [];
        Message = null;
        Results = [];
        DataSaved = false;
        RaiseChanged();
        EditSessionSaveOutcome outcome;
        try
        {
            outcome = await _saver.SaveAsync(Pending, WriteIntoFiles);
        }
        finally
        {
            Saving = false;
        }

        Failures = outcome.Failures;
        Message = outcome.Message;
        Results = outcome.Results ?? [];
        DataSaved = outcome.Saved;
        if (!outcome.Saved)
        {
            RaiseChanged();
            return false;
        }

        if (Saved is { } refresh)
        {
            await refresh();
        }

        foreach (var id in outcome.AppliedIds ?? [])
        {
            Pending.Remove(id);
        }

        if (Pending.IsEmpty)
        {
            WriteIntoFiles = null;
        }

        RaiseChanged();
        return outcome.Failures.Count == 0;
    }

    /// <summary>A short line for one failed change: the item and why it did not go through.</summary>
    public string Describe(EditSessionFailure failure, Func<Guid, string?> titleOf)
    {
        var item = titleOf(failure.EntityId);
        return string.IsNullOrWhiteSpace(item) ? failure.Reason : $"{item}: {failure.Reason}";
    }

    private void RaiseChanged() => Changed?.Invoke();
}
