using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// How well a show or episode in the Match browser fits what the file name says, as a whole percentage (universal editor v4.1, D5 default).
/// Shows compare names (and the year when both sides have one); episodes compare the season and episode numbers first and the title second.
/// It is only a guide for the person: nothing is chosen from it, and 0 means there is nothing to compare.
/// </summary>
public static class EditorTvMatchScore
{
    public static int Show(string? searched, string? searchedYear, TvMatchBrowserShowDto show)
    {
        if (string.IsNullOrWhiteSpace(searched))
        {
            return 0;
        }

        var score = Similarity(searched, show.Name) * 90d;
        if (!string.IsNullOrWhiteSpace(searchedYear) && !string.IsNullOrWhiteSpace(show.Year))
        {
            score += string.Equals(searchedYear.Trim(), show.Year.Trim(), StringComparison.Ordinal) ? 10 : -15;
        }
        else
        {
            score += Similarity(searched, show.Name) * 10d;
        }

        return Clamp(score);
    }

    public static int Episode(EditorMatchContextDto? context, TvMatchBrowserEpisodeDto episode)
    {
        if (context is null || (context.ParsedEpisode is null && string.IsNullOrWhiteSpace(context.ParsedTitle)))
        {
            return 0;
        }

        double score = 0;
        if (context.ParsedEpisode is { } number && number == episode.EpisodeNumber)
        {
            score += context.ParsedSeason is null || context.ParsedSeason == episode.SeasonNumber ? 70 : 40;
        }

        if (!string.IsNullOrWhiteSpace(context.ParsedTitle))
        {
            score += Similarity(context.ParsedTitle, episode.Title) * 30d;
        }
        else if (score > 0)
        {
            score += 20;
        }

        return Clamp(score);
    }

    /// <summary>The tone of the pill: strong, middling or weak.</summary>
    public static string Band(int percent) => percent >= 85 ? "strong" : percent >= 60 ? "medium" : "weak";

    private static int Clamp(double score) => (int)Math.Round(Math.Clamp(score, 0, 100), MidpointRounding.AwayFromZero);

    private static double Similarity(string a, string b)
    {
        var left = Tokens(a);
        var right = Tokens(b);
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var shared = left.Intersect(right, StringComparer.Ordinal).Count();
        return 2d * shared / (left.Count + right.Count);
    }

    private static HashSet<string> Tokens(string value) =>
        new(new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token is not ("the" or "a" or "an")), StringComparer.Ordinal);
}
