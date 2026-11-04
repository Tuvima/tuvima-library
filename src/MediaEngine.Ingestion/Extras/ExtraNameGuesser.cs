using System.Globalization;
using System.Text.RegularExpressions;
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
/// The answer only pre-fills the picker in the editor: nothing is decided or stored from it.
/// </summary>
public static partial class ExtraNameGuesser
{
    [GeneratedRegex(@"\b(?:trailers?|teasers?|promos?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailerRegex();

    [GeneratedRegex(@"\bfeaturettes?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeaturetteRegex();

    [GeneratedRegex(@"\b(?:behind the scenes|behindthescenes|bts|making of|the making of)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BehindTheScenesRegex();

    [GeneratedRegex(@"\b(?:deleted scenes?|deleted|outtakes?|bloopers?|gag reel)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeletedSceneRegex();

    [GeneratedRegex(@"\binterviews?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InterviewRegex();

    [GeneratedRegex(@"\bS(?<season>\d{1,2})\s*E(?<episode>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\b(?:season|series|staffel|saison|temporada)\s*(?<season>\d{1,2})\b|\bS(?<season2>\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonRegex();

    [GeneratedRegex(@"\bspecials?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpecialsRegex();

    [GeneratedRegex(@"\b(?:episode|ep)\s*(?<episode>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"\btrack\s*(?<track>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackRegex();

    /// <summary>
    /// The guess for one file. <paramref name="fileKind"/> is an <see cref="ItemExtraKinds"/> value (what opens the file);
    /// <paramref name="parentFolderName"/> is the folder the file sits in, so a file inside "Season 2" points at season 2.
    /// </summary>
    public static ExtraNameGuess Guess(string fileName, string fileKind, string? parentFolderName = null)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var words = Spaced(stem);

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
            season = SeasonOf(words);
            if (season is not null && EpisodeRegex().Match(words) is { Success: true } named)
            {
                episode = Number(named.Groups["episode"]);
            }
        }

        if (season is null && !string.IsNullOrWhiteSpace(parentFolderName))
        {
            season = SeasonOf(Spaced(parentFolderName));
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

        return new ExtraNameGuess(KindOf(words, fileKind), season, episode is > 0 ? episode : null, track is > 0 ? track : null);
    }

    private static string Spaced(string text) => text.Replace('_', ' ').Replace('.', ' ').Replace('-', ' ');

    private static string KindOf(string words, string fileKind)
    {
        if (fileKind != ItemExtraKinds.Clip)
        {
            return ExtraKinds.DefaultFor(fileKind);
        }

        if (TrailerRegex().IsMatch(words))
        {
            return ExtraKinds.Trailer;
        }

        if (FeaturetteRegex().IsMatch(words))
        {
            return ExtraKinds.Featurette;
        }

        if (BehindTheScenesRegex().IsMatch(words))
        {
            return ExtraKinds.BehindTheScenes;
        }

        if (DeletedSceneRegex().IsMatch(words))
        {
            return ExtraKinds.DeletedScene;
        }

        return InterviewRegex().IsMatch(words) ? ExtraKinds.Interview : ExtraKinds.OtherClip;
    }

    private static int? SeasonOf(string words)
    {
        if (SpecialsRegex().IsMatch(words))
        {
            return 0;
        }

        var match = SeasonRegex().Match(words);
        if (!match.Success)
        {
            return null;
        }

        return Number(match.Groups["season"].Success ? match.Groups["season"] : match.Groups["season2"]);
    }

    private static int? Number(Group group) =>
        int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
