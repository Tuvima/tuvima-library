namespace MediaEngine.Web.Components.Listen;

/// <summary>Where a song table is shown. Each place remembers its own columns and widths per profile.</summary>
public enum SongTablePlace
{
    Songs,
    Albums,
    AlbumPage,
    Playlist,
}

/// <summary>
/// The song table's columns, in the one order every place uses (spec music-table-and-reader-frame D3). The order of the members is
/// the order on screen, so Favorite is always right before Time.
/// </summary>
public enum SongColumn
{
    /// <summary>The selection tick. Added by the host when the person can select songs; never in the Columns list.</summary>
    Tick,
    Number,
    Song,
    Artist,
    Album,
    DateAdded,
    Plays,
    Genre,
    Status,
    Favorite,
    Time,
}

/// <summary>One column as drawn: which column and how wide.</summary>
public sealed record SongColumnLayout(SongColumn Column, int Width);

/// <summary>
/// What a profile chose for one place: columns shown or hidden against that place's defaults, and widths changed from the
/// defaults. An empty choice means "use the defaults" (unset); <see cref="SongTableColumns.Resolve"/> applies it.
/// </summary>
public sealed record SongTableChoice
{
    public static SongTableChoice Default { get; } = new();

    /// <summary>Columns the person turned on that are off by default.</summary>
    public IReadOnlySet<SongColumn> Shown { get; init; } = new HashSet<SongColumn>();

    /// <summary>Columns the person turned off that are on by default.</summary>
    public IReadOnlySet<SongColumn> Hidden { get; init; } = new HashSet<SongColumn>();

    /// <summary>Widths the person dragged, in pixels.</summary>
    public IReadOnlyDictionary<SongColumn, int> Widths { get; init; } = new Dictionary<SongColumn, int>();

    public bool IsDefault => Shown.Count == 0 && Hidden.Count == 0 && Widths.Count == 0;
}

/// <summary>
/// The pure rules for the song table's columns: which columns each place has, what starts on, how wide they are, and how a
/// remembered choice is applied. The remembered choice is stored by <see cref="SongTablePreferences"/>.
/// </summary>
public static class SongTableColumns
{
    /// <summary>All columns in on-screen order.</summary>
    public static IReadOnlyList<SongColumn> Order { get; } = Enum.GetValues<SongColumn>();

    public const int MaxWidth = 560;

    /// <summary>The columns a place can offer in its Columns list, in on-screen order. Song is always on and is not in the list.</summary>
    public static IReadOnlyList<SongColumn> Available(SongTablePlace place) =>
        Order.Where(column => IsAvailable(place, column) && column is not (SongColumn.Tick or SongColumn.Song)).ToList();

    /// <summary>
    /// True when the place can show the column at all. The playlist has no Genre, Plays or Date added; the album page has no Album,
    /// Genre, Plays or Date added (every track is on that album, and the Engine sends none of the others); Status is the album page's.
    /// </summary>
    public static bool IsAvailable(SongTablePlace place, SongColumn column) => place switch
    {
        SongTablePlace.AlbumPage => column is not (SongColumn.Album or SongColumn.DateAdded or SongColumn.Plays or SongColumn.Genre),
        SongTablePlace.Playlist => column is not (SongColumn.DateAdded or SongColumn.Plays or SongColumn.Genre or SongColumn.Status),
        _ => column != SongColumn.Status,
    };

    /// <summary>
    /// The columns that start on in a place. Songs and Albums: all but Genre. Album page: #, Song, Time, Status, Favorite, plus Artist only
    /// when the album has more than one artist. Playlist: #, Song, Artist, Album, Favorite, Time. Tick is the host's call and is not here.
    /// </summary>
    public static IReadOnlyList<SongColumn> Defaults(SongTablePlace place, bool albumHasMultipleArtists = false)
    {
        var on = new HashSet<SongColumn> { SongColumn.Number, SongColumn.Song, SongColumn.Favorite, SongColumn.Time };
        switch (place)
        {
            case SongTablePlace.AlbumPage:
                on.Add(SongColumn.Status);
                if (albumHasMultipleArtists)
                {
                    on.Add(SongColumn.Artist);
                }

                break;
            case SongTablePlace.Playlist:
                on.Add(SongColumn.Artist);
                on.Add(SongColumn.Album);
                break;
            default:
                on.Add(SongColumn.Artist);
                on.Add(SongColumn.Album);
                on.Add(SongColumn.DateAdded);
                on.Add(SongColumn.Plays);
                break;
        }

        return Order.Where(on.Contains).ToList();
    }

