using System.Globalization;
using MediaEngine.Web.Components.Listen;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Reads and writes which song columns a profile shows in each place, and how wide they are (spec music-table-and-reader-frame D4).
/// The choice is kept as one short text per place in a name-to-text map, the same shape the page reader's per-title choices use
/// on a profile; unset means the place's defaults, and Reset removes the entry.
/// </summary>
public static class SongTablePreferences
{
    private const string KeyPrefix = "songTable.";

    /// <summary>The name a place is stored under in the profile's map.</summary>
    public static string KeyFor(SongTablePlace place) => KeyPrefix + place.ToString().ToLowerInvariant();

    /// <summary>What the profile chose for a place; the defaults when nothing is stored or the stored text is not readable.</summary>
    public static SongTableChoice Read(IReadOnlyDictionary<string, string>? store, SongTablePlace place)
        => store is not null && store.TryGetValue(KeyFor(place), out var text) ? Decode(text) : SongTableChoice.Default;

    /// <summary>Remembers a choice for a place. A choice equal to the defaults removes the entry so the profile does not fill up.</summary>
    public static void Write(IDictionary<string, string> store, SongTablePlace place, SongTableChoice choice)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(choice);

        if (choice.IsDefault)
        {
            store.Remove(KeyFor(place));
            return;
        }

        store[KeyFor(place)] = Encode(choice);
    }

    /// <summary>Reset columns: forgets the place's choice so its defaults apply again.</summary>
    public static void Reset(IDictionary<string, string> store, SongTablePlace place)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.Remove(KeyFor(place));
    }

    /// <summary>
    /// The choice after the person ticks or unticks a column in the Columns list. Song cannot be unticked and a column the place does
    /// not have cannot be ticked; going back to the default removes the entry for that column.
    /// </summary>
    public static SongTableChoice WithColumnVisible(
        SongTableChoice choice,
        SongTablePlace place,
        SongColumn column,
        bool visible,
        bool albumHasMultipleArtists = false)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (column is SongColumn.Song or SongColumn.Tick || !SongTableColumns.IsAvailable(place, column))
        {
            return choice;
        }

        var onByDefault = SongTableColumns.Defaults(place, albumHasMultipleArtists).Contains(column);
        var shown = new HashSet<SongColumn>(choice.Shown);
        var hidden = new HashSet<SongColumn>(choice.Hidden);
        shown.Remove(column);
        hidden.Remove(column);

        if (visible && !onByDefault)
        {
            shown.Add(column);
        }
        else if (!visible && onByDefault)
        {
            hidden.Add(column);
        }

        return choice with { Shown = shown, Hidden = hidden };
    }

    /// <summary>The choice after the person drags a column to a new width; a width equal to the default removes the entry.</summary>
    public static SongTableChoice WithWidth(SongTableChoice choice, SongColumn column, int width)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!SongTableColumns.IsResizable(column))
        {
            return choice;
        }

        var widths = new Dictionary<SongColumn, int>(choice.Widths);
        var clamped = SongTableColumns.ClampWidth(column, width);
        if (clamped == SongTableColumns.DefaultWidth(column))
        {
            widths.Remove(column);
        }
        else
        {
            widths[column] = clamped;
        }

        return choice with { Widths = widths };
    }

    /// <summary>Writes a choice as text: <c>show=artist,genre;hide=plays;width=song:400,artist:200</c>.</summary>
    public static string Encode(SongTableChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var parts = new List<string>();

        if (choice.Shown.Count > 0)
        {
            parts.Add("show=" + string.Join(',', Ordered(choice.Shown).Select(SongTableColumns.KeyOf)));
        }

        if (choice.Hidden.Count > 0)
        {
            parts.Add("hide=" + string.Join(',', Ordered(choice.Hidden).Select(SongTableColumns.KeyOf)));
        }

        if (choice.Widths.Count > 0)
        {
            parts.Add("width=" + string.Join(
                ',',
                Ordered(choice.Widths.Keys).Select(column =>
                    SongTableColumns.KeyOf(column) + ":" + choice.Widths[column].ToString(CultureInfo.InvariantCulture))));
        }

        return string.Join(';', parts);
    }

    /// <summary>
    /// Reads the text <see cref="Encode"/> writes. Anything it does not recognise (an unknown section or column, a width that is not a
    /// number, a column that cannot be hidden) is left out, so an old or damaged value falls back toward the defaults.
    /// </summary>
    public static SongTableChoice Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SongTableChoice.Default;
        }

        var shown = new HashSet<SongColumn>();
        var hidden = new HashSet<SongColumn>();
        var widths = new Dictionary<SongColumn, int>();

        foreach (var section in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = section.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = section[..separator].Trim();
            var values = section[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var value in values)
            {
                switch (name.ToLowerInvariant())
                {
                    case "show" when SongTableColumns.TryParseKey(value, out var showColumn) && showColumn is not (SongColumn.Tick or SongColumn.Song):
                        shown.Add(showColumn);
                        break;
                    case "hide" when SongTableColumns.TryParseKey(value, out var hideColumn) && hideColumn is not (SongColumn.Tick or SongColumn.Song):
                        hidden.Add(hideColumn);
                        break;
                    case "width":
                        var pair = value.Split(':', 2, StringSplitOptions.TrimEntries);
                        if (pair.Length == 2
                            && SongTableColumns.TryParseKey(pair[0], out var widthColumn)
                            && SongTableColumns.IsResizable(widthColumn)
                            && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width))
                        {
                            widths[widthColumn] = SongTableColumns.ClampWidth(widthColumn, width);
                        }

                        break;
                }
            }
        }

        // A column cannot be both shown and hidden; a damaged value like that leaves the column at its default.
        var both = shown.Intersect(hidden).ToList();
        shown.ExceptWith(both);
        hidden.ExceptWith(both);

        return new SongTableChoice { Shown = shown, Hidden = hidden, Widths = widths };
    }

    private static IEnumerable<SongColumn> Ordered(IEnumerable<SongColumn> columns)
        => SongTableColumns.Order.Where(columns.ToHashSet().Contains);
}
