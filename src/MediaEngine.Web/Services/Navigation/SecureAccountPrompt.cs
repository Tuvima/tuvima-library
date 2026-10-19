using System.Security.Claims;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// The secure-account prompt rules. An account that works only on this computer has no password, so it cannot be
/// used away from home (phones and TVs at home work without one). The profile menu shows a reminder card at its top
/// until the person chooses Not now (remembered with the account, see <see cref="AccountNoticeState"/>); Settings &gt;
/// Security keeps its dot and first row until then and shows a quiet note afterwards; the "add a password first" toast
/// is unchanged. Nothing is shown as a bar across the top of the screen.
/// </summary>
public static class SecureAccountPrompt
{
    /// <summary>Secure account opens the Security settings, where the password is set.</summary>
    public const string SecureAccountHref = "/settings/security";

    public static bool IsThisComputerOnly(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.FindFirst("tuvima:authentication_method")?.Value == ThisComputerRequests.AuthenticationMethod;
}
