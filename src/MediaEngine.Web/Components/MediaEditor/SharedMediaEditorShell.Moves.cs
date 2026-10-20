namespace MediaEngine.Web.Components.MediaEditor;

/// <summary>
/// The Move area of Match and Identity (details overlay spec D12 to D17). The area itself (<c>ItemMoveArea</c>) holds the picker, the
/// Match step and the confirmations; the shell only tells it which title is being edited and refreshes after a move.
/// </summary>
public partial class SharedMediaEditorShell
{
    private string? _moveResult;

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

    /// <summary>A move was committed: keep its result line, tell the page behind the section, and show the item as it is now.</summary>
    private async Task OnItemMovedAsync(string resultLine)
    {
        _moveResult = resultLine;
        _hasCommittedChanges = true;
        await LoadSingleItemAsync(CurrentEntityId, resetEditorState: true);
        _tabState.Activate("links");
    }
}
