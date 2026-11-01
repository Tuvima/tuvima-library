namespace MediaEngine.Api.Services.Collections.ListImport;

/// <summary>The names of the identifiers a list entry can carry. They are the names Library stores them under.</summary>
public static class ListIdentifierKeys
{
    public const string Imdb = "imdb_id";
    public const string Tmdb = "tmdb_id";
    public const string Tvdb = "tvdb_id";
    public const string Isbn = "isbn";
    public const string Asin = "asin";
    public const string MusicBrainz = "musicbrainz_id";
    public const string ComicVine = "comic_vine_id";

    /// <summary>All of them, strongest first: the order a match tries them in.</summary>
    public static readonly IReadOnlyList<string> All = [Imdb, Tmdb, Tvdb, Isbn, Asin, MusicBrainz, ComicVine];
}

/// <summary>The sites a list file can come from (Collections and lists spec 4d).</summary>
public static class ListSourceKinds
{
    public const string Imdb = "imdb";
    public const string Letterboxd = "letterboxd";
    public const string Trakt = "trakt";
    public const string Goodreads = "goodreads";
    public const string StoryGraph = "storygraph";
    public const string Csv = "csv";
    public const string Text = "text";

    // The web sources (Collections and lists spec D7): the list is read from an address through the site's documented API.
    public const string TmdbList = "tmdb_list";
    public const string TmdbCollection = "tmdb_collection";
    public const string MdbList = "mdblist";
    public const string OpenLibrary = "openlibrary";
    public const string MusicBrainz = "musicbrainz";

    /// <summary>True for a kind that is read from a web address, so it can be refreshed from where it came from.</summary>
    public static bool IsWeb(string kind) => kind is TmdbList or TmdbCollection or MdbList or OpenLibrary or MusicBrainz;

    public static string LabelFor(string kind) => kind switch
    {
        TmdbList => "TMDb list",
        TmdbCollection => "TMDb collection",
        MdbList => "MDBList list",
        OpenLibrary => "Open Library list",
        MusicBrainz => "MusicBrainz collection",
        Imdb => "IMDb export",
        Letterboxd => "Letterboxd export",
        Trakt => "Trakt export",
        Goodreads => "Goodreads export",
        StoryGraph => "StoryGraph export",
        Text => "Text list",
        _ => "CSV list",
    };
}

/// <summary>
/// One title as a list file names it, before anything is matched. Identifiers are stored by <see cref="ListIdentifierKeys"/>
/// name; a media type hint narrows the library titles it can match (a Letterboxd row is a film, a Goodreads row is a book).
/// </summary>
public sealed record ParsedListEntry
{
    public int Position { get; init; }
    public string Title { get; init; } = string.Empty;
    public int? Year { get; init; }
    public string? Creator { get; init; }

    /// <summary>The shelves this entry is on (Goodreads and StoryGraph shelves, Letterboxd lists). Empty when the file has none.</summary>
    public IReadOnlyList<string> Shelves { get; init; } = [];

    public IReadOnlyDictionary<string, string> Ids { get; init; } = new Dictionary<string, string>();

    /// <summary>Library media types this entry can be (Movies, TV, Books, Audiobooks...). Empty means any.</summary>
    public IReadOnlyList<string> MediaTypes { get; init; } = [];
}

/// <summary>A list file read into entries.</summary>
public sealed record ParsedList
{
    /// <summary>See <see cref="ListSourceKinds"/>.</summary>
    public string SourceKind { get; init; } = ListSourceKinds.Csv;

    public string SourceLabel => ListSourceKinds.LabelFor(SourceKind);

    /// <summary>The list's own name when the file has one (a Letterboxd list), otherwise empty.</summary>
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<ParsedListEntry> Entries { get; init; } = [];

    /// <summary>The shelves found, in the order they first appear, with how many entries each holds.</summary>
    public IReadOnlyList<(string Name, int Count)> Shelves { get; init; } = [];

    /// <summary>The web address a web list was read from, in its plain form. Null for a file.</summary>
    public string? SourceAddress { get; init; }

    /// <summary>
    /// True when the list is a web list whose site could not be reached: it has no entries yet and the collection is saved to
    /// try again (Collections and lists spec 4h, "Will try again").
    /// </summary>
    public bool WillTryAgain { get; init; }
}

/// <summary>A list file that could not be read. The message is safe to show to the person.</summary>
public class ListFileNotUnderstoodException(string message) : Exception(message)
{
    /// <summary>The sentence the New collection flow shows (Collections and lists spec 4h).</summary>
    public const string StandardMessage =
        "Couldn't read this file. Accepted: CSV from IMDb, Letterboxd, Trakt, Goodreads or StoryGraph, or a text file with one title per line.";

    public ListFileNotUnderstoodException() : this(StandardMessage)
    {
    }
}
