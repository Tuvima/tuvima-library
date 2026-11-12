using MediaEngine.Web.Components.Listen;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Reads and writes the sort a profile chose in the phone Songs header (spec music-table-and-reader-frame D21). The choice is one short
/// text, <c>song:asc</c> or <c>plays:desc</c>; unset means the page's own default sort.
/// </summary>
public static class SongSortPreferences
{
    /// <summary>The columns the phone header's Sort by list offers, in the order it shows them.</summary>
    public static IReadOnlyList<SongColumn> PhoneSortColumns { get; } =
    [
        SongColumn.Song,
        SongColumn.Artist,
        SongColumn.Album,
        SongColumn.DateAdded,
        SongColumn.Plays,
        SongColumn.Time,
    ];

    /// <summary>Writes a sort as text. A sort the phone list cannot offer is not stored.</summary>
    public static string? Encode(SongSortState? state)
        => state is { Column: { } column } && PhoneSortColumns.Contains(column)
            ? SongTableColumns.KeyOf(column) + (state.Descending ? ":desc" : ":asc")
            : null;

    /// <summary>Reads the text <see cref="Encode"/> writes; null when it is empty, damaged or names a column the list does not offer.</summary>
    public static SongSortState? Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !SongTableColumns.TryParseKey(parts[0], out var column)
            || !PhoneSortColumns.Contains(column))
        {
            return null;
        }

        return parts[1].ToLowerInvariant() switch
        {
            "asc" => new SongSortState(column, false),
            "desc" => new SongSortState(column, true),
            _ => null,
        };
    }
}
