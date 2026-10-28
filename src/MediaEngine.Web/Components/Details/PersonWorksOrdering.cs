using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Components.Details;

/// <summary>The sort choices and quick text filter for a person's "Works in your library" row (spec F22).</summary>
public static class PersonWorksOrdering
{
    public const string Newest = "newest";
    public const string Oldest = "oldest";
    public const string TitleAz = "title";

    public static readonly IReadOnlyList<AppSelectOption> Options =
    [
        new(Newest, "Newest first"),
        new(Oldest, "Oldest first"),
        new(TitleAz, "Title A to Z"),
    ];

    public static string Normalize(string? value) => value switch
    {
        Oldest => Oldest,
        TitleAz => TitleAz,
        _ => Newest,
    };

    public static IReadOnlyList<MediaGroupingItemViewModel> Apply(
        IEnumerable<MediaGroupingItemViewModel> items, string? sort, string? find)
    {
        var query = items;
        var needle = find?.Trim();
        if (!string.IsNullOrEmpty(needle))
        {
            query = query.Where(item =>
                item.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (item.Subtitle?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return Normalize(sort) switch
        {
            Oldest => query.OrderBy(item => YearOf(item) ?? int.MaxValue).ThenBy(TitleKey, StringComparer.OrdinalIgnoreCase).ToList(),
            TitleAz => query.OrderBy(TitleKey, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => query.OrderByDescending(item => YearOf(item) ?? int.MinValue).ThenBy(TitleKey, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    // "The Hobbit" files under H, as a shelf would; otherwise leading articles scatter the A to Z order.
    public static string TitleKey(MediaGroupingItemViewModel item)
    {
        var title = item.Title.Trim();
        foreach (var article in new[] { "the ", "an ", "a " })
        {
            if (title.StartsWith(article, StringComparison.OrdinalIgnoreCase) && title.Length > article.Length)
            {
                return title[article.Length..];
            }
        }

        return title;
    }

    private static int? YearOf(MediaGroupingItemViewModel item)
        => item.Year is { Length: >= 4 } year && int.TryParse(year.AsSpan(0, 4), out var value) ? value : null;
}
