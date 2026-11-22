using System.Security.Claims;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// The secure-account prompt rules. An account with no password works on the whole home network (this computer, phones, TVs
/// and tablets), and never from outside it. Not having a password is therefore a normal home setup and is never flagged on
/// its own: it needs doing only once outside access is on (<see cref="SecurityNeedsAttention"/>). The profile menu may still
/// remind the person until they choose Not now (remembered with the account, see <see cref="AccountNoticeState"/>), and
/// Settings &gt; Security keeps its first row and a quiet note. Nothing is shown as a bar across the top of the screen.
/// </summary>
public static class SecureAccountPrompt
{
    /// <summary>Secure account opens the Security settings, where the password is set.</summary>
    public const string SecureAccountHref = "/settings/security";

    public static bool IsThisComputerOnly(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.FindFirst("tuvima:authentication_method")?.Value == ThisComputerRequests.AuthenticationMethod;

    /// <summary>The network connection mode that means nothing outside the home network can reach Library.</summary>
    public const string LocalOnlyConnectionMode = "local-only";

    /// <summary>True when the connection mode lets people reach Library from outside the home network.</summary>
    public static bool OutsideAccessIsOn(string? connectionMode) =>
        !string.IsNullOrWhiteSpace(connectionMode)
        && !string.Equals(connectionMode.Trim(), LocalOnlyConnectionMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The small dot on Security &amp; Devices in the Settings menu: shown only when something really needs doing, which is an
    /// account without a password while outside access is on. A deliberate no-password home setup never raises it.
    /// </summary>
    public static bool SecurityNeedsAttention(bool accountHasNoPassword, bool outsideAccessOn) =>
        accountHasNoPassword && outsideAccessOn;
}
