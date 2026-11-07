using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Services.Matching;

/// <summary>The boxes of the Match search (universal editor v5, 3c "Fields by kind"): the id, the words above the box, and the detail they fill.</summary>
public static class EditorMatchFieldIds
{
    public const string Title = "title";
    public const string Year = "year";
    public const string ProviderId = "provider_id";
    public const string Author = "author";
    public const string Isbn = "isbn";
    public const string Series = "series";
    public const string Issue = "issue";
    public const string Show = "show";
    public const string Artist = "artist";
    public const string Album = "album";
    public const string Song = "song";
    public const string AuthorOrNarrator = "author_or_narrator";

    /// <summary>The words above a box.</summary>
    public static string Label(string field) => field switch
    {
        Title => "Title",
        Year => "Year",
        ProviderId => "Provider ID",
        Author => "Author",
        Isbn => "ISBN",
        Series => "Series",
        Issue => "Issue",
        Show => "Show",
        Artist => "Artist",
        Album => "Album",
        Song => "Song",
        AuthorOrNarrator => "Author or narrator",
        _ => field,
    };
}

/// <summary>How a kind of item in the editor maps onto the Library's media type and the retail search's target group.</summary>
/// <param name="MediaType">The media type name the Library uses (<c>Movies</c>, <c>Books</c> ...).</param>
/// <param name="TargetKind">The search's target kind (<c>item</c> or <c>container</c>).</param>
/// <param name="TargetFieldGroup">The search's field group (<c>movie_identity</c>, <c>issue</c>, <c>album</c> ...); empty for a kind with no canonical search.</param>
public sealed record EditorMatchKind(string MediaType, string TargetKind, string TargetFieldGroup);

/// <summary>The kinds of item the Match tab can search for. Any other kind has no provider to match.</summary>
public static class EditorMatchKinds
{
    private static readonly Dictionary<string, EditorMatchKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [EditorKinds.Movie] = new("Movies", "item", "movie_identity"),
        [EditorKinds.Book] = new("Books", "item", "book_identity"),
        [EditorKinds.Audiobook] = new("Audiobooks", "item", "audiobook_identity"),
        [EditorKinds.Comic] = new("Comics", "item", "issue"),
        [EditorKinds.TvShow] = new("TV", "container", "show"),
        [EditorKinds.TvSeason] = new("TV", "container", "show"),
        [EditorKinds.TvEpisode] = new("TV", "item", "show_episode"),
        [EditorKinds.Album] = new("Music", "container", "album"),
        [EditorKinds.Song] = new("Music", "item", "track"),
        [EditorKinds.Video] = new("Videos", "item", string.Empty),
    };

    /// <summary>The mapping of a kind, or null when the kind has no provider to match (a course, a supporting file, a Series).</summary>
    public static EditorMatchKind? For(string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && Kinds.TryGetValue(kind.Trim(), out var found) ? found : null;

    /// <summary>True for the kinds where the search lists works and then their editions or releases (books, comics, audiobooks).</summary>
    public static bool HasEditions(string? kind) =>
        string.Equals(kind, EditorKinds.Book, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, EditorKinds.Comic, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, EditorKinds.Audiobook, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the kinds whose one file's measured length is evidence (a film, an episode, a song, an audiobook, a video).</summary>
    public static bool UsesMeasuredLength(string? kind) =>
        string.Equals(kind, EditorKinds.Movie, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, EditorKinds.TvEpisode, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, EditorKinds.Song, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, EditorKinds.Audiobook, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, EditorKinds.Video, StringComparison.OrdinalIgnoreCase);
}
