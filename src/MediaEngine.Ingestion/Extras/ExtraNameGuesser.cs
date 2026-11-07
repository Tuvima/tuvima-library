using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;

namespace MediaEngine.Ingestion.Extras;

/// <summary>What a file name suggests an Extra is and where it belongs. Every part is a guess that only pre-fills "What is this file?".</summary>
/// <param name="ExtraKind">An <see cref="ExtraKinds"/> value that fits the file.</param>
/// <param name="Season">The season the name points at; 0 is Specials. Null when it names none.</param>
/// <param name="Episode">The episode the name points at; only set together with a season.</param>
/// <param name="Track">The track the name points at, for an album.</param>
public sealed record ExtraNameGuess(string ExtraKind, int? Season, int? Episode, int? Track);

/// <summary>
/// Reads a file name (and the name of the folder it sits in) for the kind of Extra it looks like and the season, episode or track it
/// names ("Season 1 trailer.mkv" is a Trailer, Season 1; "Workbook.pdf" is a Document). One rule for every title, no special cases.
/// The words that say what a file is come from <c>config/extras.json</c> (the same ones the scan reads). The answer only pre-fills the
/// picker in the editor: nothing is decided or stored from it.
/// </summary>
public static partial class ExtraNameGuesser
{
    [GeneratedRegex(@"\bS(?<season>\d{1,2})\s*E(?<episode>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\btrack\s*(?<track>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackRegex();

    /// <summary>
    /// The guess for one file. <paramref name="fileKind"/> is an <see cref="ItemExtraKinds"/> value (what opens the file);
    /// <paramref name="parentFolderName"/> is the folder the file sits in, so a file inside "Season 2" points at season 2.
    /// </summary>
    public static ExtraNameGuess Guess(string fileName, string fileKind, string? parentFolderName = null) =>
        Guess(fileName, fileKind, parentFolderName, null);

    /// <summary>The same guess with explicit rules (null means the rules in force).</summary>
    public static ExtraNameGuess Guess(string fileName, string fileKind, string? parentFolderName, ExtrasConfiguration? rules)
    {
        var book = ExtrasRuleBook.For(rules ?? ExtrasConfigurationProvider.Active);
        var stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var words = ExtrasRuleBook.Spaced(stem);

        int? season;
        int? episode = null;
        int? track = null;

        if (SeasonEpisodeRegex().Match(words) is { Success: true } both)
        {
            season = Number(both.Groups["season"]);
            episode = Number(both.Groups["episode"]);
        }
        else
        {
            season = SeasonOf(book, words);
            if (season is not null && book.EpisodeWord.Match(words) is { Success: true } named)
            {
                episode = Number(named.Groups["episode"]);
            }
        }

        if (season is null && !string.IsNullOrWhiteSpace(parentFolderName))
        {
            season = SeasonOf(book, ExtrasRuleBook.Spaced(parentFolderName));
        }

        if (TrackRegex().Match(words) is { Success: true } trackMatch)
        {
            track = Number(trackMatch.Groups["track"]);
        }

        if (track is > 0)
        {
            season = null;
            episode = null;
        }

        return new ExtraNameGuess(KindOf(book, words, fileKind), season, episode is > 0 ? episode : null, track is > 0 ? track : null);
    }

    private static string KindOf(ExtrasRuleBook book, string words, string fileKind)
    {
        if (fileKind != ItemExtraKinds.Clip)
        {
            return ExtraKinds.DefaultFor(fileKind);
        }

        return ExtraRoles.ToExtraKind(book.RoleByWords(words, fileKind) ?? ExtraRoles.Clip, fileKind);
    }

    private static int? SeasonOf(ExtrasRuleBook book, string words)
    {
        if (book.Specials.IsMatch(words))
        {
            return 0;
        }

        var match = book.SeasonWord.Match(words);
        if (!match.Success)
        {
            return null;
        }

        return Number(match.Groups["season"].Success ? match.Groups["season"] : match.Groups["season2"]);
    }

    private static int? Number(Group group) =>
        int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
