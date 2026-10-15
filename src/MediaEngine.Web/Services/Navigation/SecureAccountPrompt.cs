using System.Security.Claims;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// The secure-account prompt rules. An account that works only on this computer has no password, so the shell
/// marks the account menu (Secure account item and avatar dot), Settings &gt; Security (dot and first row) and the
/// "add a password first" toast. Nothing is shown as a bar across the top of the screen.
/// </summary>
public static class SecureAccountPrompt
{
    /// <summary>Secure account opens the Security settings, where the password is set.</summary>
    public const string SecureAccountHref = "/settings/security";

    public static bool IsThisComputerOnly(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.FindFirst("tuvima:authentication_method")?.Value == ThisComputerRequests.AuthenticationMethod;
}
