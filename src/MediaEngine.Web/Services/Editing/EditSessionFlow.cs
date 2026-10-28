using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Navigation;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What a person does with the universal editor on one detail page, kept off the screens so the bar, the Esc key and the overlay's
/// leave guard all follow the same rules: pick a tool, close the panel (edit mode and the pending list stay, spec D22), Cancel (asks
/// "Unsaved changes" first when anything is pending), and Save changes (check, then save, with the reason for every change that failed).
/// It follows <see cref="EditingModeContext"/>: turning that on starts the <see cref="EditSessionState"/>, turning it off ends it.
/// </summary>
public sealed class EditSessionFlow : IDetailsOverlayDismissGuard, IDisposable
{
    private readonly EditSessionState _session;
    private readonly EditingModeContext _mode;
    private readonly EditSessionSaver _saver;
    private readonly Func<IReadOnlyList<EditTrailItem>> _trail;
    private readonly Func<Task<bool>> _confirmDiscard;

    /// <param name="trail">Where the item being edited sits (the page's own item for now).</param>
    /// <param name="confirmDiscard">Asks "Unsaved changes"; true means discard them.</param>
    public EditSessionFlow(
        EditSessionState session,
        EditingModeContext mode,
        EditSessionSaver saver,
        Func<IReadOnlyList<EditTrailItem>> trail,
        Func<Task<bool>> confirmDiscard)
    {
        _session = session;
        _mode = mode;
        _saver = saver;
        _trail = trail;
        _confirmDiscard = confirmDiscard;
        _mode.UseStagedEditor = true;
        _mode.Changed += OnModeChanged;
    }

    public EditSessionState Session => _session;

    /// <summary>Raised after a save that reached the library, so the page can refresh before edit mode ends.</summary>
    public Func<Task>? Saved { get; set; }

    public bool Saving { get; private set; }

    /// <summary>The changes the last save did not accept, with the Engine's reasons. Empty when nothing failed.</summary>
    public IReadOnlyList<EditSessionFailure> Failures { get; private set; } = [];

    /// <summary>A plain sentence when the last save could not even be tried (Engine unreachable, nothing to save).</summary>
    public string? Message { get; private set; }

    public event Action? Changed;

    public void Dispose() => _mode.Changed -= OnModeChanged;

    /// <summary>Keeps the session in step with edit mode and keeps the open tool in step with the section the panel hosts.</summary>
    public void Sync()
    {
        if (_mode is { IsOn: true, CanEdit: true })
        {
            if (!_session.IsActive)
            {
                _session.Begin(_trail());
            }
        }
        else if (_session.IsActive)
        {
            _session.Cancel();
            Failures = [];
            Message = null;
        }

        if (!_session.IsActive)
        {
            return;
        }

        var tool = ToolFor(_mode.OpenSectionKind);
        if (tool is { } open && _session.ActiveTool != open)
        {
            _session.SetTool(open);
        }
        else if (tool is null && _mode.OpenSectionKind is null && _session.ActiveTool is EditTool.Artwork or EditTool.Match)
        {
            _session.SetTool(null);
        }
    }

    /// <summary>A tab in the bar: opens that tool, or closes the panel when it is the open one.</summary>
    public async Task SelectToolAsync(EditTool tool)
    {
        if (!_session.IsActive || !_mode.CanEdit || Saving)
        {
            return;
        }

        if (_session.ActiveTool == tool)
        {
            await ClosePanelAsync();
            return;
        }

        var opened = tool switch
        {
            // History is the block at the bottom of Details, so the Details tool hosts it.
            EditTool.Details => await _mode.ShowSectionAsync(EditingSection.History),
            EditTool.Artwork => _mode.Fields.CanEditArtwork && await _mode.ShowSectionAsync(EditingSection.Artwork),
            EditTool.Match => _mode.Fields.CanMatch && await _mode.ShowSectionAsync(EditingSection.Match),
            _ => false,
        };
        if (opened && _session.IsActive)
        {
            _session.SetTool(tool);
        }
    }

    /// <summary>The panel's close button: closes the panel only. Edit mode and the pending list stay; no question is asked.</summary>
    public async Task<bool> ClosePanelAsync()
    {
        if (!_session.IsActive)
        {
            return true;
        }

        if (_mode.SectionOpen && !await _mode.CloseSectionAsync())
        {
            return false;
        }

        if (_session.IsActive)
        {
            _session.SetTool(null);
        }

        return true;
    }

