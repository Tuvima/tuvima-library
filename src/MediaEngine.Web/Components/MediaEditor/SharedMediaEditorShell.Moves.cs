using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Components.MediaEditor;

/// <summary>
/// The Move area of Match and Identity (details overlay spec D12 to D17). The area itself (<c>ItemMoveArea</c>) holds the picker, the
/// Match step and the confirmations; the shell only tells it which title is being edited and refreshes after a move.
/// </summary>
public partial class SharedMediaEditorShell
{
    private string? _moveResult;

    // The line under the Move area: this editor's own last move, else the one a page carried over when it replaced the page that moved.
    private string? MoveResultLine => _moveResult ?? Request.InitialMoveResult;

    /// <summary>
    /// The item a move acts on: the edited title, or for an audiobook the one part (an Edition) chosen in the selector, or its only one.
    /// An audiobook with several parts and none chosen has no single item to move, so the area stays hidden (empty id).
    /// </summary>
    protected Guid MoveItemId
    {
        get
        {
            if (!string.Equals(EditorMediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase))
            {
                return CurrentEntityId;
            }

            if (_workVersions is null)
            {
                return Guid.Empty;
            }

            if (string.Equals(_workVersions.SelectedEntityType, "Edition", StringComparison.OrdinalIgnoreCase))
            {
                return _workVersions.SelectedEntityId;
            }

            return _workVersions.Editions.Count == 1 ? _workVersions.Editions[0].EditionId : Guid.Empty;
        }
    }

    // Whether the item is Not matched, as the last move left it. The launch request only knows what the page showed when it opened, so
    // after a move the area reads this instead (the Engine's outcome for the item: added anyway, or placed).
    private bool? _unmatchedAfterMove;

    /// <summary>True when the item is shown as Not matched: the launch request's mark until a move has changed it.</summary>
    protected bool IsUnmatchedNow => _unmatchedAfterMove ?? Request.IsUnmatched;

    /// <summary>
    /// A move was committed: keep its result line, show the item as it is now, and tell the page behind the section. A move that merges
    /// the edited title into an entry's own title (or empties its audiobook) deletes the Work the editor was showing: a host showing that
    /// Work is asked to follow to the Work the item now lives in or as, and takes this editor's place on the new page, so the editor does not
    /// re-target; with no host (or a host showing something else) it re-targets itself. A move that leaves the edited Work in place (an
    /// audiobook part joined another audiobook while this one has other parts) keeps the editor on it, reloaded.
    /// </summary>
    private async Task OnItemMovedAsync(ItemMovedArgs moved)
    {
        _moveResult = moved.ResultLine;
        _unmatchedAfterMove = moved.IsUnmatched;
        _hasCommittedChanges = true;
        var previousId = CurrentEntityId;
        var removed = LibraryMoveRules.RemovedEditedWork(
            previousId,
            moved.ItemId,
            moved.ResultEntityId,
            [_workVersions?.WorkTitle, HeaderTitle],
            moved.RemovedContainers);
        var survivingId = removed && moved.ResultEntityId is { } survivor ? survivor : previousId;

        if (removed && Request.OnEditedItemReplaced is { } replaced)
        {
            try
            {
                if (await replaced(previousId, survivingId, moved.ResultLine))
                {
                    // The page behind follows and brings Editing Mode, this section and the line with it; this editor goes with its page.
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "The page could not follow the moved item from {PreviousId} to {TargetId}.", previousId, survivingId);
                Snackbar.Add("The move was saved, but the page could not follow the item. Reload to see it.", AppSeverity.Warning);
            }
        }

        await LoadSingleItemAsync(survivingId, resetEditorState: true, preferredScopeId: ActiveScope?.ScopeId);
        _tabState.Activate("links");
    }
}
