namespace MediaEngine.Web.Components.Listen;

/// <summary>
/// Where a row ends up when a finger drops it before or after another row (spec listen-playlists WP5): the browser's drag only ever drops on a
/// row, but a finger also reports which half of that row it was in, so a row can be dropped after the last one. The same answer serves the song
/// table, the queue and the playlist lists.
/// </summary>
public static class TouchReorderRules
{
    /// <summary>
    /// The place the moved row ends up in once it has left <paramref name="from"/> and been put before (or, with <paramref name="after"/>, after) the row
    /// at <paramref name="target"/>. Both are places in the list as it is now. Returns null when the move changes nothing or a place is outside the list.
    /// </summary>
    public static int? FinalIndex(int from, int target, bool after, int count)
    {
        if (from < 0 || from >= count || target < 0 || target >= count || from == target)
        {
            return null;
        }

        // Dropping before a later row leaves a gap where the moved row was, so it lands one place earlier than that row.
        var slot = after ? target + 1 : target;
        var final = slot > from ? slot - 1 : slot;
        return final == from ? null : final;
    }
}
