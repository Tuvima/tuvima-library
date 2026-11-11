using System.Globalization;
using MediaEngine.Domain.Models;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// What a person chose for one file in "What is this file?" (universal editor v4 D20): keep it as an Extra of its own title
/// (<see cref="EditSessionExtraMove.ActionExtra"/>), attach it to another title as an Extra (<see cref="EditSessionExtraMove.ActionAttach"/>),
/// or ignore it (<see cref="EditSessionExtraMove.ActionIgnore"/>).
/// </summary>
/// <param name="Action">One of the <c>Action*</c> constants on <see cref="EditSessionExtraMove"/>.</param>
/// <param name="ExtraKind">An <see cref="ExtraKinds"/> value; null only for ignore.</param>
/// <param name="Season">The season the Extra belongs to (0 is Specials); null for the whole title.</param>
/// <param name="Episode">The episode inside <paramref name="Season"/>; null for the whole season.</param>
/// <param name="Track">The track of an album; null for the whole album. Never set together with a season.</param>
/// <param name="TitleId">The title an attached file goes to; only for attach.</param>
public sealed record ExtraFileChoice(string Action, string? ExtraKind, int? Season, int? Episode, int? Track, Guid? TitleId);

/// <summary>
/// How an <see cref="EditSessionChangeKinds.ExtraMove"/> change names what moves (Extras spec WP4).
/// <list type="bullet">
/// <item><b>Extra to episode:</b> <see cref="EditSessionChange.EntityId"/> is the show being edited, <see cref="EditSessionChange.TargetId"/>
/// is the Extra's id and <see cref="EditSessionChange.Token"/> is <see cref="ToEpisodeToken"/>.</item>
/// <item><b>Episode to Extras:</b> <see cref="EditSessionChange.EntityId"/> is the episode's own id and <see cref="EditSessionChange.Token"/>
/// is <see cref="ToExtrasToken"/>.</item>
/// <item><b>"What is this file?" (universal editor v4 D20):</b> <see cref="EditSessionChange.EntityId"/> is the item whose single file is
/// being sorted and <see cref="EditSessionChange.Token"/> is a <see cref="ToFileToken"/>: the file becomes an Extra of its own title with a
/// kind and place, becomes an Extra of another title, or is ignored.</item>
/// </list>
/// </summary>
public static class EditSessionExtraMove
{
    /// <summary>The token of an episode that moves to Extras.</summary>
    public const string ToExtrasToken = "extras";

    private const string EpisodePrefix = "episode:";

    /// <summary>The start of the token of a "What is this file?" choice (see <see cref="ToFileToken"/>).</summary>
    public const string FilePrefix = "file:";

    /// <summary>Keep the file as an Extra of the title it sits in, with a kind and optional place.</summary>
    public const string ActionExtra = "extra";

    /// <summary>File the file under another title as an Extra of that title.</summary>
    public const string ActionAttach = "attach";

    /// <summary>Hide the file from the Library and future scans; it stays on disk and is listed in Settings with Add back.</summary>
    public const string ActionIgnore = "ignore";

    /// <summary>The highest season and episode numbers the editor accepts.</summary>
    public const int MaxSeason = 99;

    public const int MaxEpisode = 9999;

    /// <summary>The token of an Extra that moves to the given season (0 is Specials) and episode.</summary>
    public static string ToEpisodeToken(int season, int episode) =>
        string.Create(CultureInfo.InvariantCulture, $"{EpisodePrefix}{season}:{episode}");

    public static bool IsToExtras(string? token) =>
        string.Equals(token, ToExtrasToken, StringComparison.Ordinal);

    public static bool IsFileChoice(string? token) =>
        token is not null && token.StartsWith(FilePrefix, StringComparison.Ordinal);

    /// <summary>The token of a "What is this file?" choice, for example <c>file:extra;kind=trailer;season=1</c> or <c>file:ignore</c>.</summary>
    public static string ToFileToken(ExtraFileChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var parts = new List<string> { FilePrefix + choice.Action };
        if (choice.ExtraKind is { Length: > 0 })
        {
            parts.Add("kind=" + choice.ExtraKind);
        }

        AddNumber(parts, "season", choice.Season);
        AddNumber(parts, "episode", choice.Episode);
        AddNumber(parts, "track", choice.Track);
        if (choice.TitleId is { } title)
        {
            parts.Add("title=" + title.ToString("D"));
        }

        return string.Join(';', parts);
    }

    /// <summary>
    /// Reads a <see cref="ToFileToken"/>. False when the token is anything else, names an unknown kind, or holds a place that does not make
    /// sense (an episode without a season, a track with a season, a title on a file that is not attached).
    /// </summary>
    public static bool TryParseFile(string? token, out ExtraFileChoice? choice)
    {
        choice = null;
        if (!IsFileChoice(token))
        {
            return false;
        }

        var parts = token![FilePrefix.Length..].Split(';');
        var action = parts[0];
        string? kind = null;
        int? season = null;
        int? episode = null;
        int? track = null;
        Guid? title = null;
        foreach (var part in parts.Skip(1))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                return false;
            }

            switch (pair[0])
            {
                case "kind":
                    kind = pair[1];
                    break;
                case "season":
                    if (!TryNumber(pair[1], out var seasonNumber)) { return false; }
                    season = seasonNumber;
                    break;
                case "episode":
                    if (!TryNumber(pair[1], out var episodeNumber)) { return false; }
                    episode = episodeNumber;
                    break;
                case "track":
                    if (!TryNumber(pair[1], out var trackNumber)) { return false; }
                    track = trackNumber;
                    break;
                case "title":
                    if (!Guid.TryParseExact(pair[1], "D", out var titleId) || titleId == Guid.Empty) { return false; }
                    title = titleId;
                    break;
                default:
                    return false;
            }
        }

        var valid = action switch
        {
            ActionIgnore => kind is null && season is null && episode is null && track is null && title is null,
            ActionExtra => ExtraKinds.IsKnown(kind) && title is null,
            ActionAttach => ExtraKinds.IsKnown(kind) && title is not null && season is null && episode is null && track is null,
            _ => false,
        };
        valid = valid
            && (season is null || season is >= 0 and <= MaxSeason)
            && (episode is null || (season is not null && episode is >= 1 and <= MaxEpisode))
            && (track is null || (season is null && track is >= 1 and <= MaxEpisode));
        if (!valid)
        {
            return false;
        }

        choice = new ExtraFileChoice(action, kind, season, episode, track, title);
        return true;
    }

    private static void AddNumber(List<string> parts, string name, int? value)
    {
        if (value is { } number)
        {
            parts.Add(name + "=" + number.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>Reads a <see cref="ToEpisodeToken"/>. False when the token is anything else or a number is out of range.</summary>
    public static bool TryParseEpisode(string? token, out int season, out int episode)
    {
        season = 0;
        episode = 0;
        if (token is null || !token.StartsWith(EpisodePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = token[EpisodePrefix.Length..].Split(':');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out season)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out episode)
            && season is >= 0 and <= MaxSeason
            && episode is >= 1 and <= MaxEpisode;
    }
}
