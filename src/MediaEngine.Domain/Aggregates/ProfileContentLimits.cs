using System.Globalization;

namespace MediaEngine.Domain.Aggregates;

/// <summary>
/// The "what can this person watch?" ladder a household administrator sets on every profile, and the one table that
/// turns the many rating vocabularies in a library (film, TV, books, comics, age numbers) into a single step on it.
/// A profile with no limit sees everything; otherwise anything above its step, and (unless allowed) anything with no
/// recognised rating, is kept out of every list, search, detail page and player.
/// </summary>
public static class ProfileContentLimits
{
    /// <summary>The steps a parent can pick, lowest first. Not picking one means "Everything".</summary>
    public static readonly IReadOnlyList<string> Choices = ["G", "PG", "PG-13", "R"];

    /// <summary>The step a new Kids profile starts on.</summary>
    public const string KidsDefault = "PG";

    /// <summary>The rank of "Everything": above every rating, including adults-only ones.</summary>
    public const int NoLimitRank = int.MaxValue;

    private const int Adult = 5;

    /// <summary>
    /// Turns a requested limit into the stored step. A missing, blank or "everything" value means no limit and returns
    /// <see langword="null"/>; an unknown value throws <see cref="ArgumentException"/>.
    /// </summary>
    public static string? Normalize(string? limit)
    {
        if (string.IsNullOrWhiteSpace(limit))
        {
            return null;
        }

        var key = limit.Trim().ToUpperInvariant().Replace('_', '-').Replace(' ', '-');
        if (key == "EVERYTHING")
        {
            return null;
        }

        return Choices.FirstOrDefault(choice => choice == key)
            ?? throw new ArgumentException("Choose Everything, G, PG, PG-13 or R.");
    }

    /// <summary>The rank of a stored limit; no limit ranks above everything.</summary>
    public static int RankOfLimit(string? limit) => limit switch
    {
        "G" => 1,
        "PG" => 2,
        "PG-13" => 3,
        "R" => 4,
        _ => NoLimitRank,
    };

    /// <summary>
    /// The rank of a library rating on the same ladder (1 = all ages, 5 = adults only), or <see langword="null"/> when the
    /// rating is missing or not one this table knows ("Not Rated" and "Unrated" included).
    /// </summary>
    public static int? RankOfRating(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        var key = rating.Trim().ToUpperInvariant().Replace('_', '-').Replace(' ', '-');
        foreach (var prefix in new[] { "US:", "US/", "RATED-" })
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                key = key[prefix.Length..];
            }
        }

        switch (key)
        {
            case "G" or "TV-G" or "TV-Y" or "TV-Y7" or "TV-Y7-FV" or "U" or "UC" or "E" or "EC" or "EVERYONE"
                or "ALL" or "ALL-AGES" or "ALLAGES" or "KIDS":
                return 1;
            case "PG" or "TV-PG" or "E10+" or "EVERYONE-10+":
                return 2;
            case "PG-13" or "TV-14" or "TEEN" or "YA" or "YOUNG-ADULT" or "12A" or "T":
                return 3;
            case "R" or "TV-MA" or "M" or "MATURE" or "MA15+" or "MA-15+":
                return 4;
            case "NC-17" or "X" or "XXX" or "AO" or "ADULT" or "ADULTS" or "ADULTS-ONLY" or "ADULTS-ONLY-18+":
                return Adult;
        }

        // Plain ages, as used on posters and book covers: "12", "15+", "18".
        var digits = key.TrimEnd('+', 'A');
        if (digits.Length is >= 1 and <= 2 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var age))
        {
            return age switch
            {
                <= 6 => 1,
                <= 11 => 2,
                <= 14 => 3,
                <= 17 => 4,
                _ => Adult,
            };
        }

        return null;
    }

    /// <summary>
    /// True when a profile with <paramref name="limit"/> may see an item rated <paramref name="rating"/>.
    /// An unrated or unrecognised rating is allowed only when <paramref name="allowUnrated"/> is set.
    /// </summary>
    public static bool Allows(string? limit, bool allowUnrated, string? rating)
    {
        var cap = RankOfLimit(limit);
        if (cap == NoLimitRank)
        {
            return true;
        }

        return RankOfRating(rating) is { } rank ? rank <= cap : allowUnrated;
    }
}
