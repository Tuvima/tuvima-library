namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// A row's own Edit (a song's "..." menu on an album page, the Edit button beside a song, the lyrics button in the player): starts edit
/// mode on the page the row is on, ticks that row and opens the Edit panel for it (universal editor spec D18, D19). The page, its scroll
/// position and its filters do not move, and nothing opens in a pop-up.
/// </summary>
public static class RowEditEntry
{
    /// <summary>True when the panel is now open for the row; false when edit mode is not available here (device, permission) or the selection is full.</summary>
    public static async Task<bool> StartAsync(EditingModeContext? mode, BulkEditSelection? selection, BulkEditRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (mode is null || selection is null || row.Id == Guid.Empty)
        {
            return false;
        }

        if (!mode.IsOn && !await mode.EnterAsync())
        {
            return false;
        }

        if (!mode.CanEdit)
        {
            return false;
        }

        // The row may already be ticked (with others): Edit then works on everything ticked, which is what the person sees.
        if (!selection.Contains(row.Id) && !selection.Toggle(row))
        {
            return false;
        }

        selection.OpenPanel(BulkPanelKind.Edit);
        return true;
    }
}