    /// <summary>The width a column starts at, in pixels. Tick is fixed.</summary>
    public static int DefaultWidth(SongColumn column) => column switch
    {
        SongColumn.Tick => 44,
        SongColumn.Number => 56,
        SongColumn.Song => 360,
        SongColumn.Artist => 180,
        SongColumn.Album => 220,
        SongColumn.DateAdded => 150,
        SongColumn.Plays => 92,
        SongColumn.Genre => 150,
        SongColumn.Status => 110,
        SongColumn.Favorite => 96,
        SongColumn.Time => 88,
        _ => 120,
    };

    public static int MinWidth(SongColumn column) => column switch
    {
        SongColumn.Tick => 44,
        SongColumn.Number => 40,
        SongColumn.Status => 80,
        _ => 64,
    };

    /// <summary>True when the column has a resize handle (every column but the tick).</summary>
    public static bool IsResizable(SongColumn column) => column != SongColumn.Tick;

    public static int ClampWidth(SongColumn column, int width)
        => IsResizable(column) ? Math.Clamp(width, MinWidth(column), MaxWidth) : DefaultWidth(column);

    /// <summary>
    /// The columns to draw, in order, with their widths: the place's defaults changed by the person's choice. Song cannot be hidden and
    /// a column the place does not have cannot be shown. With <paramref name="showTick"/> the selection tick comes first.
    /// </summary>
    public static IReadOnlyList<SongColumnLayout> Resolve(
        SongTablePlace place,
        SongTableChoice? choice = null,
        bool albumHasMultipleArtists = false,
        bool showTick = false)
    {
        choice ??= SongTableChoice.Default;
        var visible = new HashSet<SongColumn>(Defaults(place, albumHasMultipleArtists));

        foreach (var column in choice.Shown.Where(column => IsAvailable(place, column) && column is not SongColumn.Tick))
        {
            visible.Add(column);
        }

        foreach (var column in choice.Hidden.Where(column => column is not (SongColumn.Song or SongColumn.Tick)))
        {
            visible.Remove(column);
        }

        visible.Add(SongColumn.Song);
        if (showTick)
        {
            visible.Add(SongColumn.Tick);
        }

        return Order
            .Where(visible.Contains)
            .Select(column => new SongColumnLayout(
                column,
                choice.Widths.TryGetValue(column, out var width) ? ClampWidth(column, width) : DefaultWidth(column)))
            .ToList();
    }

    /// <summary>True when more than one distinct artist is named on the rows (the album page then offers the Artist column).</summary>
    public static bool HasMultipleArtists(IEnumerable<SongRow> rows)
        => rows.Select(row => row.Artist)
               .Where(artist => !string.IsNullOrWhiteSpace(artist))
               .Select(artist => artist!.Trim())
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .Skip(1)
               .Any();

    /// <summary>The stable name stored for a column; the same names the Songs and playlist tables use today.</summary>
    public static string KeyOf(SongColumn column) => column switch
    {
        SongColumn.Tick => "tick",
        SongColumn.Number => "number",
        SongColumn.Song => "song",
        SongColumn.Artist => "artist",
        SongColumn.Album => "album",
        SongColumn.DateAdded => "dateAdded",
        SongColumn.Plays => "plays",
        SongColumn.Genre => "genre",
        SongColumn.Status => "status",
        SongColumn.Favorite => "favorite",
        SongColumn.Time => "time",
        _ => column.ToString().ToLowerInvariant(),
    };

    public static bool TryParseKey(string? key, out SongColumn column)
    {
        foreach (var candidate in Order)
        {
            if (string.Equals(KeyOf(candidate), key?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                column = candidate;
                return true;
            }
        }

        column = default;
        return false;
    }
}
