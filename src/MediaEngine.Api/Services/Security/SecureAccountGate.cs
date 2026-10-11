using MediaEngine.Api.Http;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Security;

/// <summary>
/// Who may start without a password: a visitor on this computer or on the home network. Without a password the
/// account never works from outside, and cannot open the door to Anywhere.
/// </summary>
public static class ThisComputerAccess
{
    public static bool IsAvailable(string? clientIngress) => ClientIngress.IsLocal(clientIngress);
}

/// <summary>
/// Keeps an account that only works on this computer from opening the door wider. While the only administrators
/// have no password or passkey yet, anything that would let someone else in is refused with 409
/// <c>secure_account_first</c>.
/// </summary>
public sealed class SecureAccountGate(IUsableAdministratorService administrators)
{
    public const string Code = "secure_account_first";
    public const string Message = "Add a password or passkey to your account first. Until then Tuvima only works on your home network.";

    /// <summary>True while every enabled administrator is a this-computer-only account.</summary>
    public Task<bool> IsLockedAsync(CancellationToken ct) => administrators.OnlyThisComputerAdministratorsAsync(ct);

    /// <summary>True when a change from <paramref name="current"/> to <paramref name="proposed"/> opens the door wider than the home network (the widest a no-password account may use).</summary>
    public static bool Raises(string? current, string? proposed) =>
        WhoCanConnectModes.Rank(proposed) > WhoCanConnectModes.Rank(WhoCanConnectModes.HomeNetwork)
        && WhoCanConnectModes.Rank(proposed) > WhoCanConnectModes.Rank(current);

    public static IResult Refusal() => ApiErrors.Conflict(Code, Message);
}

public static class SecureAccountGateExtensions
{
    /// <summary>Refuses the whole action with 409 <c>secure_account_first</c> while the account is still this-computer-only.</summary>
    public static RouteHandlerBuilder RequireSecuredAccount(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (invocation, next) =>
        {
            var gate = invocation.HttpContext.RequestServices.GetRequiredService<SecureAccountGate>();
            return await gate.IsLockedAsync(invocation.HttpContext.RequestAborted).ConfigureAwait(false)
                ? SecureAccountGate.Refusal()
                : await next(invocation).ConfigureAwait(false);
        });
}
