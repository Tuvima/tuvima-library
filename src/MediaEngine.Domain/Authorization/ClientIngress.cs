namespace MediaEngine.Domain.Authorization;

/// <summary>
/// Where a request physically came from, as one shared vocabulary for the Dashboard, the Engine and stored
/// sessions. Anything unknown or missing is <see cref="Remote"/> so the rule always fails closed.
/// </summary>
public static class ClientIngress
{
    public const string ThisComputer = "this_computer";
    public const string HomeNetwork = "home_network";
    public const string Remote = "remote";

    /// <summary>Normalises a wire value; unknown or missing values become <see cref="Remote"/>.</summary>
    public static string Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ThisComputer => ThisComputer,
        HomeNetwork => HomeNetwork,
        _ => Remote,
    };

    /// <summary>How far from the computer the request is: 0 this computer, 1 home network, 2 anywhere else.</summary>
    public static int Rank(string? value) => Parse(value) switch
    {
        ThisComputer => 0,
        HomeNetwork => 1,
        _ => 2,
    };

    /// <summary>True for this computer and the home network.</summary>
    public static bool IsLocal(string? value) => Rank(value) < 2;

    /// <summary>
    /// A session that was made at home or on this computer is only usable from there; a session made from
    /// outside keeps working anywhere (the door rule still applies on top).
    /// </summary>
    public static bool SessionMayContinue(string? issuedIngress, string? currentIngress) =>
        !IsLocal(issuedIngress) || IsLocal(currentIngress);
}
