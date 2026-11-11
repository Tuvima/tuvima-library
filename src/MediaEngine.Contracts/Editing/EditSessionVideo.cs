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

    private const string NewPrefix = "new:";

    /// <summary>The longest name a new series may have.</summary>
    public const int MaxSeriesNameLength = 200;

    public static string NewSeriesToken(string name) => NewPrefix + name.Trim();

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