    /// <summary>Cancel: leaves edit mode and drops the pending changes, asking "Unsaved changes" first when there are any. False means the person stayed.</summary>
    public async Task<bool> LeaveAsync()
    {
        if (!_mode.IsOn)
        {
            return true;
        }

        if (Saving || _mode.IsSettling)
        {
            return false;
        }

        if (_session.HasPendingChanges || _mode.HasUnsavedInput)
        {
            if (!await _confirmDiscard())
            {
                return false;
            }

            if (!_mode.IsOn)
            {
                // Edit mode ended while the question was open (the title was removed): there is nothing left to discard.
                return true;
            }
        }

        _session.Cancel();
        Failures = [];
        Message = null;
        _mode.Leave();
        return true;
    }

    /// <summary>The overlay is closing (close button, Esc, backdrop, Back, a link to another title): leaving goes through <see cref="LeaveAsync"/>.</summary>
    public async ValueTask<bool> CanDismissAsync() => await LeaveAsync();

    /// <summary>Esc from outside a field: closes the open panel; with the panel closed and changes pending it asks "Unsaved changes". False means the press was not ours.</summary>
    public async ValueTask<bool> HandleEscapeAsync()
    {
        if (!_mode.IsOn || !_session.IsActive)
        {
            return false;
        }

        if (Saving)
        {
            return true;
        }

        if (_session.ActiveTool is not null)
        {
            await ClosePanelAsync();
            return true;
        }

        if (_session.HasPendingChanges)
        {
            await LeaveAsync();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Save changes: checks every pending change, saves them, and on success leaves edit mode. A change that fails stays in the list with
    /// its reason, so nothing is lost. Returns true only when everything was saved and edit mode ended.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (Saving || !_session.IsActive || !_session.HasPendingChanges)
        {
            return false;
        }

        // A section hosted in the panel (Artwork, Match, History) may hold input of its own: settle it first, as closing it would.
        if (_mode.SectionDirty && !await _mode.CloseSectionAsync())
        {
            return false;
        }

        Saving = true;
        Failures = [];
        Message = null;
        Changed?.Invoke();
        EditSessionSaveOutcome outcome;
        try
        {
            outcome = await _saver.SaveAsync(_session.Pending);
        }
        finally
        {
            Saving = false;
        }

        Failures = outcome.Failures;
        Message = outcome.Message;
        if (!outcome.Saved)
        {
            Changed?.Invoke();
            return false;
        }

        if (Saved is { } refresh)
        {
            await refresh();
        }

        foreach (var id in outcome.AppliedIds ?? [])
        {
            _session.Pending.Remove(id);
        }

        if (outcome.Failures.Count > 0 || !_session.IsActive)
        {
            Changed?.Invoke();
            return false;
        }

        _session.Pending.Discard();
        _session.End();
        _mode.Leave();
        Changed?.Invoke();
        return true;
    }

    /// <summary>A short line for one failed change: the item, the fields it touched, and why it did not go through.</summary>
    public string Describe(EditSessionFailure failure)
    {
        var item = _session.Trail.FirstOrDefault(step => step.EntityId == failure.EntityId)?.Title;
        var change = _session.Pending.Changes.FirstOrDefault(candidate => candidate.Id == failure.ChangeId);
        string what;
        if (change is { Kind: EditSessionChangeKinds.Details, Fields.Count: > 0 })
        {
            what = string.Join(", ", change.Fields.Select(field => _mode.Fields.Find(field.Key)?.Label ?? field.Key));
        }
        else
        {
            what = failure.Kind switch
            {
                EditSessionChangeKinds.Artwork => "Artwork",
                EditSessionChangeKinds.Identity => "Match",
                EditSessionChangeKinds.Relationship => "Relationship",
                EditSessionChangeKinds.FileMove => "File move",
                _ => "Details",
            };
        }

        return string.IsNullOrWhiteSpace(item) ? $"{what}: {failure.Reason}" : $"{item}, {what}: {failure.Reason}";
    }

    private static EditTool? ToolFor(EditingSection? section) => section switch
    {
        EditingSection.Artwork => EditTool.Artwork,
        EditingSection.Match => EditTool.Match,
        EditingSection.History => EditTool.Details,
        _ => null,
    };

    private void OnModeChanged()
    {
        Sync();
        Changed?.Invoke();
    }
}
