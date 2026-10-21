using MediaEngine.Contracts.Display;

namespace MediaEngine.Api.Services.Display;

/// <summary>Where a "View all" grid's full item list comes from. Each one mirrors the shelf of the same key.</summary>
internal enum DisplayGridSource
{
    HomeWatchNext,
    HomeReadNext,
    HomeListenNext,
    HomeContinue,
    ReadContinue,
    ReadRecentlyAdded,
    ReadAuthor,
    WatchContinue,
    WatchTvShows,
    WatchVideos,
    WatchMovies,
    WatchGenre,
    ListenContinue,
    ListenRecentlyPlayed,
    ListenAlbums,
    ListenAudiobooks,
    MyList,
    Liked,
}

/// <summary>One grid: which shelf it is the full list of, how it opens, and how it is remembered.</summary>
/// <param name="Preset">For author and genre grids, the stable key of the author or genre the list is narrowed to.</param>
internal sealed record DisplayGridDefinition(
    string Lane,
    string ShelfKey,
    DisplayGridSource Source,
    string Title,
    string? Subtitle,
    string DefaultSort,
    string? RememberKey,
    string? Preset = null)
{
    /// <summary>True when the grid's first rows are what this profile played last, so progress filters add nothing.</summary>
    public bool IsPlayedDefault => DefaultSort == DisplayGridValues.SortPlayed;

    public string Route => DisplayGridCatalog.RouteFor(Lane, ShelfKey);
}

/// <summary>The sort, filter and paging choices of one grid request, as the caller sent them.</summary>
public sealed record DisplayGridQuery(
    string? Sort = null,
    string? Type = null,
    string? Genre = null,
    string? Format = null,
    string? Status = null,
    int Offset = 0,
    int Limit = DisplayGridValues.DefaultPageSize);

internal static class DisplayGridCatalog
{
    public const string PersonalLane = "personal";
    public const string HomeLane = "home";
    private const string AuthorPrefix = "author-";
    private const string GenrePrefix = "genre-";

    public static readonly IReadOnlyList<DisplayGridSortOptionDto> SortOptions =
    [
        new(DisplayGridValues.SortAdded, "Recently added"),
        new(DisplayGridValues.SortTitle, "Title A–Z"),
        new(DisplayGridValues.SortYear, "Year"),
        new(DisplayGridValues.SortPlayed, "Recently played"),
    ];

    private static readonly IReadOnlyList<DisplayGridDefinition> Fixed =
    [
        new(HomeLane, "continue", DisplayGridSource.HomeContinue, "Continue Across Media", "Pick up where you left off", DisplayGridValues.SortPlayed, null),
        new(HomeLane, "watch-next", DisplayGridSource.HomeWatchNext, "Watch", "Movies and shows ready to play", DisplayGridValues.SortAdded, DisplayGridValues.RememberWatch),
        new(HomeLane, "read-next", DisplayGridSource.HomeReadNext, "Read", "Books and comics ready to open", DisplayGridValues.SortAdded, DisplayGridValues.RememberRead),
        new(HomeLane, "listen-next", DisplayGridSource.HomeListenNext, "Listen", "Music and audiobooks ready to resume", DisplayGridValues.SortAdded, DisplayGridValues.RememberListen),
        new("read", "continue-reading", DisplayGridSource.ReadContinue, "Continue reading", "Books and comics already in motion", DisplayGridValues.SortPlayed, null),
        new("read", "recently-added", DisplayGridSource.ReadRecentlyAdded, "Recently added to read", "Fresh pages and issues ready to pick up", DisplayGridValues.SortAdded, DisplayGridValues.RememberRead),
        new("watch", "continue-watching", DisplayGridSource.WatchContinue, "Continue watching", "Movies and shows already in progress", DisplayGridValues.SortPlayed, null),
        new("watch", "tv-shows", DisplayGridSource.WatchTvShows, "TV Shows", "Shows built from the episodes you own", DisplayGridValues.SortAdded, DisplayGridValues.RememberWatch),
        new("watch", "videos", DisplayGridSource.WatchVideos, "Videos", "Downloads, courses and clips ready to play", DisplayGridValues.SortAdded, DisplayGridValues.RememberWatch),
        new("watch", "movies", DisplayGridSource.WatchMovies, "Movies in your library", "Feature films ready to play", DisplayGridValues.SortAdded, DisplayGridValues.RememberWatch),
        new("listen", "continue-listening", DisplayGridSource.ListenContinue, "Continue Listening", "Resume audiobooks already in progress", DisplayGridValues.SortPlayed, null),
        new("listen", "recently-played", DisplayGridSource.ListenRecentlyPlayed, "Recently Played", "Albums from your latest music sessions", DisplayGridValues.SortPlayed, null),
        new("listen", "recently-updated-albums", DisplayGridSource.ListenAlbums, "Recently Updated Albums", "Albums that received newly added tracks", DisplayGridValues.SortAdded, DisplayGridValues.RememberListen),
        new("listen", "new-tracks-added", DisplayGridSource.ListenAlbums, "New tracks added", "Recently added tracks grouped by album", DisplayGridValues.SortAdded, DisplayGridValues.RememberListen),
        new("listen", "albums", DisplayGridSource.ListenAlbums, "Albums", "Album-first browsing with cover art at the center", DisplayGridValues.SortAdded, DisplayGridValues.RememberListen),
        new("listen", "recently-added-audiobooks", DisplayGridSource.ListenAudiobooks, "Recently Added Audiobooks", "New spoken-word titles in your library", DisplayGridValues.SortAdded, DisplayGridValues.RememberListen),
        new(PersonalLane, "my-list", DisplayGridSource.MyList, "My List", "Saved for later", DisplayGridValues.SortAdded, DisplayGridValues.RememberMyList),
        new(PersonalLane, "liked", DisplayGridSource.Liked, "Liked", "Things you have liked", DisplayGridValues.SortAdded, DisplayGridValues.RememberLiked),
    ];

