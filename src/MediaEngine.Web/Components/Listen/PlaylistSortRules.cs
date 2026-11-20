namespace MediaEngine.Web.Components.Listen;

/// <summary>What a playlist page can sort its songs by (spec listen-playlists, WP4): Title, Artist, Album, Date added and Length.</summary>
public enum PlaylistSortKey
{
    Title,
    Artist,
    Album,
    DateAdded,
    Length,
}

/// <summary>
/// Puts a playlist's songs in the order a Sort choice gives. A sort is a preview: the page stages the order and "Keep this order" saves it
/// as the playlist's own order. It reuses the song table's column rules, so a song sorts the same way here as in the Songs list.
/// </summary>
public static class PlaylistSortRules
{
    /// <summary>The choices in the order the Sort menu lists them.</summary>
    public static IReadOnlyList<PlaylistSortKey> All { get; } =
        [PlaylistSortKey.Title, PlaylistSortKey.Artist, PlaylistSortKey.Album, PlaylistSortKey.DateAdded, PlaylistSortKey.Length];

    /// <summary>The table column a Sort choice stands for.</summary>
    public static SongColumn ColumnFor(PlaylistSortKey key) => key switch
    {
        PlaylistSortKey.Artist => SongColumn.Artist,
        PlaylistSortKey.Album => SongColumn.Album,
        PlaylistSortKey.DateAdded => SongColumn.DateAdded,
        PlaylistSortKey.Length => SongColumn.Time,
        _ => SongColumn.Song,
    };

    /// <summary>The Sort choice a table column stands for; null for a column no choice uses.</summary>
    public static PlaylistSortKey? KeyFor(SongColumn column) => All.Cast<PlaylistSortKey?>().FirstOrDefault(key => ColumnFor(key!.Value) == column);

    /// <summary>
    /// What choosing <paramref name="key"/> does: a new choice sorts the way its column first sorts in the song table (Date added newest first,
    /// the others from the start); choosing the choice already applied flips the direction.
    /// </summary>
    public static SongSortState Choose(SongSortState? current, PlaylistSortKey key)
    {
        var column = ColumnFor(key);
        return current is { } now && now.Column == column
            ? now with { Descending = !now.Descending }
            : new SongSortState(column, SongTableSort.StartsDescending(column));
    }

    /// <summary>The songs' playlist item ids in the order the sort gives; rows with no playlist item id are left out.</summary>
    public static IReadOnlyList<Guid> Order(IEnumerable<SongRow> rows, SongSortState state)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. SongTableSort.Sort(rows, SongTablePlace.Songs, state)
            .Where(row => row.PlaylistItemId.HasValue)
            .Select(row => row.PlaylistItemId!.Value)];
    }
}
