namespace MediaEngine.Contracts.Editing;

/// <summary>
/// How a <see cref="EditSessionChangeKinds.VideoSeries"/> change names where the video goes (universal editor v3.1).
/// <list type="bullet">
/// <item><b>Join a series or section:</b> <see cref="EditSessionChange.TargetId"/> is the series or section id.</item>
/// <item><b>Start a new series:</b> <see cref="EditSessionChange.Token"/> is <see cref="NewSeriesToken"/>.</item>
/// <item><b>Stand alone:</b> <see cref="EditSessionChange.Token"/> is <see cref="NoSeriesToken"/>.</item>
/// </list>
/// </summary>
public static class EditSessionVideo
{
    /// <summary>The token of a video that leaves its series.</summary>
    public const string NoSeriesToken = "none";

    /// <summary>
    /// How a <see cref="EditSessionChangeKinds.FileMove"/> change says it belongs to a series change (universal editor v6, D24): its token is
    /// this prefix and the <see cref="ChoiceKey"/> of the series choice it was shown with, and its <see cref="EditSessionChange.TargetId"/> is
    /// the id of that series change. It only lets Review and the footer say the file will move. The series change itself moves the file, first,
    /// and puts everything back if anything fails; this change then has nothing left to do. The Engine moves the file only when the pair
    /// still agrees (same video, same series change, same choice) and refuses the save otherwise.
    /// </summary>
    public const string MovesWithSeriesPrefix = "with_series:";

    private const string NewPrefix = "new:";

    /// <summary>The longest name a new series may have.</summary>
    public const int MaxSeriesNameLength = 200;

    public static string NewSeriesToken(string name) => NewPrefix + name.Trim();

    /// <summary>The token of the file move that goes with a series change that has this target and token.</summary>
    public static string MovesWithSeries(string? seriesTargetId, string? seriesToken) =>
        MovesWithSeriesPrefix + ChoiceKey(seriesTargetId, seriesToken);

    /// <summary>True when <paramref name="token"/> marks a file move as belonging to a series change.</summary>
    public static bool IsMovesWithSeries(string? token) =>
        token is not null && token.StartsWith(MovesWithSeriesPrefix, StringComparison.Ordinal);

    /// <summary>Reads the series choice a file move was shown with. False when the token is not a series move's.</summary>
    public static bool TryReadMovesWithSeries(string? token, out string choiceKey)
    {
        choiceKey = string.Empty;
        if (!IsMovesWithSeries(token))
        {
            return false;
        }

        choiceKey = token![MovesWithSeriesPrefix.Length..];
        return true;
    }

    /// <summary>
    /// A short, comparable name for where a series change puts the video: <c>none</c>, <c>new:Name</c> or the series id.
    /// Two series changes with the same key do the same thing.
    /// </summary>
    public static string ChoiceKey(string? targetId, string? token)
    {
        if (IsNoSeries(token))
        {
            return NoSeriesToken;
        }

        if (TryParseNewSeries(token, out var name))
        {
            return NewPrefix + name;
        }

        return Guid.TryParse(targetId, out var id) ? id.ToString("D") : string.Empty;
    }

    public static bool IsNoSeries(string? token) => string.Equals(token, NoSeriesToken, StringComparison.Ordinal);

    /// <summary>Reads a <see cref="NewSeriesToken"/>. False when the token is anything else or the name is empty.</summary>
    public static bool TryParseNewSeries(string? token, out string name)
    {
        name = string.Empty;
        if (token is null || !token.StartsWith(NewPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        name = token[NewPrefix.Length..].Trim();
        return name.Length > 0;
    }
}
