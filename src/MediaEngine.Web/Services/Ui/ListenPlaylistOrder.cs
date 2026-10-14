namespace MediaEngine.Web.Services.Ui;

/// <summary>
/// Position maths for moving one playlist row up or down by a step. The row menu and Alt+Arrow keys use it,
/// then save through the same order command the drag handlers use.
/// </summary>
public static class ListenPlaylistOrder
{
    /// <summary>
    /// Returns a copy of <paramref name="items"/> with the entry at <paramref name="sourceIndex"/> moved by
    /// <paramref name="delta"/> places, or <see langword="null"/> when the move would leave the list.
    /// </summary>
    public static List<T>? Move<T>(IReadOnlyList<T> items, int sourceIndex, int delta)
    {
        var targetIndex = sourceIndex + delta;
        if (sourceIndex < 0 || sourceIndex >= items.Count || targetIndex < 0 || targetIndex >= items.Count || delta == 0)
        {
            return null;
        }

        var reordered = items.ToList();
        var moved = reordered[sourceIndex];
        reordered.RemoveAt(sourceIndex);
        reordered.Insert(targetIndex, moved);
        return reordered;
    }
}
