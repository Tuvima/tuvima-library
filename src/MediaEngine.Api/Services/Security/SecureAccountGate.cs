using MediaEngine.Api.Http;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Security;

/// <summary>
/// Who may start without a password: only a visitor on the computer that runs Tuvima. Anyone on the home network, and
/// anyone outside it, signs in with a password or passkey (home network spec D8).
/// </summary>
public static class ThisComputerAccess
{
    public static bool IsAvailable(string? clientIngress) => ClientIngress.IsThisComputer(clientIngress);
}

/// <summary>
/// The old "secure your account first" refusal (409 <c>secure_account_first</c>). It no longer refuses anything: the
/// server is open from the start. A no-password account works only on the computer that runs Tuvima (home network
/// spec D8). Kept only for its remaining callers.
/// </summary>
public sealed class SecureAccountGate
{
    public const string Code = "secure_account_first";
    public const string Message = "Add a password or passkey to your account first.";

    /// <summary>True while every enabled administrator is a this-computer-only account.</summary>
    public Task<bool> IsLockedAsync(CancellationToken ct) => Task.FromResult(false);

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
