using MediaEngine.Api.Http;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Security;

/// <summary>
/// Who may start without a password: a visitor on this computer or on the home network. Without a password the
/// account never works from outside.
/// </summary>
public static class ThisComputerAccess
{
    public static bool IsAvailable(string? clientIngress) => ClientIngress.IsLocal(clientIngress);
}

/// <summary>
/// The old "secure your account first" refusal (409 <c>secure_account_first</c>). It no longer refuses anything: the
/// server is open from the start and a no-password account is usable from any device that reaches it (spec
/// open-from-the-start, D5). Kept only for its remaining callers.
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
