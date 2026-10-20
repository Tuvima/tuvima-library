namespace MediaEngine.Contracts.Display;

/// <summary>
/// One page of a shelf's full item list for the "View all" grid, with the sort and filter choices that make sense for it.
/// <see cref="Sort"/> is the order applied; <see cref="Filters"/> only holds groups worth showing (see <see cref="DisplayGridValues"/>).
/// </summary>
public sealed record DisplayGridPageDto(
    string Lane,
    string ShelfKey,
    string Title,
    string? Subtitle,
    IReadOnlyList<DisplayCardDto> Items,
    int TotalCount,
    int Offset,
    int Limit,
    bool HasMore,
    string? NextCursor,
    string Sort,
    string DefaultSort,
    string? RememberKey,
    IReadOnlyList<DisplayGridSortOptionDto> SortOptions,
    IReadOnlyList<DisplayGridFilterGroupDto> Filters);

public sealed record DisplayGridSortOptionDto(string Value, string Label);

public sealed record DisplayGridFilterGroupDto(
    string Key,
    string Label,
    IReadOnlyList<DisplayGridFilterOptionDto> Options);

public sealed record DisplayGridFilterOptionDto(string Value, string Label, int Count, bool Selected);

/// <summary>The fixed vocabulary of the "View all" grid action: sort values, filter keys and values, remember keys and page sizes.</summary>
public static class DisplayGridValues
{
    public const string SortAdded = "added";
    public const string SortTitle = "title";
    public const string SortYear = "year";
    public const string SortPlayed = "played";

    public const string FilterType = "type";
    public const string FilterGenre = "genre";
    public const string FilterFormat = "format";
    public const string FilterStatus = "status";

    public const string StatusNotStarted = "not-started";
    public const string StatusInProgress = "in-progress";
    public const string StatusFinished = "finished";

    public const string TypeMovie = "movie";
    public const string TypeTv = "tv";
    public const string TypeBook = "book";
    public const string TypeComic = "comic";
    public const string TypeAudiobook = "audiobook";
    public const string TypeMusic = "music";
    public const string TypeCollection = "collection";

    public const string FormatEpub = "epub";
    public const string FormatPdf = "pdf";
    public const string FormatComic = "comic";
    public const string Format4K = "4k";
    public const string FormatHd = "hd";
    public const string FormatSd = "sd";

    public const string RememberWatch = "watch";
    public const string RememberRead = "read";
    public const string RememberListen = "listen";
    public const string RememberMyList = "my-list";
    public const string RememberLiked = "liked";

    public const int DefaultPageSize = 48;
    public const int MaxPageSize = 100;
}
