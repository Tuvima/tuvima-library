namespace MediaEngine.Domain.Authorization;

/// <summary>
/// Where a request physically came from, as one shared vocabulary for the Dashboard, the Engine and stored
/// sessions. Parse folds unknown values into the home network; a no-password account uses IsOnLocalNetwork/ParseCurrent, which fail closed.
/// </summary>
public static class ClientIngress
{
    public const string ThisComputer = "this_computer";
    public const string HomeNetwork = "home_network";
    public const string Remote = "remote";

    /// <summary>Normalises a wire value; anything other than this computer becomes the home network.</summary>
    public static string Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ThisComputer => ThisComputer,
        _ => HomeNetwork,
    };

    /// <summary>
    /// Where a request is right now, for deciding whether a session may continue. Unlike <see cref="Parse"/> a missing,
    /// blank or unrecognised place is outside (fail closed), so an account that has no password never continues on a guess.
    /// </summary>
    public static string ParseCurrent(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ThisComputer => ThisComputer,
        HomeNetwork => HomeNetwork,
        _ => Remote,
    };

    // Stored places keep the older rule that only this computer is told apart; Rank and IsLocal do not restrict password accounts.
    private static string Fold(string? value) => value?.Trim().ToLowerInvariant() == ThisComputer ? ThisComputer : HomeNetwork;

    /// <summary>How far from the computer the request is: 0 this computer, 1 home network, 2 anywhere else.</summary>
    public static int Rank(string? value) => Fold(value) switch
    {
        ThisComputer => 0,
        HomeNetwork => 1,
        _ => 2,
    };

    /// <summary>True for this computer and the home network.</summary>
    public static bool IsLocal(string? value) => Rank(value) < 2;

    /// <summary>
    /// True only for the computer that runs Tuvima. First-run setup without a password starts here and nowhere else.
    /// </summary>
    public static bool IsThisComputer(string? value) => Parse(value) == ThisComputer;

    /// <summary>
    /// True only when the request is known to come from this computer or from the home network. Unlike
    /// <see cref="Parse"/> it never folds an unknown, missing or "remote" place into the home network: whatever is not
    /// exactly one of the two local places is outside, so the no-password account fails closed. A no-password account
    /// works from any device on the home network (phones, TVs) and never from outside it.
    /// </summary>
    public static bool IsOnLocalNetwork(string? value) => value?.Trim().ToLowerInvariant() is ThisComputer or HomeNetwork;

    /// <summary>
    /// A session that was made at home or on this computer is only usable from there; a session made from
    /// outside keeps working anywhere.
    /// </summary>
    public static bool SessionMayContinue(string? issuedIngress, string? currentIngress) =>
        !IsLocal(issuedIngress) || IsLocal(currentIngress);

    /// <summary>
    /// Same rule for an account that has no password yet: its session may continue only on this computer or the home
    /// network, never from outside it, whatever the session was issued from.
    /// </summary>
    public static bool SessionMayContinue(string? issuedIngress, string? currentIngress, bool accountIsThisComputerOnly) =>
        accountIsThisComputerOnly
            ? IsOnLocalNetwork(currentIngress)
            : SessionMayContinue(issuedIngress, currentIngress);

    /// <summary>Starts the access and refresh tokens of a device approved by a no-password account.</summary>
    public const string LanOnlyTokenPrefix = "lan.";

    /// <summary>A home-network-only token is usable only from this computer or the home network; any other token always is.</summary>
    public static bool TokenMayBeUsedFrom(string? token, string? currentIngress) =>
        token is null || !token.StartsWith(LanOnlyTokenPrefix, StringComparison.Ordinal) || IsOnLocalNetwork(currentIngress);
}
