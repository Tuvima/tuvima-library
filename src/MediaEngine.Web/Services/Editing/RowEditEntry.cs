namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// A row's own Edit (a song's "..." menu on an album page, the Edit button beside a song, the lyrics button in the player): opens the pop-over
/// editor on the page the row is on, ticks that row and opens the Edit panel for it (universal editor spec D18, D19). The page, its scroll
/// position and its filters do not move.
/// </summary>
public static class RowEditEntry
{
    /// <summary>True when the panel is now open for the row; false when the editor is not available here (device, permission) or the selection is full.</summary>
    public static async Task<bool> StartAsync(EditorHost? host, EditorBinding? binding, BulkEditRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (host is null || binding is null || row.Id == Guid.Empty)
        {
            return false;
        }

        if (!host.IsOpen)
        {
            await host.OpenAsync(binding, binding.EntityId, EditTool.Details);
        }

        if (!host.IsOpen || !host.Mode.CanEdit)
        {
            return false;
        }

        // The row may already be ticked (with others): Edit then works on everything ticked, which is what the person sees.
        var selection = host.Batch;
        if (!selection.Contains(row.Id) && !selection.Toggle(row))
        {
            return false;
        }

        selection.OpenPanel(BulkPanelKind.Edit);
        return true;
    }
}