    /// <summary>
    /// The grid for a lane and shelf key, or null when there is none. Author and genre grids exist for any key; whether
    /// anything matches is decided when the list is built (an empty author or genre is not a grid).
    /// </summary>
    public static DisplayGridDefinition? Find(string? lane, string? shelfKey)
    {
        if (string.IsNullOrWhiteSpace(lane) || string.IsNullOrWhiteSpace(shelfKey))
        {
            return null;
        }

        var normalizedLane = lane.Trim().ToLowerInvariant();
        var key = shelfKey.Trim().ToLowerInvariant();
        var fixedGrid = Fixed.FirstOrDefault(grid => grid.Lane == normalizedLane && grid.ShelfKey == key);
        if (fixedGrid is not null)
        {
            return fixedGrid;
        }

        if (normalizedLane == "read" && key.Length > AuthorPrefix.Length && key.StartsWith(AuthorPrefix, StringComparison.Ordinal))
        {
            return new DisplayGridDefinition(normalizedLane, key, DisplayGridSource.ReadAuthor, "Author", null,
                DisplayGridValues.SortAdded, DisplayGridValues.RememberRead, key[AuthorPrefix.Length..]);
        }

        if (normalizedLane == "watch" && key.Length > GenrePrefix.Length && key.StartsWith(GenrePrefix, StringComparison.Ordinal))
        {
            return new DisplayGridDefinition(normalizedLane, key, DisplayGridSource.WatchGenre, "Genre", null,
                DisplayGridValues.SortAdded, DisplayGridValues.RememberWatch, key[GenrePrefix.Length..]);
        }

        return null;
    }

    public static string RouteFor(string lane, string shelfKey) => $"/all/{lane}/{shelfKey}";

    public static string? NormalizeSort(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return null;
        }

        var value = sort.Trim().ToLowerInvariant();
        return SortOptions.Any(option => option.Value == value) ? value : throw new DisplayGridQueryException($"'{sort}' is not a sort this grid offers.");
    }

    public static string? NormalizeType(string? type) => NormalizeOneOf(type, "type",
        DisplayGridValues.TypeMovie, DisplayGridValues.TypeTv, DisplayGridValues.TypeVideo, DisplayGridValues.TypeBook, DisplayGridValues.TypeComic,
        DisplayGridValues.TypeAudiobook, DisplayGridValues.TypeMusic, DisplayGridValues.TypeCollection);

    public static string? NormalizeFormat(string? format) => NormalizeOneOf(format, "format",
        DisplayGridValues.FormatEpub, DisplayGridValues.FormatPdf, DisplayGridValues.FormatComic,
        DisplayGridValues.Format4K, DisplayGridValues.FormatHd, DisplayGridValues.FormatSd);

    public static string? NormalizeStatus(string? status) => NormalizeOneOf(status, "status",
        DisplayGridValues.StatusNotStarted, DisplayGridValues.StatusInProgress, DisplayGridValues.StatusFinished);

    private static string? NormalizeOneOf(string? value, string name, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return allowed.Contains(normalized) ? normalized : throw new DisplayGridQueryException($"'{value}' is not a {name} this grid offers.");
    }
}
