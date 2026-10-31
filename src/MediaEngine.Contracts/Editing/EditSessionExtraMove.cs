using System.Globalization;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// How an <see cref="EditSessionChangeKinds.ExtraMove"/> change names what moves (Extras spec WP4).
/// <list type="bullet">
/// <item><b>Extra to episode:</b> <see cref="EditSessionChange.EntityId"/> is the show being edited, <see cref="EditSessionChange.TargetId"/>
/// is the Extra's id and <see cref="EditSessionChange.Token"/> is <see cref="ToEpisodeToken"/>.</item>
/// <item><b>Episode to Extras:</b> <see cref="EditSessionChange.EntityId"/> is the episode's own id and <see cref="EditSessionChange.Token"/>
/// is <see cref="ToExtrasToken"/>.</item>
/// </list>
/// </summary>
public static class EditSessionExtraMove
{
    /// <summary>The token of an episode that moves to Extras.</summary>
    public const string ToExtrasToken = "extras";

    private const string EpisodePrefix = "episode:";

    /// <summary>The highest season and episode numbers the editor accepts.</summary>
    public const int MaxSeason = 99;

    public const int MaxEpisode = 9999;

    /// <summary>The token of an Extra that moves to the given season (0 is Specials) and episode.</summary>
    public static string ToEpisodeToken(int season, int episode) =>
        string.Create(CultureInfo.InvariantCulture, $"{EpisodePrefix}{season}:{episode}");

    public static bool IsToExtras(string? token) =>
        string.Equals(token, ToExtrasToken, StringComparison.Ordinal);

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
