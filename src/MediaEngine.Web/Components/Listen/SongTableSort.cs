namespace MediaEngine.Web.Components.Listen;

/// <summary>The column the table is sorted by and which way. A null column means the list's own order.</summary>
public sealed record SongSortState(SongColumn? Column, bool Descending);

/// <summary>
/// The pure sorting rules for the song table (spec music-table-and-reader-frame D5): which headers sort, which way a first click goes,
/// and how rows are ordered, including the album page's Unnumbered group and the playlist's saved order.
/// </summary>
public static class SongTableSort
{
    /// <summary>The state a place starts in: Songs and Albums newest first, an album page in track order, a playlist in its saved order.</summary>
    public static SongSortState DefaultFor(SongTablePlace place) => place switch
    {
        SongTablePlace.AlbumPage => new SongSortState(SongColumn.Number, false),
        SongTablePlace.Playlist => new SongSortState(null, false),
        _ => new SongSortState(SongColumn.DateAdded, true),
    };

    /// <summary>
    /// True when the header sorts. A playlist's headers never sort (sorting would fight drag order) unless the list asks for it with
    /// <paramref name="ownOrderSortable"/> (Favorites: any column can sort it, and a third click returns to its own order). The # column
    /// sorts only on an album page, where it is the track number; in a list of songs it is just a position.
    /// </summary>
    public static bool IsSortable(SongTablePlace place, SongColumn column, bool ownOrderSortable = false)
    {
        if (place == SongTablePlace.Playlist)
        {
            return ownOrderSortable && column is not (SongColumn.Tick or SongColumn.Number or SongColumn.Status);
        }

        return column switch
        {
            SongColumn.Tick => false,
            SongColumn.Number or SongColumn.Status => place == SongTablePlace.AlbumPage,
            _ => true,
        };
    }

    /// <summary>True for the columns whose first click goes high to low: Date added, Plays, Time and Favorite.</summary>
    public static bool StartsDescending(SongColumn column)
        => column is SongColumn.DateAdded or SongColumn.Plays or SongColumn.Time or SongColumn.Favorite;

    /// <summary>
    /// What a click on a header does. A new column sorts ascending (descending for <see cref="StartsDescending"/> columns); the sorted
    /// column flips direction. A header that does not sort leaves the state as it was. In a list with its own order
    /// (<paramref name="ownOrderSortable"/>) a click on the column already shown in its second direction goes back to that own order.
    /// </summary>
    public static SongSortState Click(SongTablePlace place, SongSortState current, SongColumn clicked, bool ownOrderSortable = false)
    {
        if (!IsSortable(place, clicked, ownOrderSortable))
        {
            return current;
        }

        if (ownOrderSortable && place == SongTablePlace.Playlist && current.Column == clicked && current.Descending != StartsDescending(clicked))
        {
            return new SongSortState(null, false);
        }

        return current.Column == clicked
            ? current with { Descending = !current.Descending }
            : new SongSortState(clicked, StartsDescending(clicked));
    }

    /// <summary>
    /// The rows in the order the table shows them. A playlist is returned as given. On an album page the numbered tracks are sorted by
    /// the chosen column and the Unnumbered group always follows them, by title, whatever the sort. A list with its own order that can
    /// also be sorted (<paramref name="ownOrderSortable"/>) is returned as given until a column is chosen.
    /// </summary>
    /// <param name="favoriteIds">The ids (<see cref="SongRow.Id"/>) the person has saved to Favorites; used when sorting by Favorite.</param>
    public static IReadOnlyList<SongRow> Sort(
        IEnumerable<SongRow> rows,
        SongTablePlace place,
        SongSortState state,
        IReadOnlySet<string>? favoriteIds = null,
        bool ownOrderSortable = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var all = rows.ToList();

        if (place == SongTablePlace.Playlist)
        {
            return ownOrderSortable ? SortRows(all, state, favoriteIds) : all;
        }

        if (place != SongTablePlace.AlbumPage)
        {
            return SortRows(all, state, favoriteIds);
        }

        return
        [
            .. SortRows(all.Where(row => !row.IsUnnumbered), state, favoriteIds),
            .. all.Where(row => row.IsUnnumbered).OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// True when the phone list groups under letter headings with an A to Z jump strip: Songs and Albums lists sorted by Song or Artist
    /// (spec music-table-and-reader-frame D21). An album page and a playlist keep their own order.
    /// </summary>
    public static bool ShowsLetters(SongTablePlace place, SongSortState state)
        => place is SongTablePlace.Songs or SongTablePlace.Albums && state.Column is SongColumn.Song or SongColumn.Artist;

    /// <summary>
    /// The heading letter a song sits under: its first letter, upper case with accents dropped (É is E), or # for a number, a symbol
    /// or nothing.
    /// </summary>
    public static string LetterFor(string? text)
    {
        var trimmed = text?.TrimStart();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "#";
        }

        var first = char.ToUpperInvariant(trimmed[..1].Normalize(System.Text.NormalizationForm.FormD)[0]);
        return first is >= 'A' and <= 'Z' ? first.ToString() : "#";
    }

    private static List<SongRow> SortRows(IEnumerable<SongRow> rows, SongSortState state, IReadOnlySet<string>? favoriteIds)
    {
        if (state.Column is not { } column)
        {
            return rows.ToList();
        }

        var descending = state.Descending;
        var ordered = column switch
        {
            SongColumn.Song => Order(rows, row => row.Title, descending),
            SongColumn.Artist => Order(rows, row => row.Artist ?? string.Empty, descending),
            SongColumn.Album => Order(rows, row => row.Album ?? string.Empty, descending).ThenBy(row => row.Number ?? int.MaxValue),
            SongColumn.Genre => Order(rows, row => row.Genre ?? string.Empty, descending),
            SongColumn.Number => Order(rows, row => row.Number ?? int.MaxValue, descending),
            SongColumn.Plays => Order(rows, row => row.Plays, descending),
            SongColumn.Time => Order(rows, row => row.DurationSeconds ?? 0d, descending),
            SongColumn.DateAdded => Order(rows, row => row.DateAdded ?? DateTimeOffset.MinValue, descending),
            // Ascending puts owned tracks first, as the album page does today.
            SongColumn.Status => Order(rows, row => row.IsOwned ? 0 : 1, descending),
            SongColumn.Favorite => Order(rows, row => favoriteIds?.Contains(row.Id) == true ? 1 : 0, descending),
            _ => rows.OrderBy(_ => 0),
        };

        // Rows that tie keep a stable, readable order: by title, then by id.
        return ordered
            .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static IOrderedEnumerable<SongRow> Order<TKey>(IEnumerable<SongRow> rows, Func<SongRow, TKey> key, bool descending)
        => descending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    private static IOrderedEnumerable<SongRow> Order(IEnumerable<SongRow> rows, Func<SongRow, string> key, bool descending)
        => descending
            ? rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(key, StringComparer.OrdinalIgnoreCase);
}
